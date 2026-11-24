using Dictate.Core.Transcription;
using GrokVoice.Platform;

namespace GrokVoice.Bot;

/// <summary>
/// Sends a Copilot key instruction to the main bot through the Grok Bot API. When no bot has been
/// picked yet, the default (see <see cref="DefaultBot"/>) is used and remembered.
/// </summary>
internal sealed class BotSender(DictationHost host) : IBotSender
{
    public GrokBotClient Client { get; } = new();

    public string? BotName => host.Settings.BotName is { Length: > 0 } name ? name : null;

    public void Choose(GrokBot? bot)
    {
        host.Settings.BotId = bot?.Id ?? "";
        host.Settings.BotName = bot?.Name ?? "";
    }

    /// <summary>The bot to use when none has been picked: the only one, or Jeeves (Aaron's main bot).</summary>
    public static GrokBot? DefaultBot(IReadOnlyList<GrokBot> bots) =>
        bots.Count == 1 ? bots[0]
        : bots.FirstOrDefault(b => b.Name.StartsWith("Jeeves", StringComparison.OrdinalIgnoreCase));

    public async Task SendAsync(BotMessage message, CancellationToken ct)
    {
        if (!Client.IsSignedIn)
            throw new TranscriptionException("Sign in to Grok Bot under Main bot in Murmur's settings first.");

        var id = host.Settings.BotId;
        if (id.Length == 0)
        {
            var bots = await Client.ListBotsAsync(ct).ConfigureAwait(false);
            var main = DefaultBot(bots) ?? throw new TranscriptionException(bots.Count == 0
                ? "Your Grok Bot account has no bots yet."
                : "Pick your main bot under Main bot in Murmur's settings.");
            Choose(main);
            id = main.Id;
        }
        var (text, paths, names) = await UploadAsync(id, message, ct).ConfigureAwait(false);
        var messageId = await Client.SendAsync(id, text, ct, paths, names).ConfigureAwait(false);
        _ = WatchAsync(id, messageId);
    }

    // Uploads the attachments; if any fails, the message goes with its content written inline instead.
    private async Task<(string Text, IReadOnlyList<string> Paths, IReadOnlyList<string> Names)> UploadAsync(
        string botId, BotMessage message, CancellationToken ct)
    {
        if (message.Attachments.Count == 0) return (message.Text, [], []);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var paths = new List<string>();
        try
        {
            foreach (var file in message.Attachments)
                paths.Add(await Client.UploadAttachmentAsync(botId, file.Name, file.Data, ct).ConfigureAwait(false));
            GrokVoice.Log.Write($"bot   attached {string.Join(", ", message.Attachments.Select(a => $"{a.Name} ({a.Data.Length / 1024} KB)"))} in {clock.ElapsedMilliseconds} ms");
            return (message.Text, paths, message.Attachments.Select(a => a.Name).ToList());
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            GrokVoice.Log.Write($"bot   couldn't attach files, sending the screen as text: {e.Message}");
            return (message.Inline, [], []);
        }
    }

    /// <summary>The bot's reply text, on a background thread.</summary>
    public event Action<string, string>? Replied;
    /// <summary>The bot gave no reply in time; argument is the bot's name.</summary>
    public event Action<string>? NoReply;

    private CancellationTokenSource? _watch;

    // Follows only the latest instruction; a new one stops watching the previous one.
    private async Task WatchAsync(string botId, string messageId)
    {
        _watch?.Cancel();
        var cts = _watch = new CancellationTokenSource();
        var name = BotName ?? "Your bot";
        var any = false;
        try
        {
            await Client.WatchRepliesAsync(botId, messageId, reply =>
            {
                any = true;
                Replied?.Invoke(name, reply);
            }, quietAfterReply: TimeSpan.FromSeconds(30), giveUpAfter: TimeSpan.FromMinutes(5), cts.Token).ConfigureAwait(false);
            if (!any) NoReply?.Invoke(name);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            GrokVoice.Log.Write($"bot   couldn't watch for the reply: {e.Message}");
        }
    }
}

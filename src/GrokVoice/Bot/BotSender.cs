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

    public async Task SendAsync(string text, CancellationToken ct)
    {
        if (!Client.IsSignedIn)
            throw new TranscriptionException("Sign in to Grok Bot under Main bot in Grok Voice's settings first.");

        var id = host.Settings.BotId;
        if (id.Length == 0)
        {
            var bots = await Client.ListBotsAsync(ct).ConfigureAwait(false);
            var main = DefaultBot(bots) ?? throw new TranscriptionException(bots.Count == 0
                ? "Your Grok Bot account has no bots yet."
                : "Pick your main bot under Main bot in Grok Voice's settings.");
            Choose(main);
            id = main.Id;
        }
        var messageId = await Client.SendAsync(id, text, ct).ConfigureAwait(false);
        _ = WatchAsync(id, messageId);
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

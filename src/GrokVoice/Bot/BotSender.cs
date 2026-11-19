using Dictate.Core.Transcription;
using GrokVoice.Platform;

namespace GrokVoice.Bot;

/// <summary>
/// Sends a Copilot key instruction to the main bot through the Grok Bot API. When no bot has been
/// picked yet and the account has only one, that one is used and remembered.
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

    public async Task SendAsync(string text, CancellationToken ct)
    {
        if (!Client.IsSignedIn)
            throw new TranscriptionException("Sign in to Grok Bot under Main bot in Grok Voice's settings first.");

        var id = host.Settings.BotId;
        if (id.Length == 0)
        {
            var bots = await Client.ListBotsAsync(ct).ConfigureAwait(false);
            if (bots.Count != 1)
                throw new TranscriptionException(bots.Count == 0
                    ? "Your Grok Bot account has no bots yet."
                    : "Pick your main bot under Main bot in Grok Voice's settings.");
            Choose(bots[0]);
            id = bots[0].Id;
        }
        await Client.SendAsync(id, text, ct).ConfigureAwait(false);
    }
}

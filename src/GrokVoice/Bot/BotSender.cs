using Dictate.Core.Transcription;
using GrokVoice.Platform;

namespace GrokVoice.Bot;

/// <summary>
/// Sends a Copilot key instruction to the main bot. Placeholder until the Grok Bot API is confirmed
/// on Windows: it explains that the bot isn't connected yet.
/// </summary>
internal sealed class BotSender(DictationHost host) : IBotSender
{
    public string? BotName => host.Settings.BotName is { Length: > 0 } name ? name : null;

    public Task SendAsync(string text, CancellationToken ct) =>
        throw new TranscriptionException("Grok Bot isn't connected yet. Sign in under Main bot in Grok Voice's settings.");
}

using Dictate.Core.Dictation;

namespace GrokVoice.Platform;

/// <summary>Where a dictation goes: typed at the cursor (Right Alt), or sent to the main bot (Copilot key).</summary>
internal enum DictationTarget
{
    Cursor,
    Bot,
}

/// <summary>Sends an instruction to the user's main bot.</summary>
internal interface IBotSender
{
    /// <summary>The bot's name for messages, or null when no bot is set up yet.</summary>
    string? BotName { get; }
    Task SendAsync(string text, CancellationToken ct);
}

/// <summary>
/// The controller's one text sink, pointing at the cursor or the bot. The target is chosen when a
/// dictation starts and kept until its text has been delivered, even if the other key is pressed.
/// </summary>
internal sealed class RoutingTextSink(KeyboardTextSink keyboard, IBotSender bot, Func<string, CancellationToken, Task<string>> polish,
    Func<string, string>? forBot = null) : ITextSink
{
    private DictationTarget _next = DictationTarget.Cursor;
    private DictationTarget _current = DictationTarget.Cursor;

    public KeyboardTextSink Keyboard { get; } = keyboard;

    /// <summary>The target for the dictation about to start.</summary>
    public DictationTarget Next
    {
        set => _next = value;
    }

    /// <summary>The target of the dictation in progress (or the last one).</summary>
    public DictationTarget Current => _current;

    public void RememberTarget()
    {
        _current = _next;
        // Remember the window either way: the pill appears on its screen.
        Keyboard.RememberTarget();
    }

    /// <summary>The text last delivered, after clean-up.</summary>
    public string LastDelivered { get; private set; } = "";

    public async Task InsertAsync(string text, CancellationToken ct)
    {
        text = await polish(text, ct).ConfigureAwait(false);
        LastDelivered = text;
        await (_current switch
        {
            DictationTarget.Bot => bot.SendAsync(forBot?.Invoke(text) ?? text, ct),
            _ => Keyboard.InsertAsync(text, ct),
        }).ConfigureAwait(false);
    }
}

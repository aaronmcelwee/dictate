namespace GrokVoice;

/// <summary>
/// Diagnostics for code that runs off the UI thread or has no host to hand (the launcher, the
/// keyboard hook). The host points <see cref="Sink"/> at its log.
/// </summary>
internal static class Log
{
    public static Action<string>? Sink { get; set; }

    public static void Write(string line) => Sink?.Invoke(line);
}

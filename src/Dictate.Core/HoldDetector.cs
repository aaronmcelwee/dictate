namespace Dictate.Core;

/// <summary>
/// Turns a key's raw presses and releases into the Tap / HoldStart / HoldEnd events that
/// <see cref="KeyStateMachine"/> understands, with our own hold threshold. Used for keys we read
/// ourselves with a keyboard hook (Right Alt, and the Copilot key on key-down) instead of waiting
/// for Windows to decide.
/// Thread-safe; the hold timer fires on a thread-pool thread.
/// </summary>
public sealed class HoldDetector : IDisposable
{
    public static readonly TimeSpan DefaultHoldThreshold = TimeSpan.FromMilliseconds(250);

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _threshold;
    private ITimer? _timer;
    private bool _down;
    private bool _holding;
    private int _press;

    public HoldDetector(TimeProvider? time = null, TimeSpan? holdThreshold = null)
    {
        _time = time ?? TimeProvider.System;
        _threshold = holdThreshold ?? DefaultHoldThreshold;
    }

    /// <summary>Raised for Tap, HoldStart and HoldEnd, on the thread that caused it (the timer's for HoldStart).</summary>
    public event Action<KeyEvent>? Gesture;
    /// <summary>The key went down (before anyone knows if it's a tap or a hold), on the caller's thread.</summary>
    public event Action? Pressed;

    public bool IsDown
    {
        get { lock (_gate) return _down; }
    }

    /// <summary>The key went down. Repeats while it's already down are ignored.</summary>
    public void Down()
    {
        lock (_gate)
        {
            if (_down) return;
            _down = true;
            _holding = false;
            var press = ++_press;
            _timer?.Dispose();
            _timer = _time.CreateTimer(_ => OnThreshold(press), null, _threshold, Timeout.InfiniteTimeSpan);
        }
        Pressed?.Invoke();
    }

    /// <summary>The key came up.</summary>
    public void Up()
    {
        KeyEvent? e;
        lock (_gate)
        {
            if (!_down) return;
            _down = false;
            _timer?.Dispose();
            _timer = null;
            e = _holding ? KeyEvent.HoldEnd : KeyEvent.Tap;
            _holding = false;
        }
        Gesture?.Invoke(e.Value);
    }

    /// <summary>
    /// Forgets the current press without reporting anything, e.g. when the key turned out to be part
    /// of a shortcut. A hold already reported still gets its HoldEnd so dictation isn't left running.
    /// </summary>
    public void Abandon()
    {
        bool endHold;
        lock (_gate)
        {
            if (!_down) return;
            endHold = _holding;
            _down = false;
            _holding = false;
            _timer?.Dispose();
            _timer = null;
        }
        if (endHold) Gesture?.Invoke(KeyEvent.HoldEnd);
    }

    private void OnThreshold(int press)
    {
        lock (_gate)
        {
            if (!_down || _holding || press != _press) return;
            _holding = true;
            _timer?.Dispose();
            _timer = null;
        }
        Gesture?.Invoke(KeyEvent.HoldStart);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}

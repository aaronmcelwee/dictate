using System.Runtime.InteropServices;

namespace GrokVoice.Input;

/// <summary>
/// Reads Right Alt and the Copilot key the moment they go down or up, with a low-level keyboard
/// hook on its own thread (Windows drops hooks that answer slowly, so callbacks must only hand off).
/// <para>
/// Right Alt is swallowed while it's down on its own. If another key is pressed before a hold
/// starts, it was a shortcut after all: Right Alt and that key are replayed so the shortcut works.
/// </para>
/// <para>
/// The Copilot key arrives as Win + Shift + F23. The hook swallows F23, its repeats and its release,
/// and reports the press with whether Win and Shift already got through, so they can be let go of.
/// Their physical releases are swallowed later.
/// </para>
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private const uint WM_REPLAY = 0x8000 + 0x0010; // wParam = the key that showed Right Alt was a shortcut.

    private readonly Action<string> _log;
    private readonly Thread _thread;
    private readonly HookProc _proc; // Held so the delegate is not collected.
    private readonly ManualResetEventSlim _started = new();
    private uint _threadId;
    private IntPtr _hook;

    // Touched only on the hook thread.
    private bool _copilotDown;
    private bool _winDown, _shiftDown, _owedWinUp, _owedShiftUp;
    private bool _altDown, _altPassThrough;

    public KeyboardHook(Action<string> log)
    {
        _log = log;
        _proc = HookCallback;
        _thread = new Thread(Run) { IsBackground = true, Name = "Keyboard hook" };
        _thread.Start();
        _started.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Whether Right Alt is used for dictation. When off, it passes through untouched.</summary>
    public volatile bool RightAltEnabled = true;

    /// <summary>Whether a Right Alt hold has started; then other keys pass through without replaying Right Alt.</summary>
    public volatile bool RightAltHolding;

    public event Action? RightAltDown;
    public event Action? RightAltUp;
    /// <summary>Right Alt turned out to be part of a shortcut; forget the press.</summary>
    public event Action? RightAltAbandoned;
    /// <summary>(let go of Win, let go of Shift).</summary>
    public event Action<bool, bool>? CopilotDown;
    public event Action? CopilotUp;

    public bool IsActive => _hook != IntPtr.Zero;

    /// <summary>When the hook last saw the Copilot key go down (Environment.TickCount64), or 0.</summary>
    public long LastCopilotDown { get; private set; }

    public bool CopilotHeld => _copilotDown;

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) _log($"error keyboard hook not installed ({Marshal.GetLastWin32Error()}); Right Alt won't work");
        _started.Set();
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_REPLAY && msg.hwnd == IntPtr.Zero)
            {
                InjectedKeys.SendDown(InjectedKeys.RMenu);
                InjectedKeys.SendDown((ushort)msg.wParam);
                continue;
            }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var injected = (info.flags & LLKHF_INJECTED) != 0;
            var up = wParam == WM_KEYUP || wParam == WM_SYSKEYUP;
            if (!injected)
            {
                try
                {
                    if (Handle((ushort)info.vkCode, up)) return 1;
                }
                catch (Exception e)
                {
                    _log($"error in keyboard hook: {e.Message}");
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    // True to swallow the key.
    private bool Handle(ushort vk, bool up)
    {
        switch (vk)
        {
            case InjectedKeys.RMenu:
                return HandleRightAlt(up);

            case InjectedKeys.F23 when !up:
                if (_copilotDown) return true; // Auto-repeat.
                _copilotDown = true;
                LastCopilotDown = Environment.TickCount64;
                var releaseWin = _winDown;
                var releaseShift = _shiftDown;
                _owedWinUp |= releaseWin;
                _owedShiftUp |= releaseShift;
                _winDown = _shiftDown = false;
                CopilotDown?.Invoke(releaseWin, releaseShift);
                return true;

            case InjectedKeys.F23:
                if (!_copilotDown) return false;
                _copilotDown = false;
                CopilotUp?.Invoke();
                return true;

            case InjectedKeys.LWin:
                if (!up) { _winDown = true; return false; }
                _winDown = false;
                if (!_owedWinUp) return false;
                _owedWinUp = false;
                return true;

            case InjectedKeys.LShift:
                if (!up) { _shiftDown = true; return false; }
                _shiftDown = false;
                if (!_owedShiftUp) return false;
                _owedShiftUp = false;
                return true;

            default:
                // Another key while Right Alt is down on its own: it's a shortcut, not dictation.
                if (!up && _altDown && !_altPassThrough && !RightAltHolding)
                {
                    _altPassThrough = true;
                    RightAltAbandoned?.Invoke();
                    PostThreadMessage(_threadId, WM_REPLAY, (IntPtr)vk, IntPtr.Zero);
                    return true;
                }
                return false;
        }
    }

    private bool HandleRightAlt(bool up)
    {
        if (!up)
        {
            if (_altDown) return !_altPassThrough; // Auto-repeat.
            if (!RightAltEnabled) return false;
            _altDown = true;
            _altPassThrough = false;
            RightAltDown?.Invoke();
            return true;
        }

        if (!_altDown) return false;
        _altDown = false;
        if (_altPassThrough)
        {
            _altPassThrough = false;
            return false; // The replayed Right Alt down needs its release.
        }
        RightAltUp?.Invoke();
        return true;
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint WM_QUIT = 0x0012;
    private const uint LLKHF_INJECTED = 0x10;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
}

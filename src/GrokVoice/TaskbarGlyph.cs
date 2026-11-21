using System.Runtime.InteropServices;
using Dictate.Core.Glyph;
using Microsoft.Win32;

namespace GrokVoice;

/// <summary>
/// Murmur's one piece of resting UI: a 40 x 40 glyph in the empty part of the taskbar, left of Start
/// (where Widgets would sit), or left of the tray when the taskbar is left-aligned.
/// <para>
/// It is a plain Win32 layered window (no XAML) that never activates, drawn by <see cref="TaskbarGlyphModel"/>.
/// Being topmost over the taskbar, it re-asserts its place whenever the foreground changes and on a
/// short timer, and it steps aside for full-screen apps or a hidden taskbar.
/// </para>
/// </summary>
internal sealed class TaskbarGlyph : IDisposable
{
    private const string ClassName = "MurmurTaskbarGlyph";
    private const double GapDip = 12;
    private const uint AnimateTimer = 1, PlaceTimer = 2, HoverTimer = 3;

    private readonly TaskbarGlyphModel _model = new();
    private readonly WndProc _proc;
    private readonly WinEventProc _winEvent;
    private readonly IntPtr _hwnd;
    private readonly IntPtr _eventHook;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double _lastFrame;
    private bool _animating, _tracking, _visible, _enabled = true;
    private RECT _bounds;
    private double _scale = 1;

    public TaskbarGlyph()
    {
        _proc = Proc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandle(null),
            lpszClassName = ClassName,
            hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW),
        };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
            ClassName, "Murmur", WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        // Dark context menus that match the taskbar.
        try { SetPreferredAppMode(1); FlushMenuThemes(); } catch { }

        // The taskbar or another topmost window coming forward can cover the glyph: put it back.
        _winEvent = (_, _, _, _, _, _, _) =>
        {
            try { Place(); }
            catch (Exception e) { Error?.Invoke($"glyph place: {e}"); }
        };
        _eventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _winEvent, 0, 0, WINEVENT_OUTOFCONTEXT);
        SetTimer(_hwnd, (IntPtr)PlaceTimer, 500, IntPtr.Zero);
        try
        {
            Place();
            Draw();
            Error?.Invoke($"glyph window {_hwnd:X} at {_bounds.Left},{_bounds.Top} size {_bounds.Right - _bounds.Left}, showing {_visible}");
        }
        catch (Exception e)
        {
            Error?.Invoke($"glyph start: {e}");
        }
    }

    public IntPtr Handle => _hwnd;

    /// <summary>On screen now (enabled, taskbar visible, no full-screen app).</summary>
    public bool IsShowing => _visible;

    /// <summary>The glyph's screen rectangle in physical pixels, for anchoring flyouts above it.</summary>
    public (int Left, int Top, int Right, int Bottom) Bounds => (_bounds.Left, _bounds.Top, _bounds.Right, _bounds.Bottom);

    /// <summary>Left click.</summary>
    public event Action? Clicked;
    /// <summary>The pointer rested on the glyph (true) or left it (false), for the tooltip.</summary>
    public event Action<bool>? Hovering;
    /// <summary>Right click, with the screen point, for the menu.</summary>
    public event Action<int, int>? MenuRequested;

    /// <summary>Whether the glyph shows at all (the "Show in taskbar" setting).</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            Place();
        }
    }

    public GlyphMode Mode
    {
        get => _model.Mode;
        set
        {
            if (_model.Mode == value) return;
            _model.Mode = value;
            Animate();
        }
    }

    public void SetLevel(float peak)
    {
        _model.SetLevel(peak);
        Animate();
    }

    private void Animate()
    {
        if (_animating) return;
        _animating = true;
        _lastFrame = _clock.Elapsed.TotalMilliseconds;
        SetTimer(_hwnd, (IntPtr)AnimateTimer, 15, IntPtr.Zero);
        Frame();
    }

    private void Frame()
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        var more = _model.Step(Math.Min(100, now - _lastFrame), now / 1000);
        _lastFrame = now;
        Draw();
        if (!more)
        {
            KillTimer(_hwnd, (IntPtr)AnimateTimer);
            _animating = false;
        }
    }

    /// <summary>Finds the spot on the primary taskbar, follows the theme, and shows or hides the glyph.</summary>
    private void Place()
    {
        var bar = FindWindow("Shell_TrayWnd", null);
        var show = _enabled && bar != IntPtr.Zero && GetWindowRect(bar, out var tray) && !FullScreenApp();
        if (show)
        {
            GetWindowRect(bar, out tray);
            var dpi = GetDpiForWindow(bar);
            var scale = dpi > 0 ? dpi / 96.0 : 1;
            var monitor = MonitorFromWindow(bar, MONITOR_DEFAULTTOPRIMARY);
            var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(monitor, ref info);
            var height = tray.Bottom - tray.Top;
            var width = tray.Right - tray.Left;
            // Only a bottom taskbar that is actually on screen (not auto-hidden away).
            var onScreen = width > height && tray.Top >= info.rcMonitor.Top
                && tray.Bottom <= info.rcMonitor.Bottom + 2 && tray.Top < info.rcMonitor.Bottom - height / 2;
            if (!onScreen) show = false;
            else
            {
                var size = (int)Math.Round(TaskbarGlyphModel.PlateDip * scale);
                var gap = (int)Math.Round(GapDip * scale);
                var left = SpotLeft(bar, tray, size, gap);
                var top = tray.Top + (height - size) / 2;
                var themeChanged = UpdateTheme();
                if (left != _bounds.Left || top != _bounds.Top || scale != _scale || themeChanged)
                {
                    _bounds = new RECT { Left = left, Top = top, Right = left + size, Bottom = top + size };
                    _scale = scale;
                    Draw();
                }
            }
        }

        if (show)
        {
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            _visible = true;
        }
        else if (_visible)
        {
            ShowWindow(_hwnd, SW_HIDE);
            _visible = false;
        }
    }

    /// <summary>
    /// Where the glyph goes: with a centred taskbar, 12 DIPs in from the left edge (Widgets' spot), or
    /// just left of Start when Widgets is there; with a left-aligned taskbar, just left of the tray.
    /// </summary>
    private static int SpotLeft(IntPtr bar, RECT tray, int size, int gap)
    {
        var advanced = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
        var centred = advanced?.GetValue("TaskbarAl") is not int al || al != 0;
        var widgets = advanced?.GetValue("TaskbarDa") is not int da || da != 0;
        if (!centred)
        {
            var notify = FindWindowEx(bar, IntPtr.Zero, "TrayNotifyWnd", null);
            if (notify != IntPtr.Zero && GetWindowRect(notify, out var n)) return n.Left - size - gap / 3;
            return tray.Right - size * 8;
        }
        if (widgets)
        {
            var start = FindWindowEx(bar, IntPtr.Zero, "Start", null);
            if (start != IntPtr.Zero && GetWindowRect(start, out var s) && s.Left - tray.Left > size + gap)
                return s.Left - size - gap / 3;
        }
        return tray.Left + gap;
    }

    private bool _light;

    private bool UpdateTheme()
    {
        var personalize = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var light = personalize?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        if (light == _light && _model.Palette == (light ? GlyphPalette.Light : GlyphPalette.Dark)) return false;
        _light = light;
        _model.Palette = light ? GlyphPalette.Light : GlyphPalette.Dark;
        Animate();
        return true;
    }

    /// <summary>A full-screen app, game or presentation is up: keep off it.</summary>
    private static bool FullScreenApp()
    {
        if (SHQueryUserNotificationState(out var state) == 0
            && state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE)
            return true;
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == GetShellWindow() || fg == GetDesktopWindow()) return false;
        var cls = new System.Text.StringBuilder(64);
        GetClassName(fg, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        if (!GetWindowRect(fg, out var r)) return false;
        var monitor = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;
        return r.Left <= info.rcMonitor.Left && r.Top <= info.rcMonitor.Top
            && r.Right >= info.rcMonitor.Right && r.Bottom >= info.rcMonitor.Bottom;
    }

    private byte[] _pixels = [];

    private void Draw()
    {
        var size = _bounds.Right - _bounds.Left;
        if (size <= 0) return;
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var header = new BITMAPINFO
        {
            // biSize is the header alone (40 bytes), not the header plus the colour table.
            biSize = 40, biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32,
        };
        var bitmap = CreateDIBSection(screen, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
        {
            Error?.Invoke($"glyph bitmap {size} px failed (error {Marshal.GetLastWin32Error()})");
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
            return;
        }
        var old = SelectObject(memory, bitmap);
        if (_pixels.Length != size * size * 4) _pixels = new byte[size * size * 4];
        _model.Render(_pixels, size, _scale);
        Marshal.Copy(_pixels, 0, bits, _pixels.Length);
        var position = new POINT { X = _bounds.Left, Y = _bounds.Top };
        var dims = new SIZE { cx = size, cy = size };
        var source = new POINT();
        var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
        UpdateLayeredWindow(_hwnd, screen, ref position, ref dims, memory, ref source, 0, ref blend, ULW_ALPHA);
        SelectObject(memory, old);
        DeleteObject(bitmap);
        DeleteDC(memory);
        ReleaseDC(IntPtr.Zero, screen);
    }

    /// <summary>Raised when something in the glyph fails; it keeps going, so Murmur does too.</summary>
    public static Action<string>? Error { get; set; }

    private IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            return HandleMessage(hwnd, msg, wParam, lParam);
        }
        catch (Exception e)
        {
            Error?.Invoke($"glyph {msg:X}: {e}");
            return DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    private IntPtr HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                return (IntPtr)MA_NOACTIVATE;
            case WM_TIMER when wParam == (IntPtr)AnimateTimer:
                Frame();
                return IntPtr.Zero;
            case WM_TIMER when wParam == (IntPtr)PlaceTimer:
                Place();
                return IntPtr.Zero;
            case WM_TIMER when wParam == (IntPtr)HoverTimer:
                KillTimer(_hwnd, (IntPtr)HoverTimer);
                if (_model.Hover) Hovering?.Invoke(true);
                return IntPtr.Zero;
            case WM_MOUSEMOVE:
                if (!_tracking)
                {
                    var track = new TRACKMOUSEEVENT { cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = TME_LEAVE, hwndTrack = hwnd };
                    TrackMouseEvent(ref track);
                    _tracking = true;
                    _model.Hover = true;
                    Animate();
                    // Tooltip after the usual pause.
                    SetTimer(_hwnd, (IntPtr)HoverTimer, 500, IntPtr.Zero);
                }
                return IntPtr.Zero;
            case WM_MOUSELEAVE:
                _tracking = false;
                _model.Hover = false;
                KillTimer(_hwnd, (IntPtr)HoverTimer);
                Animate();
                Hovering?.Invoke(false);
                return IntPtr.Zero;
            case WM_LBUTTONUP:
                KillTimer(_hwnd, (IntPtr)HoverTimer);
                Hovering?.Invoke(false);
                Clicked?.Invoke();
                return IntPtr.Zero;
            case WM_RBUTTONUP:
                KillTimer(_hwnd, (IntPtr)HoverTimer);
                Hovering?.Invoke(false);
                GetCursorPos(out var p);
                MenuRequested?.Invoke(p.X, p.Y);
                return IntPtr.Zero;
            case WM_DISPLAYCHANGE or WM_DPICHANGED or WM_SETTINGCHANGE:
                Place();
                break;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Shows a native menu at the pointer and returns the chosen item's id, or 0. Items are
    /// (id, text, isChecked); an id of 0 is a separator.
    /// </summary>
    public int ShowMenu(int x, int y, IReadOnlyList<(int Id, string Text, bool Checked)> items)
    {
        var menu = CreatePopupMenu();
        foreach (var (id, text, check) in items)
        {
            if (id == 0) AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, null);
            else AppendMenu(menu, MF_STRING | (check ? MF_CHECKED : 0), (IntPtr)id, text);
        }
        // A menu closes properly on an outside click only when its owner is in the foreground. This is
        // a click on the glyph, so no key hold can be disturbed by it.
        SetForegroundWindow(_hwnd);
        var chosen = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_BOTTOMALIGN | TPM_LEFTALIGN | TPM_NONOTIFY, x, _bounds.Top - 4, _hwnd, IntPtr.Zero);
        PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);
        return chosen;
    }

    public void Dispose()
    {
        if (_eventHook != IntPtr.Zero) UnhookWinEvent(_eventHook);
        KillTimer(_hwnd, (IntPtr)PlaceTimer);
        KillTimer(_hwnd, (IntPtr)AnimateTimer);
        DestroyWindow(_hwnd);
    }

    // ---- Win32 ----

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] private struct TRACKMOUSEEVENT { public uint cbSize, dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
        public uint bmiColors;
    }

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_LAYERED = 0x00080000, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x00000008;
    private const uint WM_NULL = 0, WM_TIMER = 0x0113, WM_MOUSEMOVE = 0x0200, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205;
    private const uint WM_MOUSELEAVE = 0x02A3, WM_MOUSEACTIVATE = 0x0021, WM_DISPLAYCHANGE = 0x007E, WM_DPICHANGED = 0x02E0, WM_SETTINGCHANGE = 0x001A;
    private const int MA_NOACTIVATE = 3;
    private const uint TME_LEAVE = 2;
    private const uint ULW_ALPHA = 2;
    private const int SW_HIDE = 0;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1, MONITOR_DEFAULTTONEAREST = 2;
    private const uint EVENT_SYSTEM_FOREGROUND = 3, WINEVENT_OUTOFCONTEXT = 0;
    private const int QUNS_BUSY = 2, QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4;
    private const uint MF_STRING = 0, MF_SEPARATOR = 0x800, MF_CHECKED = 8;
    private const uint TPM_RETURNCMD = 0x100, TPM_BOTTOMALIGN = 0x20, TPM_LEFTALIGN = 0, TPM_NONOTIFY = 0x80;
    private static readonly IntPtr IDC_ARROW = new(32512);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd, IntPtr id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, ref POINT pos, ref SIZE size, IntPtr src, ref POINT srcPos, uint key, ref BLENDFUNCTION blend, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, IntPtr id, string? text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("uxtheme.dll", EntryPoint = "#135")] private static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] private static extern void FlushMenuThemes();
}

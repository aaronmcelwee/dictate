using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GrokVoice;

/// <summary>
/// The small always-on-top capsule shown while dictating. It never takes focus, so the
/// text still goes to the app the user was in.
/// <para>
/// The window is shown once at startup and then only moved on and off screen. Showing a window
/// while the Copilot key is held makes Windows end the hold about 50 ms in and replay the key as
/// taps, so push-to-talk falls apart.
/// </para>
/// </summary>
public sealed partial class RecordingPill : Window
{
    /// <summary>What the pill is listening for, which sets its badge.</summary>
    public enum ListenMode { Cursor, HandsFree, Bot }

    private const double HeightDip = 36;
    private const double ReplyHeightDip = 60;
    private const double BottomMarginDip = 28;
    private const double MaxWidthDip = 400;
    private const int BarCount = 11;
    private static readonly TimeSpan ResizeTime = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan FadeTime = TimeSpan.FromMilliseconds(140);

    private readonly IntPtr _hwnd;
    private readonly DispatcherQueueTimer _hideTimer;
    private readonly DispatcherQueueTimer _frame;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly Microsoft.UI.Xaml.Shapes.Rectangle[] _bars = new Microsoft.UI.Xaml.Shapes.Rectangle[BarCount];
    private readonly double[] _barHeight = new double[BarCount];
    private readonly double[] _barTarget = new double[BarCount];
    private readonly double[] _envelope = new double[BarCount];
    private readonly Random _random = new();
    private readonly TextBlock _measure = new() { FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };

    private enum Look { Listening, Transcribing, Message }
    private Look _look;
    private ListenMode _mode;
    private float _level;
    private bool _onScreen;
    private bool _hiding;

    // Window geometry, in DIPs, animated between sizes around a fixed bottom-centre point.
    private double _scale = 1;
    private Windows.Graphics.RectInt32 _work;
    private Windows.Foundation.Size _size, _fromSize, _toSize;
    private double _resizeStart, _fadeStart;

    public RecordingPill()
    {
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        ExtendsContentIntoTitleBar = true;

        var presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        var exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        // The capsule shape comes from a window region, so Windows must not add its own corners.
        var corners = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));

        for (var i = 0; i < BarCount; i++)
        {
            var x = (i - (BarCount - 1) / 2.0) / ((BarCount - 1) / 2.0);
            _envelope[i] = 0.35 + 0.65 * Math.Exp(-x * x * 2.2);
            _bars[i] = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = 3, Height = 3, RadiusX = 1.5, RadiusY = 1.5,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
            };
            _barHeight[i] = _barTarget[i] = 3;
            Wave.Children.Add(_bars[i]);
        }

        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => Hide();

        _frame = DispatcherQueue.CreateTimer();
        _frame.Interval = TimeSpan.FromMilliseconds(16);
        _frame.IsRepeating = true;
        _frame.Tick += (_, _) => Animate();

        // Closing would destroy the window; it is only ever parked.
        AppWindow.Closing += (_, e) => e.Cancel = true;

        Root.Tapped += (_, _) => Clicked?.Invoke();

        Park();
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
    }

    public IntPtr Handle => _hwnd;

    private string? _avatarPath;

    /// <summary>
    /// Plays the bot's character clip (muted, looping) as the pill's badge, or drops it when
    /// <paramref name="path"/> is null or missing.
    /// </summary>
    public void ShowAvatar(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            AvatarFrame.Visibility = Visibility.Collapsed;
            Avatar.MediaPlayer?.Pause();
            if (_look != Look.Message && _mode != ListenMode.Bot) UpdateBadge();
            return;
        }
        if (path != _avatarPath || Avatar.MediaPlayer is null)
        {
            var player = Avatar.MediaPlayer ?? new Windows.Media.Playback.MediaPlayer();
            player.IsMuted = true;
            player.IsLoopingEnabled = true;
            player.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(path));
            if (Avatar.MediaPlayer is null) Avatar.SetMediaPlayer(player);
            _avatarPath = path;
        }
        BadgeGlyph.Visibility = Visibility.Collapsed;
        AvatarFrame.Visibility = Visibility.Visible;
        Badge.Visibility = Visibility.Visible;
        Avatar.MediaPlayer!.Play();
    }

    /// <summary>Shows the listening pill at the bottom of the screen that holds <paramref name="nearWindow"/>.</summary>
    public void ShowListening(IntPtr nearWindow, ListenMode mode)
    {
        _hideTimer.Stop();
        _look = Look.Listening;
        _mode = mode;
        _level = 0;
        AvatarFrame.Visibility = Visibility.Collapsed;
        Avatar.MediaPlayer?.Pause();
        UpdateBadge();
        Wave.Visibility = Visibility.Visible;
        Message.Visibility = Visibility.Collapsed;
        Present(nearWindow, new(Badge.Visibility == Visibility.Visible ? 128 : 96, HeightDip));
    }

    /// <summary>Live text isn't shown: the pill stays a waveform, like a recording light.</summary>
    public void SetTranscript(string text)
    {
    }

    public void SetLevel(float peak)
    {
        // Fast attack, slow release, with quiet speech still visible.
        _level = Math.Max(Math.Min(1f, peak * 3f), _level * 0.8f);
        for (var i = 0; i < BarCount; i++)
            _barTarget[i] = 3 + 17 * _level * _envelope[i] * (0.6 + 0.4 * _random.NextDouble());
    }

    public void ShowTranscribing()
    {
        _look = Look.Transcribing;
    }

    public void ShowError(string message, IntPtr nearWindow)
    {
        ShowMessage("Grok Voice", message, "\uE783", nearWindow, TimeSpan.FromSeconds(5));
        BadgeGlyph.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 153, 164));
    }

    /// <summary>A short confirmation, such as "Sent to Jeeves", that hides itself.</summary>
    public void ShowInfo(string message, IntPtr nearWindow) =>
        ShowMessage(message, null, "\uE73E", nearWindow, TimeSpan.FromSeconds(2));

    /// <summary>The bot's reply, shown for a while longer the longer it is. Clicking the pill raises <see cref="Clicked"/>.</summary>
    public void ShowReply(string botName, string reply, IntPtr nearWindow)
    {
        // "Jeeves · Chief of Staff" reads as "Jeeves" in the pill.
        var name = botName.Split(" · ")[0].Trim();
        var plain = MarkdownInlines.ToPlain(reply);
        ShowMessage(name, plain, "\uE99A", nearWindow, TimeSpan.FromSeconds(Math.Clamp(4 + plain.Length / 20.0, 5, 12)));
        MarkdownInlines.Fill(BodyText.Inlines, reply, 180);
    }

    /// <summary>The pill was clicked (used to open Grok Bot on a reply).</summary>
    public event Action? Clicked;

    public void Hide()
    {
        _hideTimer.Stop();
        Avatar.MediaPlayer?.Pause();
        _level = 0;
        if (!_onScreen) return;
        // Shrink and fade, then park off screen (see Animate).
        _hiding = true;
        Resize(new(56, HeightDip));
    }

    /// <summary>Hides the pill unless a message is still on screen.</summary>
    public void HideUnlessShowingError()
    {
        if (!_hideTimer.IsRunning) Hide();
    }

    private void ShowMessage(string title, string? body, string glyph, IntPtr nearWindow, TimeSpan showFor)
    {
        _look = Look.Message;
        AvatarFrame.Visibility = Visibility.Collapsed;
        Avatar.MediaPlayer?.Pause();
        BadgeGlyph.Glyph = glyph;
        BadgeGlyph.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
        BadgeGlyph.Visibility = Visibility.Visible;
        Badge.Visibility = Visibility.Visible;
        Wave.Visibility = Visibility.Collapsed;
        Message.Visibility = Visibility.Visible;
        TitleText.Text = title;
        BodyText.Text = body ?? "";
        BodyText.Visibility = body is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

        Windows.Foundation.Size size;
        if (body is { Length: > 0 })
        {
            size = new(MaxWidthDip, ReplyHeightDip);
        }
        else
        {
            _measure.Text = title;
            _measure.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
            size = new(Math.Clamp(_measure.DesiredSize.Width + 12 + 24 + 10 + 14 + 6, 120, MaxWidthDip), HeightDip);
        }
        Present(nearWindow, size);
        _hideTimer.Interval = showFor;
        _hideTimer.Start();
    }

    private void UpdateBadge()
    {
        var glyph = _mode switch
        {
            ListenMode.HandsFree => "\uE72E",
            ListenMode.Bot => "\uE99A",
            _ => null,
        };
        BadgeGlyph.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
        BadgeGlyph.Glyph = glyph ?? "";
        BadgeGlyph.Visibility = glyph is null ? Visibility.Collapsed : Visibility.Visible;
        Badge.Visibility = glyph is null && AvatarFrame.Visibility != Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Brings the pill on screen (growing from a small capsule) or resizes it where it is.</summary>
    private void Present(IntPtr nearWindow, Windows.Foundation.Size size)
    {
        _hiding = false;
        if (!_onScreen)
        {
            var anchor = nearWindow != IntPtr.Zero ? nearWindow : _hwnd;
            var monitor = MonitorFromWindow(anchor, MONITOR_DEFAULTTOPRIMARY);
            _scale = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(anchor);
            _work = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary).WorkArea;
            _size = new(56, HeightDip);
            _onScreen = true;
            Apply(_size, topmost: true);
        }
        Body.Opacity = 0;
        _fadeStart = _clock.Elapsed.TotalMilliseconds;
        Resize(size);
        _frame.Start();
    }

    private void Resize(Windows.Foundation.Size size)
    {
        _fromSize = _size;
        _toSize = size;
        _resizeStart = _clock.Elapsed.TotalMilliseconds;
        _frame.Start();
    }

    private void Animate()
    {
        var now = _clock.Elapsed.TotalMilliseconds;

        // Size: ease out towards the target.
        var t = Math.Clamp((now - _resizeStart) / ResizeTime.TotalMilliseconds, 0, 1);
        var e = 1 - Math.Pow(1 - t, 3);
        var size = new Windows.Foundation.Size(
            _fromSize.Width + (_toSize.Width - _fromSize.Width) * e,
            _fromSize.Height + (_toSize.Height - _fromSize.Height) * e);
        if (size != _size) Apply(size, topmost: false);

        // Content fades in after a change, and out while hiding.
        var f = Math.Clamp((now - _fadeStart) / FadeTime.TotalMilliseconds, 0, 1);
        Body.Opacity = _hiding ? Math.Min(Body.Opacity, 1 - t) : f;

        if (_hiding && t >= 1)
        {
            _hiding = false;
            _frame.Stop();
            Park();
            return;
        }

        // Bars: follow the voice while listening, a slow travelling ripple while transcribing.
        var seconds = now / 1000;
        for (var i = 0; i < BarCount; i++)
        {
            var target = _look switch
            {
                Look.Transcribing => 4 + 5 * Math.Max(0, Math.Sin(seconds * 7 - i * 0.7)),
                Look.Listening => Math.Max(3, _barTarget[i]),
                _ => 3,
            };
            _barHeight[i] += (target - _barHeight[i]) * 0.35;
            _bars[i].Height = _barHeight[i];
            // Let the targets fall back between level updates (every 50 ms).
            _barTarget[i] = 3 + (_barTarget[i] - 3) * 0.88;
        }

        if (_look == Look.Message && t >= 1 && f >= 1) _frame.Stop();
    }

    /// <summary>Sizes the window to <paramref name="size"/> DIPs, centred above the bottom of the work area, with a capsule-shaped region.</summary>
    private void Apply(Windows.Foundation.Size size, bool topmost)
    {
        _size = size;
        var width = (int)Math.Round(size.Width * _scale);
        var height = (int)Math.Round(size.Height * _scale);
        var radius = Math.Min(size.Height / 2, 18);
        Root.CornerRadius = new CornerRadius(radius);
        SetWindowPos(_hwnd, topmost ? HWND_TOPMOST : IntPtr.Zero,
            _work.X + (_work.Width - width) / 2,
            _work.Y + _work.Height - height - (int)(BottomMarginDip * _scale),
            width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW | (topmost ? 0 : SWP_NOZORDER));
        var d = (int)Math.Round(radius * 2 * _scale);
        SetWindowRgn(_hwnd, CreateRoundRectRgn(0, 0, width + 1, height + 1, d, d), true);
    }

    // Far off every monitor, still "visible" to Windows so bringing it back is only a move.
    private void Park()
    {
        SetWindowPos(_hwnd, IntPtr.Zero, -32000, -32000, 0, 0, SWP_NOACTIVATE | SWP_NOSIZE | SWP_NOZORDER);
        _onScreen = false;
    }

    private const int GWL_EXSTYLE = -20;
    private const nint WS_EX_NOACTIVATE = 0x08000000;
    private const nint WS_EX_TOOLWINDOW = 0x00000080;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const int MDT_EFFECTIVE_DPI = 0;
    private const int SW_SHOWNOACTIVATE = 4;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(IntPtr hwnd, int index, nint value);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}

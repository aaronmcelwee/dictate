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
    private const double MaxReplyHeightDip = 420;
    private const double BottomMarginDip = 28;
    private const double MaxWidthDip = 400;
    private const double ReplyWidthDip = 460;
    private const int BarCount = 11;
    private static readonly Windows.Foundation.Size RestSize = new(46, 8);
    private static readonly Windows.UI.Color PillColor = Windows.UI.Color.FromArgb(255, 18, 18, 18);
    private static readonly Windows.UI.Color RestColor = Windows.UI.Color.FromArgb(255, 64, 64, 64);
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

    private enum Look { Rest, Listening, Transcribing, Message }
    private Look _look;
    private ListenMode _mode;
    private float _level;
    private bool _onScreen;
    private bool _hiding;

    // Window geometry, in DIPs, animated between sizes around a fixed bottom-centre point.
    private double _scale = 1;
    private Windows.Graphics.RectInt32 _work;
    private Windows.Foundation.Size _size, _fromSize, _toSize;
    // The window's own size (DIPs): during an animation it is the larger of the two ends.
    private Windows.Foundation.Size _box;
    private (int Left, int Top, int Right, int Bottom)? _insets;
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

        // A plain popup: no caption, sizing frame or border styles left over from the presenter, any of
        // which makes Windows 11 draw a light 1 px frame line around the window.
        var style = GetWindowLongPtr(_hwnd, GWL_STYLE);
        SetWindowLongPtr(_hwnd, GWL_STYLE, (style & ~(WS_CAPTION | WS_THICKFRAME | WS_BORDER | WS_DLGFRAME | WS_SYSMENU)) | WS_POPUP);
        var exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE) & ~(WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_DLGMODALFRAME | WS_EX_STATICEDGE);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        // The capsule shape comes from a window region, so Windows must not add its own corners.
        var corners = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
        // No light frame line around the capsule.
        var noBorder = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        SuppressFrame();

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
        // Reading a reply: keep it up while the pointer is over it, then give a few more seconds.
        Root.PointerEntered += (_, _) => { if (_look == Look.Message) _hideTimer.Stop(); };
        Root.PointerExited += (_, _) =>
        {
            if (_look != Look.Message || !_onScreen || _hiding) return;
            _hideTimer.Interval = TimeSpan.FromSeconds(4);
            _hideTimer.Start();
        };

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
        if (mode == ListenMode.Bot && InConversation)
        {
            // Answering the bot: keep the card and listen at the bottom of it.
            _mode = mode;
            _level = 0;
            RemoveTransient();
            _compose = Compose();
            Turns.Children.Add(_compose);
            _look = Look.Listening;
            FitCard(nearWindow);
            return;
        }
        _conversation = false;
        _look = Look.Listening;
        _mode = mode;
        _level = 0;
        AvatarFrame.Visibility = Visibility.Collapsed;
        Avatar.MediaPlayer?.Pause();
        UpdateBadge();
        Wave.Visibility = Visibility.Visible;
        Message.Visibility = Visibility.Collapsed;
        BodyScroll.Visibility = Visibility.Collapsed;
        Body.VerticalAlignment = VerticalAlignment.Center;
        Body.Margin = new Thickness(0);
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
        _conversation = false;
        ShowMessage("Grok Voice", message, "\uE783", nearWindow, TimeSpan.FromSeconds(6));
        BadgeGlyph.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 153, 164));
    }

    /// <summary>A short confirmation, such as "Sent to Jeeves", that hides itself.</summary>
    public void ShowInfo(string message, IntPtr nearWindow) =>
        ShowMessage(message, null, "\uE73E", nearWindow, TimeSpan.FromSeconds(2));

    /// <summary>The bot's reply, shown for a while longer the longer it is. Clicking the pill raises <see cref="Clicked"/>.</summary>
    public void ShowReply(string botName, string reply, IntPtr nearWindow)
    {
        // "Jeeves · Chief of Staff" reads as "Jeeves" in the pill.
        var name = botName.Split(" · ", 2)[0].Trim();
        if (!InConversation)
        {
            Turns.Children.Clear();
            _conversation = true;
        }
        RemoveTransient();
        // Earlier answers step back so the newest turn stands out.
        foreach (var turn in Turns.Children.OfType<RichTextBlock>()) turn.Opacity = 0.55;
        Turns.Children.Add(BotTurn(reply, markdown: true));
        _look = Look.Message;
        ShowCardHeader(name, "\uE99A");
        FitCard(nearWindow);
        var plain = MarkdownInlines.ToPlain(reply);
        _hideTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(5 + plain.Length / 15.0, 6, 45));
        _hideTimer.Start();
    }

    /// <summary>
    /// The user's words went to the bot. In an open conversation they appear as the user's own bubble,
    /// with dots until the answer comes; otherwise a short "Sent to …" confirmation.
    /// </summary>
    public void ShowSent(string botName, string text, IntPtr nearWindow)
    {
        var name = botName.Split(" · ", 2)[0].Trim();
        if (!InConversation)
        {
            ShowInfo($"Sent to {name}", nearWindow);
            return;
        }
        RemoveTransient();
        Turns.Children.Add(MyTurn(text));
        _pending = new TextBlock
        {
            Text = "•••", FontSize = 13, CharacterSpacing = 200,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 125, 125, 132)),
        };
        Turns.Children.Add(_pending);
        _look = Look.Message;
        FitCard(nearWindow);
        // Stay up while the bot thinks; the answer restarts the timer.
        _hideTimer.Interval = TimeSpan.FromSeconds(90);
        _hideTimer.Start();
    }

    private bool _conversation;
    private FrameworkElement? _compose, _pending;
    private readonly Microsoft.UI.Xaml.Shapes.Rectangle[] _miniBars = new Microsoft.UI.Xaml.Shapes.Rectangle[7];

    /// <summary>A bot conversation card is on screen.</summary>
    private bool InConversation => _conversation && _onScreen && !_hiding && _look != Look.Rest;

    private void RemoveTransient()
    {
        if (_compose is not null) Turns.Children.Remove(_compose);
        if (_pending is not null) Turns.Children.Remove(_pending);
        _compose = _pending = null;
    }

    private static RichTextBlock BotTurn(string text, bool markdown)
    {
        var block = new RichTextBlock
        {
            FontSize = 13.5, LineHeight = 20, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = false,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 212, 212, 216)),
        };
        if (markdown) MarkdownInlines.FillBlocks(block.Blocks, text);
        else MarkdownInlines.FillPlain(block.Blocks, text);
        return block;
    }

    private static Border MyTurn(string text) => new()
    {
        HorizontalAlignment = HorizontalAlignment.Right,
        MaxWidth = (ReplyWidthDip - 12 - 14 - 8) * 0.8,
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 42, 42, 46)),
        CornerRadius = new CornerRadius(12, 12, 4, 12),
        Padding = new Thickness(10, 6, 10, 7),
        Child = new TextBlock
        {
            Text = text, FontSize = 13.5, LineHeight = 20, TextWrapping = TextWrapping.Wrap,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 242, 242, 242)),
        },
    };

    private StackPanel Compose()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Height = 20 };
        var bars = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < _miniBars.Length; i++)
        {
            _miniBars[i] = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = 3, Height = 3, RadiusX = 1.5, RadiusY = 1.5, VerticalAlignment = VerticalAlignment.Center,
                Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
            };
            bars.Children.Add(_miniBars[i]);
        }
        row.Children.Add(bars);
        row.Children.Add(new TextBlock
        {
            Text = "Listening…", FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 200, 200, 204)),
        });
        return row;
    }

    private void ShowCardHeader(string title, string glyph)
    {
        if (AvatarFrame.Visibility != Visibility.Visible)
        {
            BadgeGlyph.Glyph = glyph;
            BadgeGlyph.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
            BadgeGlyph.Visibility = Visibility.Visible;
        }
        Badge.Visibility = Visibility.Visible;
        Badge.VerticalAlignment = VerticalAlignment.Center;
        Badge.Margin = new Thickness(0);
        Wave.Visibility = Visibility.Collapsed;
        Message.Visibility = Visibility.Visible;
        TitleText.Text = title;
        BodyScroll.Visibility = Visibility.Visible;
    }

    /// <summary>Sizes the card to its conversation (scrolling past the maximum) and keeps the newest turn in view.</summary>
    private void FitCard(IntPtr nearWindow)
    {
        // Padding, header row, gap, conversation, padding, and slack so the last line never clips.
        const double frame = 14 + 24 + 10 + 14 + 4;
        Turns.Measure(new(ReplyWidthDip - 12 - 14, double.PositiveInfinity));
        var height = Math.Clamp(frame + Turns.DesiredSize.Height, HeightDip, MaxReplyHeightDip);
        BodyScroll.MaxHeight = height - frame;
        Body.VerticalAlignment = VerticalAlignment.Top;
        Body.Margin = new Thickness(0, 14, 0, 14);
        var size = new Windows.Foundation.Size(ReplyWidthDip, height);
        if (_onScreen && !_hiding && _look != Look.Rest && _size.Height >= HeightDip && _size.Width >= ReplyWidthDip - 1) Resize(size);
        else Present(nearWindow, size);
        _frame.Start();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            BodyScroll.UpdateLayout();
            BodyScroll.ChangeView(null, BodyScroll.ScrollableHeight, null, true);
        });
    }

    /// <summary>The pill was clicked (used to open Grok Bot on a reply).</summary>
    public event Action? Clicked;

    public void Hide()
    {
        _hideTimer.Stop();
        _conversation = false;
        RemoveTransient();
        Avatar.MediaPlayer?.Pause();
        _level = 0;
        if (Resting)
        {
            Rest();
            return;
        }
        if (!_onScreen) return;
        // Shrink and fade, then park off screen (see Animate).
        _hiding = true;
        Resize(new(56, HeightDip));
    }

    private bool _resting;

    /// <summary>
    /// Keeps a thin capsule at the bottom of the screen while idle, which grows into the pill
    /// and shrinks back to it. Off, the pill leaves the screen when idle.
    /// </summary>
    public bool Resting
    {
        get => _resting;
        set
        {
            if (_resting == value) return;
            _resting = value;
            if (value && !_onScreen) Rest();
            else if (!value && _look == Look.Rest) Park();
        }
    }

    /// <summary>Shrinks to the resting capsule on the primary screen.</summary>
    private void Rest()
    {
        _hideTimer.Stop();
        Avatar.MediaPlayer?.Pause();
        _look = Look.Rest;
        _hiding = false;
        Locate(IntPtr.Zero);
        if (!_onScreen)
        {
            _size = RestSize;
            _onScreen = true;
            Paint(RestColor);
            Body.Opacity = 0;
            Apply(_size, topmost: true);
            return;
        }
        Resize(RestSize);
    }

    /// <summary>Hides the pill unless a message is still on screen.</summary>
    public void HideUnlessShowingError()
    {
        if (_compose is not null && InConversation)
        {
            // A reply that came to nothing: drop the listening row and leave the conversation up a while.
            RemoveTransient();
            _look = Look.Message;
            FitCard(IntPtr.Zero);
            _hideTimer.Interval = TimeSpan.FromSeconds(6);
            _hideTimer.Start();
            return;
        }
        if (!_hideTimer.IsRunning) Hide();
    }

    private void ShowMessage(string title, string? body, string glyph, IntPtr nearWindow, TimeSpan showFor)
    {
        _conversation = false;
        RemoveTransient();
        _look = Look.Message;
        AvatarFrame.Visibility = Visibility.Collapsed;
        Avatar.MediaPlayer?.Pause();
        BadgeGlyph.Glyph = glyph;
        BadgeGlyph.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
        BadgeGlyph.Visibility = Visibility.Visible;
        Badge.Visibility = Visibility.Visible;
        Badge.VerticalAlignment = VerticalAlignment.Center;
        Badge.Margin = new Thickness(0);
        Wave.Visibility = Visibility.Collapsed;
        Message.Visibility = Visibility.Visible;
        TitleText.Text = title;

        if (body is { Length: > 0 })
        {
            Turns.Children.Clear();
            Turns.Children.Add(BotTurn(body, markdown: false));
            BodyScroll.Visibility = Visibility.Visible;
            FitCard(nearWindow);
            _hideTimer.Interval = showFor;
            _hideTimer.Start();
            return;
        }
        Windows.Foundation.Size size;
        {
            BodyScroll.Visibility = Visibility.Collapsed;
            Body.VerticalAlignment = VerticalAlignment.Center;
            Body.Margin = new Thickness(0);
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
        Paint(PillColor);
        if (!_onScreen)
        {
            Locate(nearWindow);
            _size = new(56, HeightDip);
            _onScreen = true;
            Apply(_size, topmost: true);
        }
        else if (_size.Height < HeightDip)
        {
            // Grow on the screen being typed on, from the same small capsule.
            Locate(nearWindow);
            Apply(_size, topmost: true);
        }
        Body.Opacity = 0;
        _fadeStart = _clock.Elapsed.TotalMilliseconds;
        Resize(size);
        _frame.Start();
    }

    private void Locate(IntPtr nearWindow)
    {
        var anchor = nearWindow != IntPtr.Zero ? nearWindow : GetDesktopWindow();
        var monitor = MonitorFromWindow(anchor, MONITOR_DEFAULTTOPRIMARY);
        _scale = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(anchor);
        _work = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary).WorkArea;
    }

    private void Resize(Windows.Foundation.Size size)
    {
        _fromSize = _size;
        _toSize = size;
        _resizeStart = _clock.Elapsed.TotalMilliseconds;
        // Size the window once to hold the whole animation; frames then only move the region.
        var box = new Windows.Foundation.Size(Math.Max(_fromSize.Width, _toSize.Width), Math.Max(_fromSize.Height, _toSize.Height));
        if (box != _box) Place(box, topmost: false);
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
        if (size != _size) Clip(size);
        // Settle the window to the final size once the animation ends.
        if (t >= 1 && _box != _toSize && !_hiding) Apply(_toSize, topmost: false);

        // Content fades in after a change, and out while hiding.
        var f = Math.Clamp((now - _fadeStart) / FadeTime.TotalMilliseconds, 0, 1);
        Body.Opacity = _hiding || _look == Look.Rest ? Math.Min(Body.Opacity, 1 - t) : f;

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
            if (_compose is not null && i >= 2 && i - 2 < _miniBars.Length && _miniBars[i - 2] is { } mini)
                mini.Height = Math.Min(16, _barHeight[i]);
            // Let the targets fall back between level updates (every 50 ms).
            _barTarget[i] = 3 + (_barTarget[i] - 3) * 0.88;
        }

        if (_look == Look.Rest && t >= 1)
        {
            Paint(RestColor);
            _frame.Stop();
        }
        if (_look == Look.Message && t >= 1 && f >= 1) _frame.Stop();
    }

    private void Paint(Windows.UI.Color color)
    {
        Root.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
        Shell.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
    }

    /// <summary>Sizes the window and the pill to <paramref name="size"/> DIPs, centred above the bottom of the work area.</summary>
    private void Apply(Windows.Foundation.Size size, bool topmost)
    {
        Place(size, topmost);
        Clip(size);
        if (topmost) SuppressFrame();
    }

    /// <summary>How far above the work area's bottom the pill sits; small sizes stay centred on the full-height line.</summary>
    private static double BottomFor(Windows.Foundation.Size size) => BottomMarginDip + (HeightDip - Math.Min(size.Height, HeightDip)) / 2;

    /// <summary>Moves and sizes the window itself to hold a <paramref name="box"/>-sized pill.</summary>
    private void Place(Windows.Foundation.Size box, bool topmost)
    {
        _box = box;
        var width = (int)Math.Round(box.Width * _scale);
        var height = (int)Math.Round(box.Height * _scale);
        // The window region does the rounding. Rounding the XAML too leaves anti-aliased corners that
        // blend with the light window background underneath, which shows as a pale rim.
        Root.CornerRadius = new CornerRadius(0);
        // WinUI keeps a few pixels of window frame around the content, which Windows paints light.
        // Size the window so the content itself is the box, and the region clips the frame away.
        // Measure it only when the window is at rest: mid-animation the child window lags behind.
        if (topmost || _insets is null || _insets == (0, 0, 0, 0)) _insets = FrameInsets();
        var (left, top, right, bottomInset) = _insets.Value;
        SetWindowPos(_hwnd, topmost ? HWND_TOPMOST : IntPtr.Zero,
            _work.X + (_work.Width - width) / 2 - left,
            _work.Y + _work.Height - height - (int)Math.Round(BottomFor(box) * _scale) - top,
            width + left + right, height + top + bottomInset, SWP_NOACTIVATE | SWP_SHOWWINDOW | (topmost ? 0 : SWP_NOZORDER));
    }

    /// <summary>Sizes the pill inside the window and clips the window to its capsule shape.</summary>
    private void Clip(Windows.Foundation.Size size)
    {
        _size = size;
        var (left, top, _, _) = _insets ?? (0, 0, 0, 0);
        var boxW = (int)Math.Round(_box.Width * _scale);
        var boxH = (int)Math.Round(_box.Height * _scale);
        var width = Math.Min(boxW, (int)Math.Round(size.Width * _scale));
        var height = Math.Min(boxH, (int)Math.Round(size.Height * _scale));
        // Bottom-aligned in the box, lifted so small sizes stay centred on the same line as full-height ones.
        var lift = Math.Clamp((BottomFor(size) - BottomFor(_box)) * _scale, 0, boxH - height);
        Root.Width = width / _scale;
        Root.Height = height / _scale;
        Root.Margin = new Thickness(0, 0, 0, lift / _scale);
        var x = left + (boxW - width) / 2;
        var y = top + boxH - height - (int)Math.Round(lift);
        var radius = Math.Min(size.Height / 2, 18);
        var d = (int)Math.Round(radius * 2 * _scale);
        SetWindowRgn(_hwnd, CreateRoundRectRgn(x, y, x + width + 1, y + height + 1, d, d), true);
    }

    /// <summary>How far the client area (where XAML draws) sits inside the window, in pixels.</summary>
    private (int Left, int Top, int Right, int Bottom) FrameInsets()
    {
        if (!GetWindowRect(_hwnd, out var window)) return (0, 0, 0, 0);
        // The XAML content lives in a child window; use its bounds, else the client area's.
        RECT content;
        var bridge = FindWindowEx(_hwnd, IntPtr.Zero, "Microsoft.UI.Content.DesktopChildSiteBridge", null);
        if (bridge == IntPtr.Zero || !GetWindowRect(bridge, out content))
        {
            if (!GetClientRect(_hwnd, out var client)) return (0, 0, 0, 0);
            var origin = new POINT();
            if (!ClientToScreen(_hwnd, ref origin)) return (0, 0, 0, 0);
            content = new RECT { Left = origin.X, Top = origin.Y, Right = origin.X + client.Right, Bottom = origin.Y + client.Bottom };
        }
        if (content.Right <= content.Left || content.Bottom <= content.Top) return (0, 0, 0, 0);
        return (Math.Max(0, content.Left - window.Left), Math.Max(0, content.Top - window.Top),
                Math.Max(0, window.Right - content.Right), Math.Max(0, window.Bottom - content.Bottom));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? title);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

    /// <summary>
    /// Windows 11 draws a light, anti-aliased frame line along a window's shape (including a region's
    /// rounded ends). Turn off DWM's non-client drawing altogether, and the border colour with it.
    /// </summary>
    private void SuppressFrame()
    {
        var disabled = DWMNCRP_DISABLED;
        DwmSetWindowAttribute(_hwnd, DWMWA_NCRENDERING_POLICY, ref disabled, sizeof(int));
        var noBorder = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));
        var corners = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
    }

    // Far off every monitor, still "visible" to Windows so bringing it back is only a move.
    private void Park()
    {
        SetWindowPos(_hwnd, IntPtr.Zero, -32000, -32000, 0, 0, SWP_NOACTIVATE | SWP_NOSIZE | SWP_NOZORDER);
        _onScreen = false;
    }

    private const int GWL_EXSTYLE = -20;
    private const int GWL_STYLE = -16;
    private static readonly nint WS_POPUP = unchecked((nint)0x80000000L);
    private const nint WS_CAPTION = 0x00C00000;
    private const nint WS_BORDER = 0x00800000;
    private const nint WS_DLGFRAME = 0x00400000;
    private const nint WS_THICKFRAME = 0x00040000;
    private const nint WS_SYSMENU = 0x00080000;
    private const nint WS_EX_WINDOWEDGE = 0x00000100;
    private const nint WS_EX_CLIENTEDGE = 0x00000200;
    private const nint WS_EX_DLGMODALFRAME = 0x00000001;
    private const nint WS_EX_STATICEDGE = 0x00020000;
    private const nint WS_EX_NOACTIVATE = 0x08000000;
    private const nint WS_EX_TOOLWINDOW = 0x00000080;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_NCRENDERING_POLICY = 2;
    private const int DWMNCRP_DISABLED = 1;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
    private const int DWMWCP_DONOTROUND = 1;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const int MDT_EFFECTIVE_DPI = 0;
    private const int SW_SHOWNOACTIVATE = 4;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

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

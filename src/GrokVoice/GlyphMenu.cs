using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace GrokVoice;

/// <summary>
/// The taskbar glyph's right-click menu as a WinUI MenuFlyout, so it looks like the menus on the
/// Windows 11 taskbar (rounded, Fluent icons, theme-aware) instead of a classic Win32 menu.
/// A flyout needs a XAML window to belong to: this one sits over the glyph, draws nothing (an
/// empty window region), and is only shown while the menu is open.
/// </summary>
internal sealed class GlyphMenu
{
    public sealed record Item(string Text, string Glyph, Action Invoke);

    private readonly Window _window;
    private readonly Grid _anchor = new() { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly IntPtr _hwnd;
    private MenuFlyout? _open;

    public GlyphMenu()
    {
        _window = new Window { Content = _anchor, Title = "Murmur menu" };
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        var presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        _window.AppWindow.SetPresenter(presenter);
        _window.AppWindow.IsShownInSwitchers = false;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, GetWindowLongPtr(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
        _window.AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            _window.AppWindow.Hide();
        };
    }

    /// <summary>Opens the menu just above the glyph, its left edge lined up with the glyph's.</summary>
    public void Show((int Left, int Top, int Right, int Bottom) glyph, IReadOnlyList<Item?> items)
    {
        _open?.Hide();
        var menu = new MenuFlyout
        {
            Placement = FlyoutPlacementMode.TopEdgeAlignedLeft,
            ShouldConstrainToRootBounds = false,
        };
        foreach (var item in items)
        {
            if (item is null)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                continue;
            }
            var entry = new MenuFlyoutItem { Text = item.Text, Icon = new FontIcon { Glyph = item.Glyph } };
            entry.Click += (_, _) => item.Invoke();
            menu.Items.Add(entry);
        }
        menu.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_open, menu)) return;
            _open = null;
            _window.AppWindow.Hide();
        };
        _open = menu;

        // The window covers the glyph (a little above it, so the menu clears the taskbar edge),
        // clipped to nothing so it never draws over the glyph.
        var size = Math.Max(1, glyph.Right - glyph.Left);
        _window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(glyph.Left, glyph.Top - 8, size, 8));
        SetWindowRgn(_hwnd, CreateRectRgn(0, 0, 0, 0), true);
        _window.AppWindow.Show();
        _window.Activate();
        // A menu closes on an outside click only when its window is in the foreground.
        SetForegroundWindow(_hwnd);

        void Open()
        {
            if (!ReferenceEquals(_open, menu)) return;
            menu.ShowAt(_anchor, new FlyoutShowOptions { Placement = FlyoutPlacementMode.TopEdgeAlignedLeft });
        }
        if (_anchor.XamlRoot is not null) Open();
        else _anchor.Loaded += (_, _) => Open();
    }

    private const int GWL_EXSTYLE = -20;
    private const nint WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(IntPtr hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}

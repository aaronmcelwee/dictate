using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.RegularExpressions;
using Interop.UIAutomationClient;

namespace GrokVoice.Context;

/// <summary>What kind of app a dictation is going into, which sets the writing style.</summary>
internal enum AppKind { Other, Email, Chat, Ai, Code, Document }

/// <summary>The app a dictation started in and, when it was worth reading, the text around the cursor.</summary>
internal sealed record DictationContext(string Process, string Title, AppKind Kind, string Source, string Text, long Milliseconds, string Selection = "")
{
    /// <summary>A PNG of the window, taken only for a bot instruction; sent only if the instruction needs the screen.</summary>
    public byte[]? Screenshot { get; init; }

    public static readonly DictationContext None = new("", "", AppKind.Other, "none", "", 0);

    /// <summary>Apps where what's on screen is usually what the user is answering.</summary>
    public bool IsReplyApp => Kind is AppKind.Email or AppKind.Chat or AppKind.Ai;
}

/// <summary>
/// Local, cheap awareness of where the user is dictating: the foreground app (for the style of the
/// text) and the text near the cursor (for names and what "this" refers to). Text comes from UI
/// Automation first; only reply-type apps with little readable text fall back to on-device OCR of
/// the window. Nothing here leaves the PC; the clean-up step decides what to send.
/// </summary>
internal static partial class AppAwareness
{
    private const int MaxChars = 1800;
    /// <summary>An instruction for the bot ("summarize this") can use much more of the screen.</summary>
    private const int BotMaxChars = 6000;
    private static readonly TimeSpan UiaBudget = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan BotUiaBudget = TimeSpan.FromMilliseconds(1500);

    [GeneratedRegex(@"\b(this|that|these|those|it|reply|respond|answer|above|below|here|him|her|them|they|he|she)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Reference();

    /// <summary>Whether a dictation points at something on screen ("reply to this", "tell him").</summary>
    public static bool RefersToScreen(string text) => Reference().IsMatch(text);

    [GeneratedRegex(@"\b(this|the|that)\s+(e-?mail|page|message|thread|chat|conversation|doc|document|article|post|file|code|error|text|paragraph|selection|screen|window|tab|pdf|slide|spreadsheet|table)\b|\bon\s+(my|the)\s+screen\b|\b(selected|highlighted)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ScreenNoun();

    [GeneratedRegex(@"\b(summari[sz]e|summary|tl;?dr|explain|translate|draft|rewrite|reword|proofread|fact[- ]check|critique|review|respond|reply|answer)\b|\bwhat\s+(does|is|are)\s+(this|that|these|it)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ScreenVerb();

    /// <summary>Names something on screen outright ("this email", "on my screen", "the selected text").</summary>
    public static bool NamesScreen(string text) => ScreenNoun().IsMatch(text);

    /// <summary>An instruction to do something with what's on screen ("summarize this", "draft a reply").</summary>
    public static bool ActsOnScreen(string text) => NamesScreen(text) || ScreenVerb().IsMatch(text);

    [GeneratedRegex(@"^\W*(?:(?:hey\s+)?jeeves\W+)?(?:please\s+)?(?:remind\s+me|reminder\b|add\b.{1,80}?\b(?:to|on)\s+(?:my|the)\b.{0,40}?\blist\b|put\b.{1,80}?\bon\s+(?:my|the)\b.{0,40}?\blist\b|(?:make\s+a\s+)?note\s+(?:that|down|to\s+self)|make\s+a\s+note|jot\s+down|don'?t\s+let\s+me\s+forget|to-?\s?do\b|add\s+(?:a\s+)?task\b)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CapturePhrase();

    /// <summary>A quick capture for the bot to file: "remind me…", "add … to my list", "note that…".</summary>
    public static bool IsCapture(string text) => CapturePhrase().IsMatch(text);

    public static AppKind Classify(string process, string title)
    {
        var p = process.ToLowerInvariant();
        var t = title.ToLowerInvariant();
        bool Title(params string[] words) => words.Any(t.Contains);
        if (p is "outlook" or "olk" or "thunderbird" or "hxoutlook" or "superhuman" or "spark" || Title("gmail", "outlook", "- mail", "inbox", "superhuman"))
            return AppKind.Email;
        if (p is "slack" or "teams" or "ms-teams" or "whatsapp" or "discord" or "telegram" or "signal" or "messenger" or "phoneexperiencehost"
            || Title("slack", "whatsapp", "messenger", "microsoft teams", "discord", "telegram", "google chat", "| chat"))
            return AppKind.Chat;
        if (p is "claude" or "chatgpt" or "grok" or "grok bot" or "perplexity" or "copilot" || p.StartsWith("grok", StringComparison.Ordinal)
            || Title("chatgpt", "claude", "grok", "gemini", "perplexity", "copilot"))
            return AppKind.Ai;
        if (p is "code" or "devenv" or "rider64" or "cursor" or "windowsterminal" or "powershell" or "pwsh" or "cmd" or "idea64" or "zed")
            return AppKind.Code;
        if (p is "winword" or "notepad" or "obsidian" or "notion" or "onenote" or "logseq" or "excel" || Title("google docs", "- word"))
            return AppKind.Document;
        return AppKind.Other;
    }

    /// <summary>One short line for the clean-up model about where the text goes.</summary>
    public static string? StyleFor(AppKind kind, bool toBot) => toBot
        ? "Destination: an instruction to the user's AI assistant. Clear, direct sentences; keep names, dates and numbers exact."
        : kind switch
        {
            AppKind.Email => "Destination: an email. Complete sentences with proper punctuation and paragraphs; no greeting or sign-off unless spoken.",
            AppKind.Chat => "Destination: a chat message. Casual tone, but keep normal sentence punctuation and capitals; only a single short sentence may drop its final full stop.",
            AppKind.Ai => "Destination: a prompt to an AI assistant. Keep wording exact and technical terms intact; use a list only if the speaker lists items.",
            AppKind.Code => "Destination: a code editor or terminal. Keep identifiers, commands and symbols literal; no trailing full stop.",
            AppKind.Document => "Destination: a document. Full sentences and paragraphs.",
            _ => null,
        };

    /// <summary>
    /// Reads the app and the text near the cursor in <paramref name="hwnd"/>. Runs off the UI thread and
    /// gives up on slow parts, so it never holds a dictation up.
    /// </summary>
    public static Task<DictationContext> CaptureAsync(IntPtr hwnd, bool readScreen, bool forBot = false) => Task.Run(async () =>
    {
        var clock = Stopwatch.StartNew();
        try
        {
            if (hwnd == IntPtr.Zero) return DictationContext.None;
            var (process, title) = Describe(hwnd);
            var kind = Classify(process, title);
            if (!readScreen || (kind == AppKind.Code && !forBot)) return new DictationContext(process, title, kind, "app", "", clock.ElapsedMilliseconds);

            var max = forBot ? BotMaxChars : MaxChars;
            // For the bot, grab the window first, before the card has a chance to cover any of it.
            var shot = forBot ? Grab(hwnd, int.MaxValue) : null;
            var (text, selection) = ReadWithUiAutomation(hwnd, max, forBot ? BotUiaBudget : UiaBudget);
            var source = "uia";
            if (text.Length < 200 && (forBot || kind is AppKind.Email or AppKind.Chat or AppKind.Ai))
            {
                var ocr = await ReadWithOcrAsync(hwnd).ConfigureAwait(false);
                if (ocr.Length > text.Length) { text = ocr; source = "ocr"; }
            }
            byte[]? png = null;
            if (shot is { } s)
            {
                try { png = await EncodePngAsync(s).ConfigureAwait(false); }
                catch (Exception e) { Log.Write($"context screenshot failed: {e.Message}"); }
            }
            return new DictationContext(process, title, kind, text.Length == 0 ? "app" : source, Trim(text, max), clock.ElapsedMilliseconds, Trim(selection, max))
            {
                Screenshot = png,
            };
        }
        catch (Exception e)
        {
            Log.Write($"context failed: {e.Message}");
            return DictationContext.None with { Milliseconds = clock.ElapsedMilliseconds };
        }
    });

    private static (string Process, string Title) Describe(IntPtr hwnd)
    {
        var title = new StringBuilder(512);
        GetWindowText(hwnd, title, title.Capacity);
        GetWindowThreadProcessId(hwnd, out var pid);
        string process;
        try { using var p = Process.GetProcessById((int)pid); process = p.ProcessName; }
        catch (Exception) { process = ""; }
        return (process, title.ToString());
    }

    private const int UIA_TextPatternId = 10014;
    private const int UIA_ValuePatternId = 10002;
    private const int UIA_IsPasswordPropertyId = 30019;
    private const int UIA_TextControlTypeId = 50020;
    private const int UIA_DocumentControlTypeId = 50030;
    private const int UIA_EditControlTypeId = 50004;
    private const int UIA_ListItemControlTypeId = 50007;
    private const int UIA_DataItemControlTypeId = 50029;
    private static IUIAutomation? _uia;

    /// <summary>
    /// Text near the focused element: its own text (a draft, a prompt box) and the readable text
    /// closest to it on screen, within a small time budget.
    /// </summary>
    private static (string Text, string Selection) ReadWithUiAutomation(IntPtr hwnd, int max, TimeSpan budget)
    {
        _uia ??= new CUIAutomation8();
        var clock = Stopwatch.StartNew();
        var window = _uia.ElementFromHandle(hwnd);
        if (window is null) return ("", "");
        var focused = _uia.GetFocusedElement();
        var anchor = window.CurrentBoundingRectangle;
        var near = (anchor.top + anchor.bottom) / 2.0;
        if (focused is not null && focused.CurrentProcessId == window.CurrentProcessId)
        {
            var r = focused.CurrentBoundingRectangle;
            if (r.bottom > r.top) near = (r.top + r.bottom) / 2.0;
        }

        var pieces = new List<(double Distance, string Text)>();
        var selection = "";
        if (focused is not null && focused.GetCurrentPropertyValue(UIA_IsPasswordPropertyId) is not true
            && focused.CurrentProcessId == window.CurrentProcessId)
        {
            selection = SelectionOf(focused, max);
            var own = TextOf(focused, Math.Min(max, 2500));
            if (own.Length > 0) pieces.Add((-1, own));
        }

        // Walk the visible tree breadth-first, nearest content first once collected.
        var walker = _uia.ControlViewWalker;
        var queue = new Queue<IUIAutomationElement>();
        queue.Enqueue(window);
        var visited = 0;
        while (queue.Count > 0 && visited < (max > MaxChars ? 1500 : 600) && clock.Elapsed < budget)
        {
            var element = queue.Dequeue();
            visited++;
            int type;
            try { type = element.CurrentControlType; }
            catch (Exception) { continue; }
            if (type is UIA_TextControlTypeId or UIA_DocumentControlTypeId or UIA_ListItemControlTypeId or UIA_DataItemControlTypeId
                && element.GetCurrentPropertyValue(UIA_IsPasswordPropertyId) is not true)
            {
                var text = type == UIA_DocumentControlTypeId ? TextOf(element, Math.Max(2500, max)) : (element.CurrentName ?? "").Trim();
                if (text.Length > 2)
                {
                    var r = element.CurrentBoundingRectangle;
                    if (r.bottom > r.top) pieces.Add((Math.Abs((r.top + r.bottom) / 2.0 - near), text));
                }
                if (type == UIA_DocumentControlTypeId) continue; // Its text is already in.
            }
            try
            {
                for (var child = walker.GetFirstChildElement(element); child is not null; child = walker.GetNextSiblingElement(child))
                    queue.Enqueue(child);
            }
            catch (Exception) { }
        }

        var result = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, text) in pieces.OrderBy(p => p.Distance))
        {
            if (!seen.Add(text)) continue;
            if (result.Length + text.Length > max * 2) break;
            result.AppendLine(text);
        }
        return (result.ToString(), selection);
    }

    /// <summary>The text selected in the focused element, if it exposes one (documents, browsers, editors).</summary>
    private static string SelectionOf(IUIAutomationElement element, int max)
    {
        try
        {
            if (element.GetCurrentPattern(UIA_TextPatternId) is not IUIAutomationTextPattern text) return "";
            var ranges = text.GetSelection();
            if (ranges is null) return "";
            var parts = new List<string>();
            for (var i = 0; i < ranges.Length; i++)
                if ((ranges.GetElement(i).GetText(max) ?? "").Trim() is { Length: > 0 } part) parts.Add(part);
            return string.Join("\n", parts);
        }
        catch (Exception) { return ""; }
    }

    private static string TextOf(IUIAutomationElement element, int max)
    {
        try
        {
            if (element.GetCurrentPattern(UIA_TextPatternId) is IUIAutomationTextPattern text)
                return (text.DocumentRange.GetText(max) ?? "").Trim();
            if (element.GetCurrentPattern(UIA_ValuePatternId) is IUIAutomationValuePattern value)
                return (value.CurrentValue ?? "").Trim();
        }
        catch (Exception) { }
        return "";
    }

    /// <summary>On-device OCR of the window as it is on screen (Windows.Media.Ocr).</summary>
    private static async Task<string> ReadWithOcrAsync(IntPtr hwnd)
    {
        var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null || Grab(hwnd, (int)Windows.Media.Ocr.OcrEngine.MaxImageDimension) is not { } shot) return "";
        using var image = shot.ToBitmap();
        var result = await engine.RecognizeAsync(image);
        return string.Join("\n", result.Lines.Select(l => l.Text).Where(t => t.Trim().Length > 2));
    }

    private sealed record Shot(byte[] Bgra, int Width, int Height)
    {
        public Windows.Graphics.Imaging.SoftwareBitmap ToBitmap() =>
            Windows.Graphics.Imaging.SoftwareBitmap.CreateCopyFromBuffer(
                Bgra.AsBuffer(), Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Width, Height, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
    }

    /// <summary>The window's pixels as on screen, at most <paramref name="maxSide"/> on each side.</summary>
    private static Shot? Grab(IntPtr hwnd, int maxSide)
    {
        if (!GetWindowRect(hwnd, out var rect)) return null;
        var width = Math.Min(rect.Right - rect.Left, maxSide);
        var height = Math.Min(rect.Bottom - rect.Top, maxSide);
        if (width < 32 || height < 32) return null;

        var pixels = new byte[width * height * 4];
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        try
        {
            var old = SelectObject(memory, bitmap);
            BitBlt(memory, 0, 0, width, height, screen, rect.Left, rect.Top, SRCCOPY);
            SelectObject(memory, old);
            var info = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -height,
                biPlanes = 1, biBitCount = 32, biCompression = 0,
            };
            GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref info, 0);
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        return new Shot(pixels, width, height);
    }

    /// <summary>A PNG of the shot, scaled down to at most 1920 px on its long side.</summary>
    private static async Task<byte[]> EncodePngAsync(Shot shot)
    {
        const int MaxSide = 1920;
        using var bitmap = shot.ToBitmap();
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(bitmap);
        var scale = Math.Min(1.0, (double)MaxSide / Math.Max(shot.Width, shot.Height));
        if (scale < 1.0)
        {
            encoder.BitmapTransform.ScaledWidth = (uint)Math.Max(1, shot.Width * scale);
            encoder.BitmapTransform.ScaledHeight = (uint)Math.Max(1, shot.Height * scale);
            encoder.BitmapTransform.InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant;
        }
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        using var reader = new Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    /// <summary>Collapses whitespace and keeps the text to a few hundred tokens.</summary>
    private static string Trim(string text, int max)
    {
        var lines = text.Split('\n').Select(l => Regex.Replace(l, @"\s+", " ").Trim()).Where(l => l.Length > 0);
        var joined = string.Join("\n", lines);
        return joined.Length <= max ? joined : joined[..max] + " …";
    }

    private const int SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr src, int sx, int sy, int rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);
}

namespace Dictate.Core.Glyph;

/// <summary>What the taskbar glyph shows.</summary>
public enum GlyphMode
{
    /// <summary>Still violet bars.</summary>
    Rest,
    /// <summary>Dictating at the cursor: violet bars follow the voice.</summary>
    Listen,
    /// <summary>Transcribing a dictation: the bars drop to pulsing dots.</summary>
    Work,
    /// <summary>Talking to the bot: an amber disc with small dark bars that follow the voice.</summary>
    BotListen,
    /// <summary>The bot is working on it: the amber disc with pulsing dots.</summary>
    BotWork,
    /// <summary>The bot's conversation is open: the amber disc with still bars, on a pressed plate.</summary>
    Reply,
    /// <summary>Something failed: grey bars with a red dot.</summary>
    Error,
    /// <summary>Paused from the menu: grey bars.</summary>
    Paused,
}

/// <summary>Colours for one theme, as straight (not premultiplied) RGBA.</summary>
public readonly record struct GlyphPalette(
    uint Accent, uint Bot, uint BotInk, uint Dim, uint Critical, uint Hover, uint Pressed)
{
    // 0xRRGGBBAA
    public static readonly GlyphPalette Dark = new(
        Accent: 0xA996FFFF, Bot: 0xFFB86BFF, BotInk: 0x1C1C1CFF, Dim: 0xFFFFFF8C,
        Critical: 0xFF99A4FF, Hover: 0xFFFFFF0F, Pressed: 0xFFFFFF0B);

    public static readonly GlyphPalette Light = new(
        Accent: 0x5B3FD1FF, Bot: 0xB85C00FF, BotInk: 0xFFFFFFFF, Dim: 0x00000073,
        Critical: 0xC42B1CFF, Hover: 0x0000000A, Pressed: 0x00000006);
}

/// <summary>
/// The taskbar glyph's look and motion, independent of any window: <see cref="Step"/> advances the
/// animation and <see cref="Render"/> draws one frame into a premultiplied BGRA buffer, which
/// <c>UpdateLayeredWindow</c> takes as is.
/// <para>
/// Geometry is in DIPs on a 40 x 40 plate (Windows 11 taskbar button size, corner radius 4). The mark is
/// a 24 x 24 box in the middle holding five 2 px bars, 4 px apart, up to 18 px tall. Motion follows
/// the Fluent durations: 83 ms for the plate, 167 ms for fades and bars, 250 ms (decelerate) for the
/// disc and the bars shrinking into it.
/// </para>
/// </summary>
public sealed class TaskbarGlyphModel
{
    public const double PlateDip = 40;
    private const double PlateRadius = 4;
    private const double Centre = PlateDip / 2;
    private const double BarWidth = 2;
    private const double BarHeight = 18;
    private const double DotScale = 2 / BarHeight;
    private const double BotBarsScale = 0.62;
    private const double DiscRadius = 12;
    private static readonly double[] Base = [0.34, 0.67, 0.9, 0.56, 0.34];
    private static readonly double[] Period = [0.82, 0.64, 0.9, 0.7, 0.86];

    private readonly double[] _scale = (double[])Base.Clone();
    private readonly double[] _opacity = [1, 1, 1, 1, 1];
    private readonly double[] _jitter = [1, 1, 1, 1, 1];
    private readonly Random _random = new();
    private double _discOpacity, _discScale = 0.6, _barsScale = 1, _badge;
    private double _plateR, _plateG, _plateB, _plateA;
    private double _barR, _barG, _barB, _barA;
    private double _level;
    private bool _primed;

    public GlyphMode Mode { get; set; }
    public bool Hover { get; set; }
    public GlyphPalette Palette { get; set; } = GlyphPalette.Dark;

    /// <summary>Microphone peak, 0 to 1, about every 50 ms while listening.</summary>
    public void SetLevel(float peak)
    {
        _level = Math.Max(Math.Min(1, peak * 3.0), _level);
        for (var i = 0; i < _jitter.Length; i++) _jitter[i] = 0.65 + 0.35 * _random.NextDouble();
    }

    private bool IsBot => Mode is GlyphMode.BotListen or GlyphMode.BotWork or GlyphMode.Reply;
    private bool IsListening => Mode is GlyphMode.Listen or GlyphMode.BotListen;
    private bool IsWorking => Mode is GlyphMode.Work or GlyphMode.BotWork;

    /// <summary>
    /// Advances the animation by <paramref name="dtMs"/> at clock time <paramref name="seconds"/>.
    /// Returns false once everything has settled and no more frames are needed.
    /// </summary>
    public bool Step(double dtMs, double seconds)
    {
        var p = Palette;
        var bot = IsBot;
        var plate = Hover ? p.Hover : Mode == GlyphMode.Reply ? p.Pressed : p.Hover & 0xFFFFFF00;
        var bar = Mode is GlyphMode.Error or GlyphMode.Paused ? p.Dim : bot ? p.BotInk : p.Accent;
        if (!_primed)
        {
            // First frame: start where we are, no animation in from black.
            _primed = true;
            (_plateR, _plateG, _plateB, _plateA) = Split(plate);
            (_barR, _barG, _barB, _barA) = Split(bar);
            dtMs = 1000;
        }

        var moving = false;
        moving |= Ease(ref _discOpacity, bot ? 1 : 0, dtMs, 167);
        moving |= Ease(ref _discScale, bot ? 1 : 0.6, dtMs, 250);
        moving |= Ease(ref _barsScale, bot ? BotBarsScale : 1, dtMs, 250);
        moving |= Ease(ref _badge, Mode == GlyphMode.Error ? 1 : 0, dtMs, 167);
        moving |= EaseColour(ref _plateR, ref _plateG, ref _plateB, ref _plateA, plate, dtMs, 83);
        moving |= EaseColour(ref _barR, ref _barG, ref _barB, ref _barA, bar, dtMs, 167);

        // The level falls away between microphone updates.
        _level *= Math.Exp(-dtMs / 140);
        for (var i = 0; i < _scale.Length; i++)
        {
            double scale = Base[i], opacity = 1;
            if (IsListening)
            {
                // Always a gentle sway, so a quiet room still reads as listening; the voice drives the rest.
                var sway = 0.5 + 0.5 * Math.Sin((seconds / Period[i] + i * 0.21) * 2 * Math.PI);
                var voice = Math.Min(1, _level * (0.55 + 0.45 * Base[i] / 0.9) * 1.6 * _jitter[i]);
                scale = Math.Max(0.28 + 0.16 * sway, 0.28 + 0.72 * voice);
            }
            else if (IsWorking)
            {
                scale = DotScale;
                // Each dot fades up and down in turn, 900 ms a cycle.
                var phase = (seconds - i * 0.12) / 0.9;
                opacity = 0.3 + 0.7 * (0.5 - 0.5 * Math.Cos((phase - Math.Floor(phase)) * 2 * Math.PI));
            }
            moving |= Ease(ref _scale[i], scale, dtMs, IsListening ? 90 : 167);
            moving |= Ease(ref _opacity[i], opacity, dtMs, IsWorking ? 40 : 167);
        }
        return moving || IsListening || IsWorking;
    }

    private static bool Ease(ref double value, double target, double dtMs, double durationMs)
    {
        // Exponential approach that lands within about 5% in the given duration (decelerate feel).
        var k = 1 - Math.Exp(-3 * dtMs / durationMs);
        value += (target - value) * k;
        if (Math.Abs(target - value) < 0.002)
        {
            var moved = value != target;
            value = target;
            return moved;
        }
        return true;
    }

    private static bool EaseColour(ref double r, ref double g, ref double b, ref double a, uint target, double dtMs, double durationMs)
    {
        var (tr, tg, tb, ta) = Split(target);
        var moving = Ease(ref a, ta, dtMs, durationMs);
        moving |= Ease(ref r, tr, dtMs, durationMs);
        moving |= Ease(ref g, tg, dtMs, durationMs);
        moving |= Ease(ref b, tb, dtMs, durationMs);
        return moving;
    }

    private static (double R, double G, double B, double A) Split(uint rgba) =>
        (((rgba >> 24) & 0xFF) / 255.0, ((rgba >> 16) & 0xFF) / 255.0, ((rgba >> 8) & 0xFF) / 255.0, (rgba & 0xFF) / 255.0);

    /// <summary>
    /// Draws the current frame, <paramref name="size"/> pixels square at <paramref name="scale"/> pixels
    /// per DIP, into <paramref name="bgra"/> as premultiplied BGRA rows, top row first.
    /// </summary>
    public void Render(Span<byte> bgra, int size, double scale)
    {
        if (!_primed) Step(0, 0);
        var p = Palette;
        var (discR, discG, discB, discA) = Split(p.Bot);
        var (critR, critG, critB, critA) = Split(p.Critical);
        var aa = 1 / scale; // one pixel, in DIPs, for edge smoothing
        var badgeX = Centre + 11;
        var badgeY = Centre - 7;

        for (var py = 0; py < size; py++)
        for (var px = 0; px < size; px++)
        {
            var x = (px + 0.5) / scale;
            var y = (py + 0.5) / scale;
            double r = 0, g = 0, b = 0, a = 0;

            var plate = Coverage(RoundRect(x, y, 0, 0, PlateDip, PlateDip, PlateRadius), aa);
            Over(ref r, ref g, ref b, ref a, _plateR, _plateG, _plateB, _plateA * plate);

            if (_discOpacity > 0.001)
            {
                var disc = Coverage(Circle(x, y, Centre, Centre, DiscRadius * _discScale), aa);
                Over(ref r, ref g, ref b, ref a, discR, discG, discB, discA * disc * _discOpacity);
            }

            for (var i = 0; i < _scale.Length; i++)
            {
                var cx = Centre + (4 + 4 * i - 12) * _barsScale;
                var w = BarWidth * _barsScale;
                var h = Math.Max(w, BarHeight * _scale[i] * _barsScale);
                if (Math.Abs(x - cx) > w || Math.Abs(y - Centre) > h) continue;
                var bar = Coverage(RoundRect(x, y, cx - w / 2, Centre - h / 2, w, h, w / 2), aa);
                Over(ref r, ref g, ref b, ref a, _barR, _barG, _barB, _barA * bar * _opacity[i]);
            }

            if (_badge > 0.001)
            {
                // A ring cut out of whatever is under the dot, then the dot itself.
                var cut = Coverage(Circle(x, y, badgeX, badgeY, 6), aa) * _badge;
                r *= 1 - cut; g *= 1 - cut; b *= 1 - cut; a *= 1 - cut;
                var dot = Coverage(Circle(x, y, badgeX, badgeY, 4), aa);
                Over(ref r, ref g, ref b, ref a, critR, critG, critB, critA * dot * _badge);
            }

            // Fully transparent pixels let clicks through a layered window; keep the plate clickable.
            if (plate > 0.5 && a < 1 / 255.0) a = 1 / 255.0;

            var o = (py * size + px) * 4;
            bgra[o] = ToByte(b);
            bgra[o + 1] = ToByte(g);
            bgra[o + 2] = ToByte(r);
            bgra[o + 3] = ToByte(a);
        }
    }

    /// <summary>Premultiplied "over" with a straight colour at <paramref name="alpha"/>.</summary>
    private static void Over(ref double r, ref double g, ref double b, ref double a, double sr, double sg, double sb, double alpha)
    {
        if (alpha <= 0) return;
        var keep = 1 - alpha;
        r = sr * alpha + r * keep;
        g = sg * alpha + g * keep;
        b = sb * alpha + b * keep;
        a = alpha + a * keep;
    }

    private static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);

    private static double Coverage(double distance, double pixel) => Math.Clamp(0.5 - distance / pixel, 0, 1);

    private static double Circle(double x, double y, double cx, double cy, double radius) =>
        Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - radius;

    /// <summary>Signed distance to a rounded rectangle (negative inside).</summary>
    private static double RoundRect(double x, double y, double left, double top, double width, double height, double radius)
    {
        radius = Math.Min(radius, Math.Min(width, height) / 2);
        var qx = Math.Abs(x - (left + width / 2)) - (width / 2 - radius);
        var qy = Math.Abs(y - (top + height / 2)) - (height / 2 - radius);
        var outside = Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0));
        return outside + Math.Min(Math.Max(qx, qy), 0) - radius;
    }
}

using Dictate.Core.Glyph;
using Xunit;

namespace Dictate.Core.Tests;

public class TaskbarGlyphModelTests
{
    private const int Size = 60; // 40 DIPs at 150%
    private const double Scale = 1.5;

    private static byte[] Frame(TaskbarGlyphModel model, double settleMs = 2000)
    {
        for (double t = 0; t < settleMs; t += 16) model.Step(16, t / 1000);
        var buffer = new byte[Size * Size * 4];
        model.Render(buffer, Size, Scale);
        return buffer;
    }

    private static (int B, int G, int R, int A) Pixel(byte[] f, double xDip, double yDip)
    {
        var o = ((int)(yDip * Scale) * Size + (int)(xDip * Scale)) * 4;
        return (f[o], f[o + 1], f[o + 2], f[o + 3]);
    }

    [Fact]
    public void Rest_shows_violet_bars_on_a_clear_but_clickable_plate()
    {
        var f = Frame(new TaskbarGlyphModel { Mode = GlyphMode.Rest });
        // Middle bar (x 20) is 0.9 x 18 tall, so solid at the centre.
        var bar = Pixel(f, 20.4, 20);
        Assert.Equal(255, bar.A);
        Assert.Equal(0xA9, bar.R);
        Assert.Equal(0xFF, bar.B);
        // The plate corner area is nearly transparent but still takes the mouse.
        var plate = Pixel(f, 2, 20);
        Assert.Equal(1, plate.A);
        // Outside the plate's rounded corner nothing is drawn.
        Assert.Equal(0, Pixel(f, 0.2, 0.2).A);
    }

    [Fact]
    public void Bot_modes_fill_an_amber_disc_with_dark_bars()
    {
        var f = Frame(new TaskbarGlyphModel { Mode = GlyphMode.Reply });
        // Inside the disc but between bars: amber.
        var disc = Pixel(f, 20, 30);
        Assert.Equal(255, disc.A);
        Assert.Equal(0xFF, disc.R);
        Assert.Equal(0xB8, disc.G);
        // The middle bar shrinks into the disc and turns dark.
        var bar = Pixel(f, 20.4, 20);
        Assert.True(bar.R < 0x40, $"bar should be dark, was {bar.R:X2}");
    }

    [Fact]
    public void Dictation_and_bot_listening_look_different()
    {
        var dictation = Frame(new TaskbarGlyphModel { Mode = GlyphMode.Listen });
        var bot = Frame(new TaskbarGlyphModel { Mode = GlyphMode.BotListen });
        Assert.Equal(1, Pixel(dictation, 20, 31).A);   // no disc under dictation
        Assert.Equal(255, Pixel(bot, 20, 31).A);       // disc under bot listening
    }

    [Fact]
    public void Error_adds_a_red_dot_top_right()
    {
        var f = Frame(new TaskbarGlyphModel { Mode = GlyphMode.Error });
        var dot = Pixel(f, 31, 13);
        Assert.Equal(255, dot.A);
        Assert.Equal(0xFF, dot.R);
        Assert.Equal(0x99, dot.G);
    }

    [Fact]
    public void Settles_at_rest_and_keeps_animating_while_listening()
    {
        var rest = new TaskbarGlyphModel { Mode = GlyphMode.Rest };
        Frame(rest);
        Assert.False(rest.Step(16, 3));

        var listen = new TaskbarGlyphModel { Mode = GlyphMode.Listen };
        Frame(listen);
        Assert.True(listen.Step(16, 3));
    }

    [Fact]
    public void Hover_tints_the_plate()
    {
        var model = new TaskbarGlyphModel { Mode = GlyphMode.Rest, Hover = true };
        var f = Frame(model);
        Assert.Equal(15, Pixel(f, 2, 20).A);
    }
}

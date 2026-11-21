using Dictate.Core.Audio;
using Xunit;

namespace Dictate.Core.Tests;

public class OpenClickGateTests
{
    private const int Rate = 16000;

    private static List<float> Run(IEnumerable<float> input, out OpenClickGate gate)
    {
        var output = new List<float>();
        gate = new OpenClickGate(Rate);
        foreach (var s in input) gate.Process(s, output.Add);
        return output;
    }

    // A click: loud, then decaying by ~0.2 dB per ms (about -9 dB to -63 dB over 260 ms).
    private static IEnumerable<float> Click(int ms)
    {
        for (var i = 0; i < ms * Rate / 1000; i++)
        {
            var db = -9 - 0.21 * i * 1000.0 / Rate;
            yield return (float)(Math.Pow(10, db / 20) * Math.Sqrt(2) * Math.Sin(i * 0.7));
        }
    }

    private static IEnumerable<float> Tone(int ms, double db) =>
        Enumerable.Range(0, ms * Rate / 1000).Select(i => (float)(Math.Pow(10, db / 20) * Math.Sqrt(2) * Math.Sin(i * 0.3)));

    [Fact]
    public void The_click_is_dropped_until_it_has_died_away()
    {
        var output = Run(Click(400), out var gate);
        Assert.True(gate.IsOpen);
        Assert.InRange(gate.HeldMs, 150, 200); // -45 dB is reached at about 170 ms
        Assert.All(output, s => Assert.True(Math.Abs(s) < 0.01f));
    }

    [Fact]
    public void Speech_over_the_click_tail_opens_the_gate_at_once()
    {
        var input = Click(80).Concat(Tone(200, -10)).ToList();
        var output = Run(input, out var gate);
        Assert.InRange(gate.HeldMs, 80, 100);
        Assert.True(output.Count >= 190 * Rate / 1000);
    }

    [Fact]
    public void A_quiet_open_passes_after_the_minimum_hold()
    {
        Run(Tone(200, -60), out var gate);
        Assert.Equal(40, gate.HeldMs);
    }

    [Fact]
    public void It_never_holds_back_more_than_300_ms()
    {
        Run(Tone(500, -20), out var gate);
        Assert.Equal(300, gate.HeldMs);
    }
}

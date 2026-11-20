using Dictate.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Dictate.Core.Tests;

public class HoldDetectorTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly HoldDetector _detector;
    private readonly List<KeyEvent> _events = new();

    public HoldDetectorTests()
    {
        _detector = new HoldDetector(_time, TimeSpan.FromMilliseconds(250));
        _detector.Gesture += _events.Add;
    }

    [Fact]
    public void Quick_release_is_a_tap()
    {
        _detector.Down();
        _time.Advance(TimeSpan.FromMilliseconds(120));
        _detector.Up();
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new[] { KeyEvent.Tap }, _events);
    }

    [Fact]
    public void Holding_past_the_threshold_starts_a_hold_and_release_ends_it()
    {
        _detector.Down();
        _time.Advance(TimeSpan.FromMilliseconds(249));
        Assert.Empty(_events);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(new[] { KeyEvent.HoldStart }, _events);
        _time.Advance(TimeSpan.FromSeconds(5));
        _detector.Up();
        Assert.Equal(new[] { KeyEvent.HoldStart, KeyEvent.HoldEnd }, _events);
    }

    [Fact]
    public void Auto_repeat_downs_are_ignored()
    {
        _detector.Down();
        _time.Advance(TimeSpan.FromMilliseconds(200));
        _detector.Down();
        _time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(new[] { KeyEvent.HoldStart }, _events);
    }

    [Fact]
    public void Abandoning_before_the_threshold_reports_nothing()
    {
        _detector.Down();
        _time.Advance(TimeSpan.FromMilliseconds(100));
        _detector.Abandon();
        _time.Advance(TimeSpan.FromSeconds(1));
        _detector.Up();
        Assert.Empty(_events);
    }

    [Fact]
    public void Abandoning_a_hold_still_ends_it()
    {
        _detector.Down();
        _time.Advance(TimeSpan.FromMilliseconds(300));
        _detector.Abandon();
        Assert.Equal(new[] { KeyEvent.HoldStart, KeyEvent.HoldEnd }, _events);
    }

    [Fact]
    public void A_stale_timer_from_an_earlier_press_does_not_fire()
    {
        _detector.Down();
        _time.Advance(TimeSpan.FromMilliseconds(100));
        _detector.Up();
        _detector.Down();
        _time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(new[] { KeyEvent.Tap }, _events);
        _time.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Equal(new[] { KeyEvent.Tap, KeyEvent.HoldStart }, _events);
    }
}

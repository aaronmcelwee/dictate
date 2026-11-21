namespace Dictate.Core.Audio;

/// <summary>
/// Removes the click a microphone makes when it opens. The click is loud and then only decays, so the
/// gate holds the signal back in 10 ms blocks and opens on the first block that is either quiet
/// (the click has died away) or louder than the one before (speech has started over the tail).
/// It never holds back more than 300 ms, and fades in over 10 ms when it opens.
/// </summary>
public sealed class OpenClickGate
{
    private const double MinMs = 30, MaxMs = 300, QuietDb = -45, RiseDb = 6, FadeMs = 10;
    private readonly float[] _block;
    private readonly int _fadeFrames;
    private readonly int _minBlocks, _maxBlocks;
    private int _filled, _blocks, _faded;
    private double _sumSquares, _previousDb = double.PositiveInfinity;

    public OpenClickGate(int sampleRate)
    {
        _block = new float[Math.Max(1, sampleRate / 100)];
        _fadeFrames = Math.Max(1, (int)(sampleRate * FadeMs / 1000));
        _minBlocks = (int)(MinMs / 10);
        _maxBlocks = (int)(MaxMs / 10);
    }

    public bool IsOpen { get; private set; }

    /// <summary>How long the gate held back, once open (for the log).</summary>
    public int HeldMs => _blocks * 10;

    /// <summary>Passes one sample through; whatever comes out goes to <paramref name="emit"/>.</summary>
    public void Process(float sample, Action<float> emit)
    {
        if (IsOpen)
        {
            emit(Fade(sample));
            return;
        }
        _block[_filled++] = sample;
        _sumSquares += sample * sample;
        if (_filled < _block.Length) return;

        var db = 10 * Math.Log10(_sumSquares / _block.Length + 1e-20);
        _blocks++;
        var open = _blocks >= _maxBlocks
            || (_blocks > _minBlocks && (db < QuietDb || db > _previousDb + RiseDb));
        _previousDb = db;
        if (open)
        {
            // A block that opened the gate because it is louder is the start of speech: keep it.
            IsOpen = true;
            for (var i = 0; i < _filled; i++) emit(Fade(_block[i]));
        }
        _filled = 0;
        _sumSquares = 0;
    }

    private float Fade(float sample) =>
        _faded >= _fadeFrames ? sample : sample * (_faded++ / (float)_fadeFrames);
}

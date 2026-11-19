using Dictate.Core.Dictation;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Reflection;
using System.Runtime.InteropServices;

namespace GrokVoice.Platform;

/// <summary>
/// The default Windows microphone (the multimedia default, never the "communications" device such as a
/// Bluetooth headset's hands-free mic), captured through WASAPI at its own format, mixed to mono and
/// resampled with a proper anti-aliasing filter to 16 kHz, 16-bit mono PCM in about 50 ms buffers.
/// Reopens on the next Start after a failure.
/// </summary>
internal sealed class MicrophoneSource : IAudioSource, IDisposable
{
    private readonly object _gate = new();
    private readonly int _sampleRate;
    private WasapiCapture? _capture;
    private FloatQueue? _queue;
    private ISampleProvider? _resampler;
    private float[] _out = new float[4096];
    private byte[] _pcm = new byte[8192];

    public MicrophoneSource(int sampleRate = 16000) => _sampleRate = sampleRate;

    public event Action<ReadOnlyMemory<byte>>? AudioAvailable;
    /// <summary>Peak level of each buffer, 0 to 1. Raised on the capture thread.</summary>
    public event Action<float>? LevelChanged;
    /// <summary>The microphone could not be opened or stopped unexpectedly.</summary>
    public event Action<string>? Faulted;

    /// <summary>The capture device to use, or null for Windows' default multimedia microphone. Applies from the next Start.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Active microphones, by id and name.</summary>
    public static IReadOnlyList<(string Id, string Name)> Devices()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            return devices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .Select(d => (d.ID, d.FriendlyName)).ToList();
        }
        catch (Exception) { return []; }
    }

    /// <summary>
    /// Ask for the device's unprocessed (RAW) stream, skipping Windows' and the driver's voice effects
    /// (noise suppression, "Voice Clarity"), which muffle speech and hurt recognition. Falls back to the
    /// processed stream where the device refuses. Applies from the next Start.
    /// </summary>
    public bool Raw { get; set; } = true;

    /// <summary>The device and format in use, for diagnostics; empty until the microphone has opened.</summary>
    public string Description { get; private set; } = "";

    public bool IsRunning
    {
        get { lock (_gate) return _capture is not null; }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_capture is not null) return;
            WasapiCapture? capture = null;
            try
            {
                using var devices = new MMDeviceEnumerator();
                MMDevice? device = null;
                if (DeviceId is { Length: > 0 } id)
                {
                    try { device = devices.GetDevice(id); if (device.State != DeviceState.Active) device = null; }
                    catch (Exception) { device = null; }
                }
                device ??= devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 50);
                var raw = Raw && TryRequestRaw(capture);
                capture.DataAvailable += OnData;
                try { capture.StartRecording(); }
                catch (Exception) when (raw)
                {
                    // Some drivers accept the request but refuse to open the raw stream: use the processed one.
                    capture.Dispose();
                    capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 50);
                    raw = false;
                    capture.DataAvailable += OnData;
                    capture.StartRecording();
                }
                var format = capture.WaveFormat;
                _queue = new FloatQueue(format.SampleRate);
                _resampler = format.SampleRate == _sampleRate ? _queue : new WdlResamplingSampleProvider(_queue, _sampleRate);
                Description = $"{device.FriendlyName}, {format.SampleRate} Hz, {format.Channels} ch, {format.BitsPerSample}-bit {format.Encoding}, {(raw ? "raw" : "processed")}";
                capture.RecordingStopped += OnStopped;
                _capture = capture;
                GrokVoice.Log.Write($"mic   {Description}");
            }
            catch (Exception e)
            {
                capture?.Dispose();
                Faulted?.Invoke(Describe(e));
            }
        }
    }

    /// <summary>
    /// Sets AUDCLNT_STREAMOPTIONS_RAW on the capture's audio client before it is initialised.
    /// NAudio keeps the client private and has no option for it, so reach it by reflection and call
    /// IAudioClient2::SetClientProperties directly. Returns false (and changes nothing) if anything fails.
    /// </summary>
    private static bool TryRequestRaw(WasapiCapture capture)
    {
        IntPtr unknown = IntPtr.Zero, client2 = IntPtr.Zero;
        try
        {
            var client = typeof(WasapiCapture).GetField("audioClient", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(capture);
            var com = client?.GetType().GetField("audioClientInterface", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(client);
            if (com is null) return false;
            unknown = Marshal.GetIUnknownForObject(com);
            var iid = new Guid("726778CD-F60A-4eda-82DE-E47610CD78AA");
            if (Marshal.QueryInterface(unknown, ref iid, out client2) != 0 || client2 == IntPtr.Zero) return false;
            // IUnknown (3) + IAudioClient (12) + IsOffloadCapable (1): SetClientProperties is slot 16.
            var vtable = Marshal.ReadIntPtr(client2);
            var set = Marshal.GetDelegateForFunctionPointer<SetClientPropertiesFn>(Marshal.ReadIntPtr(vtable, 16 * IntPtr.Size));
            var props = new ClientProperties { Size = (uint)Marshal.SizeOf<ClientProperties>(), Offload = 0, Category = AudioCategorySpeech, Options = StreamOptionsRaw };
            var hr = set(client2, ref props);
            GrokVoice.Log.Write($"mic   raw stream {(hr == 0 ? "requested" : $"refused (0x{hr:X8})")}");
            return hr == 0;
        }
        catch (Exception e)
        {
            GrokVoice.Log.Write($"mic   raw stream unavailable ({e.Message})");
            return false;
        }
        finally
        {
            if (client2 != IntPtr.Zero) Marshal.Release(client2);
            if (unknown != IntPtr.Zero) Marshal.Release(unknown);
        }
    }

    private const int AudioCategorySpeech = 9;
    private const int StreamOptionsRaw = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ClientProperties
    {
        public uint Size;
        public int Offload;
        public int Category;
        public int Options;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetClientPropertiesFn(IntPtr self, ref ClientProperties properties);

    public void Stop()
    {
        WasapiCapture? capture;
        lock (_gate)
        {
            capture = _capture;
            _capture = null;
        }
        // Disposed in OnStopped once the capture thread has let go of the device.
        capture?.StopRecording();
    }

    private WaveFileWriter? _raw;

    /// <summary>Starts keeping the device's own stream, unconverted, in <paramref name="path"/> (diagnostics).</summary>
    public void BeginRaw(string path)
    {
        lock (_gate)
        {
            EndRawLocked();
            if (_capture is null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                _raw = new WaveFileWriter(path, _capture.WaveFormat);
            }
            catch (Exception) { _raw = null; }
        }
    }

    public void EndRaw()
    {
        lock (_gate) EndRawLocked();
    }

    private void EndRawLocked()
    {
        try { _raw?.Dispose(); } catch (Exception) { }
        _raw = null;
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || sender is not WasapiCapture capture) return;
        lock (_gate)
        {
            try { _raw?.Write(e.Buffer, 0, e.BytesRecorded); }
            catch (Exception) { EndRawLocked(); }
        }
        var queue = _queue;
        var resampler = _resampler;
        if (queue is null || resampler is null) return;

        // Mix every channel down to mono floats.
        var format = capture.WaveFormat;
        var channels = format.Channels;
        var bytesPerSample = format.BitsPerSample / 8;
        var frames = e.BytesRecorded / (bytesPerSample * channels);
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        var buffer = e.Buffer;
        for (var f = 0; f < frames; f++)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++)
            {
                var i = (f * channels + c) * bytesPerSample;
                sum += bytesPerSample switch
                {
                    4 when isFloat => BitConverter.ToSingle(buffer, i),
                    4 => BitConverter.ToInt32(buffer, i) / 2147483648f,
                    3 => ((buffer[i] << 8 | buffer[i + 1] << 16 | buffer[i + 2] << 24) >> 8) / 8388608f,
                    2 => BitConverter.ToInt16(buffer, i) / 32768f,
                    _ => (buffer[i] - 128) / 128f,
                };
            }
            queue.Add(sum / channels);
        }

        // Resample to the output rate and convert to 16-bit PCM.
        var wanted = (int)((long)queue.Count * _sampleRate / format.SampleRate);
        if (wanted <= 0) return;
        if (_out.Length < wanted) _out = new float[wanted * 2];
        var read = resampler.Read(_out, 0, wanted);
        if (read <= 0) return;
        if (_pcm.Length < read * 2) _pcm = new byte[read * 4];
        var peak = 0f;
        for (var i = 0; i < read; i++)
        {
            var s = Math.Clamp(_out[i], -1f, 1f);
            peak = Math.Max(peak, Math.Abs(s));
            var v = (short)Math.Round(s * 32767);
            _pcm[2 * i] = (byte)v;
            _pcm[2 * i + 1] = (byte)(v >> 8);
        }
        AudioAvailable?.Invoke(new ReadOnlyMemory<byte>(_pcm, 0, read * 2));
        LevelChanged?.Invoke(peak);
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        var capture = (WasapiCapture)sender!;
        bool wasCurrent;
        lock (_gate)
        {
            wasCurrent = ReferenceEquals(_capture, capture);
            if (wasCurrent) _capture = null;
        }
        capture.DataAvailable -= OnData;
        capture.RecordingStopped -= OnStopped;
        capture.Dispose();
        if (e.Exception is not null && wasCurrent) Faulted?.Invoke(Describe(e.Exception));
    }

    private static string Describe(Exception e) =>
        $"Couldn't use the microphone ({e.Message}). Check Settings > Privacy & security > Microphone.";

    public void Dispose() => Stop();

    /// <summary>Mono float samples waiting to be resampled. Used only on the capture thread.</summary>
    private sealed class FloatQueue(int sampleRate) : ISampleProvider
    {
        private float[] _data = new float[16384];
        private int _start, _count;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        public int Count => _count;

        public void Add(float sample)
        {
            if (_start + _count == _data.Length)
            {
                if (_start > 0) { Array.Copy(_data, _start, _data, 0, _count); _start = 0; }
                else Array.Resize(ref _data, _data.Length * 2);
            }
            _data[_start + _count++] = sample;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var n = Math.Min(count, _count);
            Array.Copy(_data, _start, buffer, offset, n);
            _start += n;
            _count -= n;
            if (_count == 0) _start = 0;
            return n;
        }
    }
}

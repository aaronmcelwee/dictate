using System.Net.Http.Headers;
using System.Text.Json;

namespace Dictate.Core.Transcription;

/// <summary>
/// xAI Grok speech-to-text in one request (POST https://api.x.ai/v1/stt): audio is collected while
/// the user talks and sent as a whole when they finish, so the model hears the full sentence.
/// Slower to finish than streaming, and more accurate. Key terms bias it towards the user's words.
/// </summary>
public sealed class XaiRestTranscriber(XaiSttOptions options, IReadOnlyList<string>? keyterms = null, string? saveClipTo = null) : ITranscriber
{
    /// <summary>Raised after a clip is transcribed, with the model and the seconds of audio billed.</summary>
    public Action<string, double>? Billed { get; init; }

    /// <summary>When set, each clip's audio (audio.wav) and the request and response (stt.json) are kept here.</summary>
    public string? DiagnosticsFolder { get; init; }

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly Uri Endpoint = new("https://api.x.ai/v1/stt");
    private readonly MemoryStream _audio = new();
    private readonly object _gate = new();

    /// <summary>Clips shorter than this are treated as silence (a stray tap).</summary>
    public static readonly TimeSpan MinimumSpeech = TimeSpan.FromMilliseconds(250);

    public event Action<string>? TextChanged { add { } remove { } }

    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        lock (_gate) _audio.Write(pcm.Span);
        return Task.CompletedTask;
    }

    /// <summary>The audio of the last finished clip (16-bit mono PCM), for diagnostics.</summary>
    public byte[]? LastAudio { get; private set; }

    public async Task<string> FinishAsync(CancellationToken ct)
    {
        byte[] audio;
        lock (_gate) audio = _audio.ToArray();
        LastAudio = audio;
        if (saveClipTo is not null) SaveWav(saveClipTo, audio, options.SampleRate);
        var diag = new Dictionary<string, object?>
        {
            ["time"] = DateTimeOffset.Now.ToString("o"),
            ["endpoint"] = Endpoint.ToString(),
            ["audio"] = Stats(audio, options.SampleRate),
        };
        // Quiet microphones lose consonants to the recognizer: bring the loudest moment up to -3 dBFS.
        var gain = Normalize(audio);
        diag["gain_db"] = Math.Round(20 * Math.Log10(gain), 1);
        if (options.PadSeconds > 0)
        {
            var pad = new byte[(int)(options.PadSeconds * options.SampleRate) * 2];
            audio = [.. pad, .. audio, .. pad];
        }
        diag["audio_sent"] = Stats(audio, options.SampleRate);
        var fields = new List<string[]>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (DiagnosticsFolder is { } folder)
        {
            try { Directory.CreateDirectory(folder); } catch (IOException) { }
            SaveWav(Path.Combine(folder, "audio.wav"), audio, options.SampleRate);
        }
        void Keep(int? status, string? body, string? error)
        {
            if (DiagnosticsFolder is null) return;
            diag["fields"] = fields;
            diag["status"] = status;
            diag["elapsed_ms"] = watch.ElapsedMilliseconds;
            diag["response"] = body;
            diag["error"] = error;
            try { File.WriteAllText(Path.Combine(DiagnosticsFolder, "stt.json"), JsonSerializer.Serialize(diag, new JsonSerializerOptions { WriteIndented = true })); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (audio.Length < options.SampleRate * 2 * MinimumSpeech.TotalSeconds)
        {
            Keep(null, null, "shorter than the minimum; not sent");
            return "";
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.FinishTimeout + TimeSpan.FromSeconds(audio.Length / (options.SampleRate * 2.0) / 4));

        // Options first: the API ignores fields after the file.
        using var form = new MultipartFormDataContent();
        void Field(string name, string value)
        {
            form.Add(new StringContent(value), name);
            fields.Add([name, value]);
        }
        Field("model", options.Model);
        if (!string.IsNullOrEmpty(options.Language))
        {
            Field("language", options.Language);
            if (options.Format) Field("format", "true");
        }
        // The one-shot endpoint's voice gate defaults to 0.5, which drops quieter stretches of speech
        // and leaves fragments; streaming uses 0.08. Turn it off: the clip is all speech the user chose to record.
        Field("vad_threshold", "0");
        foreach (var term in (keyterms ?? []).Where(t => t.Length is > 0 and <= 50).Distinct(StringComparer.OrdinalIgnoreCase).Take(100))
            Field("keyterm", term);
        // A WAV file carries its own sample rate and format, so nothing about the audio is left to
        // the server to assume.
        var file = new ByteArrayContent(Wav(audio, options.SampleRate));
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "dictation.wav");
        fields.Add(["file", $"dictation.wav (audio/wav, {options.SampleRate} Hz mono 16-bit)"]);

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        request.Headers.UserAgent.ParseAdd("GrokVoice/0.2 (Windows)");

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            Keep(null, null, e.Message);
            throw new TranscriptionException($"Couldn't reach Grok: {e.Message}", e);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Keep(null, null, "timed out");
            throw new TranscriptionException("Grok took too long to transcribe.");
        }
        using (response)
        {
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            Keep((int)response.StatusCode, json, null);
            if (!response.IsSuccessStatusCode)
                throw new TranscriptionException((int)response.StatusCode switch
                {
                    401 or 403 => "Grok refused the API key. Check it in Grok Voice's settings.",
                    429 => "Grok is rate limiting; try again in a moment.",
                    _ => $"Grok transcription error (HTTP {(int)response.StatusCode}): {Trim(json)}",
                });
            Billed?.Invoke(options.Model, audio.Length / (options.SampleRate * 2.0));
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("text", out var text) ? (text.GetString() ?? "").Trim() : "";
        }
    }

    /// <summary>Scales 16-bit PCM in place so its peak sits at -3 dBFS, by at most +24 dB; returns the gain used.</summary>
    public static double Normalize(byte[] pcm)
    {
        var peak = 0;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
            peak = Math.Max(peak, Math.Abs((int)(short)(pcm[i] | pcm[i + 1] << 8)));
        if (peak == 0) return 1;
        var gain = Math.Min(32768 * 0.708 / peak, 16.0);
        if (gain <= 1.05) return 1;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var v = (int)Math.Round((short)(pcm[i] | pcm[i + 1] << 8) * gain);
            v = Math.Clamp(v, short.MinValue, short.MaxValue);
            pcm[i] = (byte)v;
            pcm[i + 1] = (byte)(v >> 8);
        }
        return gain;
    }

    /// <summary>Level statistics of 16-bit PCM, to tell a quiet, clipped or muffled microphone from a model problem.</summary>
    public static Dictionary<string, double> Stats(byte[] pcm, int sampleRate)
    {
        var n = pcm.Length / 2;
        if (n == 0) return new() { ["seconds"] = 0 };
        double sum = 0, sumSq = 0, peak = 0, clipped = 0, hf = 0, total = 0;
        double prev = 0;
        for (var i = 0; i < n; i++)
        {
            double x = (short)(pcm[2 * i] | pcm[2 * i + 1] << 8);
            sum += x; sumSq += x * x;
            var a = Math.Abs(x);
            if (a > peak) peak = a;
            if (a >= 32767) clipped++;
            // First difference as a crude high-pass: its share of energy shows how much treble survives.
            var d = x - prev; prev = x;
            hf += d * d; total += x * x;
        }
        static double Db(double v) => v <= 0 ? -120 : 20 * Math.Log10(v / 32768);
        return new()
        {
            ["seconds"] = Math.Round((double)n / sampleRate, 2),
            ["peak_dbfs"] = Math.Round(Db(peak), 1),
            ["rms_dbfs"] = Math.Round(Db(Math.Sqrt(sumSq / n)), 1),
            ["clipped_pct"] = Math.Round(100 * clipped / n, 3),
            ["dc_offset"] = Math.Round(sum / n, 1),
            ["treble_ratio"] = Math.Round(total > 0 ? hf / total : 0, 4),
        };
    }

    /// <summary>16-bit mono PCM wrapped in a WAV header.</summary>
    public static byte[] Wav(byte[] pcm, int sampleRate)
    {
        using var stream = new MemoryStream(44 + pcm.Length);
        using (var w = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8); w.Write(36 + pcm.Length); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(sampleRate); w.Write(sampleRate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm);
        }
        return stream.ToArray();
    }

    /// <summary>Keeps the last clip as a WAV file so a bad transcript can be checked against what was recorded.</summary>
    private static void SaveWav(string path, byte[] pcm, int sampleRate)
    {
        try
        {
            using var w = new BinaryWriter(File.Create(path));
            w.Write("RIFF"u8); w.Write(36 + pcm.Length); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(sampleRate); w.Write(sampleRate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Trim(string s) => s.Length > 200 ? s[..200] + "…" : s;

    public ValueTask DisposeAsync()
    {
        _audio.Dispose();
        return ValueTask.CompletedTask;
    }
}

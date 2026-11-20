using System.Net.Http.Headers;
using System.Text.Json;

namespace Dictate.Core.Transcription;

public sealed record ElevenLabsSttOptions
{
    public required string ApiKey { get; init; }
    public string Model { get; init; } = "scribe_v2";
    /// <summary>ISO 639-1 code, or empty to let the server detect it.</summary>
    public string Language { get; init; } = "en";
    public int SampleRate { get; init; } = 16000;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// ElevenLabs Scribe speech-to-text in one request (POST https://api.elevenlabs.io/v1/speech-to-text):
/// audio is collected while the user talks and sent as a WAV file when they finish.
/// </summary>
public sealed class ElevenLabsTranscriber(ElevenLabsSttOptions options, IReadOnlyList<string>? keyterms = null) : ITranscriber
{
    private static readonly HttpClient Http = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    private static readonly Uri Endpoint = new("https://api.elevenlabs.io/v1/speech-to-text");
    private readonly MemoryStream _audio = new();
    private readonly object _gate = new();

    /// <summary>Raised after a clip is transcribed, with the model and the seconds of audio billed.</summary>
    public Action<string, double>? Billed { get; init; }
    /// <summary>Where to keep this clip's audio and the request and response, for diagnostics; null for none.</summary>
    public string? DiagnosticsFolder { get; init; }

    public event Action<string>? TextChanged { add { } remove { } }

    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        lock (_gate) _audio.Write(pcm.Span);
        return Task.CompletedTask;
    }

    public async Task<string> FinishAsync(CancellationToken ct)
    {
        byte[] audio;
        lock (_gate) audio = _audio.ToArray();
        if (audio.Length < options.SampleRate * 2 * XaiRestTranscriber.MinimumSpeech.TotalSeconds) return "";
        var before = XaiRestTranscriber.Stats(audio, options.SampleRate);
        var gain = XaiRestTranscriber.Normalize(audio);
        var wav = XaiRestTranscriber.Wav(audio, options.SampleRate);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Timeout + TimeSpan.FromSeconds(audio.Length / (options.SampleRate * 2.0) / 4));

        using var form = new MultipartFormDataContent();
        var fields = new List<string[]>();
        void Field(string name, string value)
        {
            form.Add(new StringContent(value), name);
            fields.Add([name, value]);
        }
        Field("model_id", options.Model);
        if (!string.IsNullOrEmpty(options.Language)) Field("language_code", options.Language);
        Field("tag_audio_events", "false");
        Field("timestamps_granularity", "none");
        foreach (var term in (keyterms ?? []).Where(t => t.Length is > 0 and <= 50).Distinct(StringComparer.OrdinalIgnoreCase).Take(100))
            Field("keyterms", term);
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "dictation.wav");

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = form };
        request.Headers.Add("xi-api-key", options.ApiKey);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        void Keep(int? status, string? body, string? error)
        {
            if (DiagnosticsFolder is not { } folder) return;
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, "audio.wav"), wav);
                File.WriteAllText(Path.Combine(folder, "stt.json"), JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["time"] = DateTimeOffset.Now.ToString("o"),
                    ["endpoint"] = Endpoint.ToString(),
                    ["audio"] = before,
                    ["gain_db"] = Math.Round(20 * Math.Log10(gain), 1),
                    ["fields"] = fields,
                    ["status"] = status,
                    ["elapsed_ms"] = watch.ElapsedMilliseconds,
                    ["response"] = body,
                    ["error"] = error,
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            Keep(null, null, e.Message);
            throw new TranscriptionException($"Couldn't reach ElevenLabs: {e.Message}", e);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Keep(null, null, "timed out");
            throw new TranscriptionException("ElevenLabs took too long to transcribe.");
        }
        using (response)
        {
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            Keep((int)response.StatusCode, json, null);
            if (!response.IsSuccessStatusCode)
                throw new TranscriptionException((int)response.StatusCode switch
                {
                    401 or 403 => "ElevenLabs refused the API key. Check it in Murmur's settings (it needs Speech to Text access).",
                    429 => "ElevenLabs is rate limiting; try again in a moment.",
                    _ => $"ElevenLabs transcription error (HTTP {(int)response.StatusCode}): {(json.Length > 300 ? json[..300] : json)}",
                });
            Billed?.Invoke(options.Model, audio.Length / (options.SampleRate * 2.0));
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("text", out var text) ? (text.GetString() ?? "").Trim() : "";
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

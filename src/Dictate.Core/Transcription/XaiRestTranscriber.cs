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
        if (audio.Length < options.SampleRate * 2 * MinimumSpeech.TotalSeconds) return "";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.FinishTimeout + TimeSpan.FromSeconds(audio.Length / (options.SampleRate * 2.0) / 4));

        // Options first: the API ignores fields after the file.
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(options.Model), "model");
        form.Add(new StringContent("pcm"), "audio_format");
        form.Add(new StringContent(options.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture)), "sample_rate");
        if (!string.IsNullOrEmpty(options.Language))
        {
            form.Add(new StringContent(options.Language), "language");
            form.Add(new StringContent("true"), "format");
        }
        // The one-shot endpoint's voice gate defaults to 0.5, which drops quieter stretches of speech
        // and leaves fragments; streaming uses 0.08. Turn it off: the clip is all speech the user chose to record.
        form.Add(new StringContent("0"), "vad_threshold");
        foreach (var term in (keyterms ?? []).Where(t => t.Length is > 0 and <= 50).Distinct(StringComparer.OrdinalIgnoreCase).Take(100))
            form.Add(new StringContent(term), "keyterm");
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "dictation.pcm");

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
            throw new TranscriptionException($"Couldn't reach Grok: {e.Message}", e);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TranscriptionException("Grok took too long to transcribe.");
        }
        using (response)
        {
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new TranscriptionException((int)response.StatusCode switch
                {
                    401 or 403 => "Grok refused the API key. Check it in Grok Voice's settings.",
                    429 => "Grok is rate limiting; try again in a moment.",
                    _ => $"Grok transcription error (HTTP {(int)response.StatusCode}): {Trim(json)}",
                });
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("text", out var text) ? (text.GetString() ?? "").Trim() : "";
        }
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

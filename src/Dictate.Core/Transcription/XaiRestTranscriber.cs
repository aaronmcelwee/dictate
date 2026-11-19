using System.Net.Http.Headers;
using System.Text.Json;

namespace Dictate.Core.Transcription;

/// <summary>
/// xAI Grok speech-to-text in one request (POST https://api.x.ai/v1/stt): audio is collected while
/// the user talks and sent as a whole when they finish, so the model hears the full sentence.
/// Slower to finish than streaming, and more accurate. Key terms bias it towards the user's words.
/// </summary>
public sealed class XaiRestTranscriber(XaiSttOptions options, IReadOnlyList<string>? keyterms = null) : ITranscriber
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

    public async Task<string> FinishAsync(CancellationToken ct)
    {
        byte[] audio;
        lock (_gate) audio = _audio.ToArray();
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

    private static string Trim(string s) => s.Length > 200 ? s[..200] + "…" : s;

    public ValueTask DisposeAsync()
    {
        _audio.Dispose();
        return ValueTask.CompletedTask;
    }
}

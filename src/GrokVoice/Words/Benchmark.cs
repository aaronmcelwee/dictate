using System.Text.Json;
using System.Text.RegularExpressions;
using Dictate.Core.Transcription;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GrokVoice.Words;

/// <summary>
/// Accuracy benchmark on recordings the user already has. Put clips in LocalState\benchmark\in as
/// &lt;id&gt;.wav plus &lt;id&gt;.json ({"app", "asr", "formatted", "edited"}: another tool's transcript and
/// the user's own correction), start the app, and each clip goes through the same speech-to-text and
/// clean-up as a dictation. Results land in LocalState\benchmark\out (results.jsonl, summary.json);
/// delete summary.json to run again.
/// </summary>
internal static partial class Benchmark
{
    private static int _running;

    public static string Root { get; } = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "benchmark");

    public static void StartIfRequested(Func<string?> apiKey, Func<string?> elevenKey, string language, WordDictionary words, GrokVoice.Usage.UsageLog usage)
    {
        var input = Path.Combine(Root, "in");
        var output = Path.Combine(Root, "out");
        if (!Directory.Exists(input) || File.Exists(Path.Combine(output, "summary.json"))) return;
        if (apiKey() is not { Length: > 0 }) { Log.Write("bench no API key"); return; }
        // A run prepared for ElevenLabs waits until its key is saved.
        if (File.Exists(Path.Combine(input, "wait-for-elevenlabs")) && elevenKey() is not { Length: > 0 }) { Log.Write("bench waiting for the ElevenLabs key"); return; }
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        _ = Task.Run(async () => { try { await RunAsync(input, output, apiKey, elevenKey, language, words, usage); } finally { _running = 0; } });
    }

    private sealed record Row(string Id, string App, string Reference, bool Edited, string WisprAsr, string WisprFormatted,
        string GrokHeard, string GrokCleaned, double Seconds, string? Error)
    {
        /// <summary>What other speech-to-text settings heard, by variant name.</summary>
        public Dictionary<string, string> Variants { get; } = new();
    }

    /// <summary>Speech-to-text settings tried besides the app's own, to find the most accurate.</summary>
    private static readonly (string Name, Func<XaiSttOptions, XaiSttOptions> Change, bool Keyterms)[] SttVariants =
    [
        ("keyterms_40", o => o, true),
    ];

    private static async Task RunAsync(string input, string output, Func<string?> apiKey, Func<string?> elevenKey, string language, WordDictionary words, GrokVoice.Usage.UsageLog usage)
    {
        try
        {
            Directory.CreateDirectory(output);
            var clips = Directory.GetFiles(input, "*.wav").OrderBy(p => p, StringComparer.Ordinal).ToList();
            Log.Write($"bench {clips.Count} clips");
            var rows = new List<Row>();
            using var gate = new SemaphoreSlim(4);
            var tasks = clips.Select(async wav =>
            {
                await gate.WaitAsync().ConfigureAwait(false);
                try { return await RunOneAsync(wav, apiKey, elevenKey, language, words, usage).ConfigureAwait(false); }
                finally { gate.Release(); }
            }).ToList();
            foreach (var row in await Task.WhenAll(tasks).ConfigureAwait(false)) rows.Add(row);

            var options = new JsonSerializerOptions { WriteIndented = false };
            await File.WriteAllLinesAsync(Path.Combine(output, "results.jsonl"), rows.Select(r => JsonSerializer.Serialize(new
            {
                r.Id, r.App, r.Seconds, r.Edited, reference = r.Reference,
                wispr_asr = r.WisprAsr, wispr_formatted = r.WisprFormatted, grok_heard = r.GrokHeard, grok_cleaned = r.GrokCleaned,
                wer_wispr_asr = Wer(r.WisprAsr, r.Reference), wer_wispr_formatted = Wer(r.WisprFormatted, r.Reference),
                wer_grok_heard = Wer(r.GrokHeard, r.Reference), wer_grok_cleaned = Wer(r.GrokCleaned, r.Reference),
                fer_wispr_formatted = Fer(r.WisprFormatted, r.Reference), fer_grok_cleaned = Fer(r.GrokCleaned, r.Reference),
                variants = r.Variants.ToDictionary(v => v.Key, v => new { text = v.Value, wer = Wer(v.Value, r.Reference) }),
                error = r.Error,
            }, options))).ConfigureAwait(false);

            object Totals(IEnumerable<Row> set)
            {
                var list = set.Where(r => r.Error is null && r.Reference.Length > 0).ToList();
                double Pooled(Func<Row, string> pick, Func<string, string, (int Errors, int Words)> score)
                {
                    int errors = 0, total = 0;
                    foreach (var r in list) { var (e, n) = score(pick(r), r.Reference); errors += e; total += n; }
                    return total == 0 ? 0 : Math.Round(100.0 * errors / total, 2);
                }
                return new
                {
                    clips = list.Count,
                    words = list.Sum(r => Tokens(r.Reference, false).Count),
                    wer_percent = new
                    {
                        wispr_asr = Pooled(r => r.WisprAsr, Counts), wispr_formatted = Pooled(r => r.WisprFormatted, Counts),
                        grok_heard = Pooled(r => r.GrokHeard, Counts), grok_cleaned = Pooled(r => r.GrokCleaned, Counts),
                    },
                    variant_wer_percent = list.SelectMany(r => r.Variants.Keys).Distinct().ToDictionary(name => name, name => Pooled(r => r.Variants.GetValueOrDefault(name, ""), Counts)),
                    variant_formatted_error_percent = list.SelectMany(r => r.Variants.Keys).Where(n => n.EndsWith("_cleaned")).Distinct()
                        .ToDictionary(name => name, name => Pooled(r => r.Variants.GetValueOrDefault(name, ""), FormattedCounts)),
                    // Errors counting capitals and punctuation too: what the user would have to fix.
                    formatted_error_percent = new
                    {
                        wispr_formatted = Pooled(r => r.WisprFormatted, FormattedCounts), grok_cleaned = Pooled(r => r.GrokCleaned, FormattedCounts),
                    },
                };
            }
            var summary = new
            {
                time = DateTimeOffset.Now.ToString("o"),
                version = Version(),
                failed = rows.Count(r => r.Error is not null),
                // Reference = the user's edit where there is one, else the other tool's own output (biased towards it).
                all = Totals(rows),
                edited_only = Totals(rows.Where(r => r.Edited)),
            };
            await File.WriteAllTextAsync(Path.Combine(output, "summary.json"),
                JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
            Log.Write($"bench done: {rows.Count} clips, {summary.failed} failed");
        }
        catch (Exception e)
        {
            Log.Write($"bench failed ({e.Message})");
        }
    }

    private static string Version()
    {
        try { var v = Windows.ApplicationModel.Package.Current.Id.Version; return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}"; }
        catch (Exception) { return ""; }
    }

    private static async Task<Row> RunOneAsync(string wav, Func<string?> apiKey, Func<string?> elevenKey, string language, WordDictionary words, GrokVoice.Usage.UsageLog usage)
    {
        var id = Path.GetFileNameWithoutExtension(wav);
        string app = "", asr = "", formatted = "", edited = "";
        try
        {
            var meta = Path.ChangeExtension(wav, ".json");
            if (File.Exists(meta))
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(meta).ConfigureAwait(false));
                string Get(string name) => doc.RootElement.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
                app = Get("app"); asr = Get("asr"); formatted = Get("formatted"); edited = Get("edited");
            }
            var reference = edited.Trim().Length > 0 ? edited : formatted.Length > 0 ? formatted : asr;
            var pcm = Load16kMono(wav);
            var seconds = pcm.Length / 32000.0;

            await using var stt = new XaiRestTranscriber(new XaiSttOptions { ApiKey = apiKey()!, Language = language })
            {
                Billed = (model, s) => usage.AddTranscription(model, s),
            };
            await stt.SendAudioAsync(pcm, CancellationToken.None).ConfigureAwait(false);
            var heard = (await stt.FinishAsync(CancellationToken.None).ConfigureAwait(false)).Trim();
            var variants = new Dictionary<string, string>();
            foreach (var (name, change, useKeyterms) in SttVariants)
            {
                try
                {
                    await using var other = new XaiRestTranscriber(change(new XaiSttOptions { ApiKey = apiKey()!, Language = language }),
                        useKeyterms ? words.Keyterms().Take(40).ToList() : null)
                    { Billed = (model, s) => usage.AddTranscription(model, s) };
                    await other.SendAudioAsync(pcm, CancellationToken.None).ConfigureAwait(false);
                    variants[name] = (await other.FinishAsync(CancellationToken.None).ConfigureAwait(false)).Trim();
                }
                catch (Exception e) { variants[name] = "ERROR: " + e.Message; }
            }

            var kind = GrokVoice.Context.AppAwareness.Classify(app, app);
            var context = new GrokVoice.Context.DictationContext(app, app, kind, "benchmark", "", 0);
            var polisher = new TextPolisher(apiKey, words, () => true, usage)
            {
                Budget = TimeSpan.FromSeconds(15),
                Context = () => Task.FromResult(context),
                ToBot = () => false,
                UseScreen = () => false,
            };
            var cleaned = heard.Length == 0 ? "" : await polisher.PolishAsync(heard, CancellationToken.None).ConfigureAwait(false);
            if (elevenKey() is { Length: > 0 } eleven)
                foreach (var (name, terms) in new[] { ("elevenlabs", (IReadOnlyList<string>?)null), ("elevenlabs_keyterms", words.Keyterms().Take(40).ToList()) })
                {
                    try
                    {
                        await using var scribe = new ElevenLabsTranscriber(new ElevenLabsSttOptions { ApiKey = eleven, Language = language }, terms)
                        { Billed = (model, s) => usage.AddTranscription(model, s) };
                        await scribe.SendAudioAsync(pcm, CancellationToken.None).ConfigureAwait(false);
                        var text = (await scribe.FinishAsync(CancellationToken.None).ConfigureAwait(false)).Trim();
                        variants[name] = text;
                        if (terms is null)
                            variants["elevenlabs_cleaned"] = text.Length == 0 ? "" : await polisher.PolishAsync(text, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception e) { variants[name] = "ERROR: " + e.Message; }
                }
            var row = new Row(id, app, reference, edited.Trim().Length > 0 && edited != formatted, asr, formatted, heard, cleaned, seconds, null);
            foreach (var (k, v) in variants) row.Variants[k] = v;
            return row;
        }
        catch (Exception e)
        {
            return new Row(id, app, edited.Length > 0 ? edited : formatted, false, asr, formatted, "", "", 0, e.Message);
        }
    }

    /// <summary>Any WAV as 16 kHz, 16-bit mono PCM.</summary>
    private static byte[] Load16kMono(string path)
    {
        using var reader = new WaveFileReader(path);
        var source = reader.ToSampleProvider();
        if (source.WaveFormat.Channels == 2) source = new StereoToMonoSampleProvider(source);
        else if (source.WaveFormat.Channels > 2) throw new InvalidDataException($"{source.WaveFormat.Channels} channels");
        if (source.WaveFormat.SampleRate != 16000) source = new WdlResamplingSampleProvider(source, 16000);
        var output = new MemoryStream();
        var buffer = new float[16000];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            for (var i = 0; i < read; i++)
            {
                var s = (short)Math.Clamp(Math.Round(buffer[i] * 32767), short.MinValue, short.MaxValue);
                output.WriteByte((byte)s);
                output.WriteByte((byte)(s >> 8));
            }
        return output.ToArray();
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['’][\p{L}\p{N}]+)*|[^\s\p{L}\p{N}]")]
    private static partial Regex Token();

    /// <summary>Words only, lower case (formatted = false), or words with their capitals plus punctuation marks.</summary>
    private static List<string> Tokens(string text, bool formatted)
    {
        var tokens = Token().Matches(text.Replace('’', '\'')).Select(m => m.Value);
        if (!formatted) tokens = tokens.Where(t => char.IsLetterOrDigit(t[0])).Select(t => t.ToLowerInvariant());
        return tokens.Where(t => t != "-" || formatted).ToList();
    }

    private static (int Errors, int Words) Counts(string hypothesis, string reference) => Distance(Tokens(hypothesis, false), Tokens(reference, false));
    private static (int Errors, int Words) FormattedCounts(string hypothesis, string reference) => Distance(Tokens(hypothesis, true), Tokens(reference, true));

    private static double Wer(string hypothesis, string reference) { var (e, n) = Counts(hypothesis, reference); return n == 0 ? 0 : Math.Round(100.0 * e / n, 1); }
    private static double Fer(string hypothesis, string reference) { var (e, n) = FormattedCounts(hypothesis, reference); return n == 0 ? 0 : Math.Round(100.0 * e / n, 1); }

    private static (int Errors, int Words) Distance(List<string> h, List<string> r)
    {
        var previous = new int[h.Count + 1];
        var current = new int[h.Count + 1];
        for (var j = 0; j <= h.Count; j++) previous[j] = j;
        for (var i = 1; i <= r.Count; i++)
        {
            current[0] = i;
            for (var j = 1; j <= h.Count; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (r[i - 1] == h[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return (previous[h.Count], r.Count);
    }
}

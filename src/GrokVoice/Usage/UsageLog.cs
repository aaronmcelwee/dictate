using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GrokVoice.Usage;

/// <summary>One billed API call (xAI or ElevenLabs): a transcription (audio seconds) or a clean-up pass (tokens).</summary>
internal sealed record UsageEntry(
    [property: JsonPropertyName("t")] DateTimeOffset Time,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("sec")] double AudioSeconds,
    [property: JsonPropertyName("in")] long InputTokens,
    [property: JsonPropertyName("out")] long OutputTokens,
    [property: JsonPropertyName("usd")] decimal Cost);

/// <summary>Totals for a period. Transcription is split by provider: Grok (xAI) and ElevenLabs Scribe.</summary>
internal sealed record UsageTotals(int Dictations, double AudioSeconds, long InputTokens, long OutputTokens, decimal TranscribeCost, decimal CleanUpCost,
    double ScribeSeconds = 0, decimal ScribeCost = 0)
{
    public decimal Cost => TranscribeCost + CleanUpCost;
    public decimal GrokTranscribeCost => TranscribeCost - ScribeCost;
    public long Tokens => InputTokens + OutputTokens;
}

/// <summary>
/// Every xAI call Grok Voice makes, kept as one JSON line each in the package's local folder, with
/// what it cost. Clean-up costs come from xAI's own figure on the response when it gives one, else
/// from list prices; transcription is priced per hour of audio.
/// </summary>
internal sealed class UsageLog
{
    /// <summary>xAI list prices, US dollars. Speech to text, one request per clip: $0.10 an hour.</summary>
    public const decimal TranscribePerHour = 0.10m;
    private static readonly Dictionary<string, (decimal In, decimal Out)> PerMillionTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["grok-4-1-fast-non-reasoning"] = (0.20m, 0.50m),
        ["grok-4-fast-non-reasoning"] = (0.20m, 0.50m),
        ["grok-3-mini"] = (0.30m, 0.50m),
    };

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };
    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<UsageEntry> _entries = [];

    public UsageLog(string folder)
    {
        _path = Path.Combine(folder, "usage.jsonl");
        try
        {
            if (File.Exists(_path))
                foreach (var line in File.ReadLines(_path))
                {
                    if (line.Length == 0) continue;
                    try { if (JsonSerializer.Deserialize<UsageEntry>(line, Json) is { } e) _entries.Add(e); }
                    catch (JsonException) { }
                }
        }
        catch (IOException) { }
    }

    /// <summary>Raised after each entry is added, on the caller's thread.</summary>
    public event Action? Changed;

    public void AddTranscription(string model, double audioSeconds) =>
        Add(new UsageEntry(DateTimeOffset.Now, "stt", model, audioSeconds, 0, 0,
            Math.Round((decimal)audioSeconds / 3600m * (IsScribe(model) ? ScribePerHour : TranscribePerHour), 8)));

    public static bool IsScribe(string model) => model.StartsWith("scribe", StringComparison.OrdinalIgnoreCase);

    /// <summary>ElevenLabs Scribe v2 list price, US dollars per hour of audio.</summary>
    public const decimal ScribePerHour = 0.22m;

    /// <param name="reportedCost">xAI's own cost for the call, in dollars, when the response carries one.</param>
    public void AddCleanUp(string model, long inputTokens, long outputTokens, decimal? reportedCost)
    {
        var cost = reportedCost ?? (PerMillionTokens.TryGetValue(model, out var p)
            ? (inputTokens * p.In + outputTokens * p.Out) / 1_000_000m
            : 0m);
        Add(new UsageEntry(DateTimeOffset.Now, "polish", model, 0, inputTokens, outputTokens, Math.Round(cost, 8)));
    }

    private void Add(UsageEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
            try { File.AppendAllText(_path, JsonSerializer.Serialize(entry, Json) + "\n"); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Changed?.Invoke();
    }

    /// <summary>Totals for entries at or after <paramref name="since"/> (local time).</summary>
    public UsageTotals Since(DateTimeOffset since)
    {
        lock (_gate)
        {
            int dictations = 0; double seconds = 0, scribeSeconds = 0; long tin = 0, tout = 0; decimal stt = 0, polish = 0, scribe = 0;
            foreach (var e in _entries)
            {
                if (e.Time < since) continue;
                if (e.Kind == "stt")
                {
                    dictations++; seconds += e.AudioSeconds; stt += e.Cost;
                    if (IsScribe(e.Model)) { scribeSeconds += e.AudioSeconds; scribe += e.Cost; }
                }
                else { tin += e.InputTokens; tout += e.OutputTokens; polish += e.Cost; }
            }
            return new(dictations, seconds, tin, tout, stt, polish, scribeSeconds, scribe);
        }
    }

    public UsageTotals Today => Since(new DateTimeOffset(DateTime.Today));
    public UsageTotals ThisWeek => Since(new DateTimeOffset(DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7))));
    public UsageTotals MonthToDate => Since(new DateTimeOffset(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1)));
    public UsageTotals YearToDate => Since(new DateTimeOffset(new DateTime(DateTime.Today.Year, 1, 1)));
    public UsageTotals AllTime => Since(DateTimeOffset.MinValue);

    /// <summary>The whole log as CSV, for a spreadsheet.</summary>
    public string ToCsv()
    {
        var c = CultureInfo.InvariantCulture;
        var lines = new List<string> { "time,provider,kind,model,audio_seconds,input_tokens,output_tokens,cost_usd" };
        lock (_gate)
            lines.AddRange(_entries.Select(e => string.Join(',', e.Time.ToString("o", c), IsScribe(e.Model) ? "ElevenLabs" : "xAI", e.Kind, e.Model,
                e.AudioSeconds.ToString("0.###", c), e.InputTokens.ToString(c), e.OutputTokens.ToString(c), e.Cost.ToString("0.########", c))));
        return string.Join("\n", lines) + "\n";
    }
}

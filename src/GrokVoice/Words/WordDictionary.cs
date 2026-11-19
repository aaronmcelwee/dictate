using System.Text.Json;
using System.Text.RegularExpressions;
using Windows.Storage;

namespace GrokVoice.Words;

/// <summary>One dictionary entry. A word on its own fixes spelling and capitals; with a replacement it
/// swaps the spoken phrase for the replacement (snippets are long replacements, like an email address).</summary>
internal sealed record DictionaryEntry(string Phrase, string? Replacement = null, bool IsSnippet = false);

/// <summary>
/// The user's words, replacements and snippets, applied to each dictation before it is typed or sent.
/// Stored as JSON in the package's local folder.
/// </summary>
internal sealed class WordDictionary
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private List<DictionaryEntry> _entries = [];
    private Regex? _pattern;
    private Dictionary<string, string> _lookup = new(StringComparer.OrdinalIgnoreCase);

    public WordDictionary(string? path = null)
    {
        _path = path ?? Path.Combine(ApplicationData.Current.LocalFolder.Path, "dictionary.json");
        try
        {
            if (File.Exists(_path))
                _entries = JsonSerializer.Deserialize<List<DictionaryEntry>>(File.ReadAllText(_path)) ?? [];
        }
        catch (Exception e)
        {
            Log.Write($"dict  couldn't read the dictionary: {e.Message}");
        }
        Rebuild();
    }

    public IReadOnlyList<DictionaryEntry> Entries => _entries;
    public int Words => _entries.Count(e => e.Replacement is null);
    public int Replacements => _entries.Count(e => e.Replacement is not null && !e.IsSnippet);
    public int Snippets => _entries.Count(e => e.IsSnippet);

    public event Action? Changed;

    /// <summary>Up to 100 terms to bias the recognizer: corrections and names first (the API's limit).</summary>
    public IReadOnlyList<string> Keyterms() => _entries
        .Where(e => !e.IsSnippet)
        .Select(e => e.Replacement ?? e.Phrase)
        .Where(t => t.Length is > 1 and <= 50)
        .OrderByDescending(t => t.Any(char.IsUpper) || t.Contains(' '))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(100)
        .ToList();

    /// <summary>Adds entries, replacing any with the same phrase. Returns how many were new.</summary>
    public int Merge(IEnumerable<DictionaryEntry> entries)
    {
        var byPhrase = _entries.ToDictionary(e => e.Phrase, StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var e in entries)
        {
            var phrase = e.Phrase.Trim();
            if (phrase.Length == 0) continue;
            var replacement = string.IsNullOrWhiteSpace(e.Replacement) || e.Replacement.Trim() == phrase ? null : e.Replacement.Trim();
            if (!byPhrase.ContainsKey(phrase)) added++;
            byPhrase[phrase] = new DictionaryEntry(phrase, replacement, e.IsSnippet && replacement is not null);
        }
        _entries = [.. byPhrase.Values.OrderBy(e => e.Phrase, StringComparer.OrdinalIgnoreCase)];
        Save();
        Rebuild();
        Changed?.Invoke();
        return added;
    }

    public void Clear()
    {
        _entries = [];
        Save();
        Rebuild();
        Changed?.Invoke();
    }

    /// <summary>Applies replacements, snippets and word spellings to a dictation.</summary>
    public string Apply(string text)
    {
        if (_pattern is null || text.Length == 0) return text;
        return _pattern.Replace(text, m => _lookup.TryGetValue(m.Value, out var r) ? r : m.Value);
    }

    private void Rebuild()
    {
        _lookup = new(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _entries)
            _lookup[e.Phrase] = e.Replacement ?? e.Phrase;
        if (_lookup.Count == 0)
        {
            _pattern = null;
            return;
        }
        // Longest phrases first, so "work email" wins over "email"; whole words only. Spaces in a
        // phrase also match hyphens or several spaces, since the recognizer writes either.
        var alternatives = _lookup.Keys
            .OrderByDescending(k => k.Length)
            .Select(k => string.Join(@"[\s\-]+", k.Split((char[])[' ', '-'], StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape)));
        _pattern = new Regex(@"(?<![\w@.])(?:" + string.Join("|", alternatives) + @")(?![\w@])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        // Matched text with a hyphen or extra spaces still needs to find its entry.
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in _lookup) normalized[Normalize(k)] = v;
        _lookup = new(normalized.Count, new NormalizingComparer());
        foreach (var (k, v) in normalized) _lookup[k] = v;
    }

    private static string Normalize(string s) => Regex.Replace(s.Trim(), @"[\s\-]+", " ");

    private sealed class NormalizingComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) => StringComparer.OrdinalIgnoreCase.Equals(Normalize(x ?? ""), Normalize(y ?? ""));
        public int GetHashCode(string s) => StringComparer.OrdinalIgnoreCase.GetHashCode(Normalize(s));
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries, Json));
        }
        catch (Exception e)
        {
            Log.Write($"dict  couldn't save the dictionary: {e.Message}");
        }
    }
}

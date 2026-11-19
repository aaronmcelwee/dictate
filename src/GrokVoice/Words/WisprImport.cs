using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GrokVoice.Words;

/// <summary>
/// Reads Wispr Flow's dictionary (words, replacements and snippets) from its local database, or
/// from a JSON export of its Dictionary table. Wispr Flow's own files are only read, never changed.
/// </summary>
internal static class WisprImport
{
    /// <summary>Wispr Flow's database, if it is installed for this user.</summary>
    public static string? DatabasePath
    {
        get
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wispr Flow", "flow.sqlite");
            return File.Exists(path) ? path : null;
        }
    }

    public static List<DictionaryEntry> FromDatabase(string path)
    {
        try
        {
            return Read(path);
        }
        catch (SqliteException)
        {
            // Wispr Flow may hold a lock; read a copy of the database instead.
            var copy = Path.Combine(Path.GetTempPath(), $"grokvoice-wispr-{Guid.NewGuid():N}.sqlite");
            try
            {
                File.Copy(path, copy);
                return Read(copy);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                try { File.Delete(copy); } catch { }
            }
        }
    }

    private static List<DictionaryEntry> Read(string path)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var db = new SqliteConnection(builder.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT * FROM Dictionary";
        using var reader = cmd.ExecuteReader();
        var columns = Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, i => i, StringComparer.OrdinalIgnoreCase);
        var list = new List<DictionaryEntry>();
        while (reader.Read())
        {
            string? Text(string name) => columns.TryGetValue(name, out var i) && !reader.IsDBNull(i) ? Convert.ToString(reader.GetValue(i)) : null;
            bool Flag(string name) => Text(name) is { } v && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
            if (Flag("isDeleted")) continue;
            if (Text("phrase") is not { Length: > 0 } phrase) continue;
            list.Add(new DictionaryEntry(phrase, Text("replacement"), Flag("isSnippet")));
        }
        return list;
    }

    /// <summary>A JSON array of Dictionary rows (phrase, replacement, isSnippet, isDeleted), or Grok Voice's own dictionary file.</summary>
    public static List<DictionaryEntry> FromJson(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var rows = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
            : doc.RootElement.TryGetProperty("rows", out var r) ? r
            : doc.RootElement.TryGetProperty("entries", out var e) ? e
            : throw new InvalidDataException("The file doesn't hold a list of dictionary entries.");
        var list = new List<DictionaryEntry>();
        foreach (var row in rows.EnumerateArray())
        {
            string? Text(string name) => Get(row, name) is { } v && v.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
                ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;
            bool Flag(string name) => Text(name) is { } v && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
            if (Flag("isDeleted")) continue;
            if (Text("phrase") is not { Length: > 0 } phrase) continue;
            list.Add(new DictionaryEntry(phrase, Text("replacement"), Flag("isSnippet")));
        }
        return list;
    }

    private static JsonElement? Get(JsonElement row, string name)
    {
        foreach (var p in row.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }
}

using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Documents;

namespace GrokVoice;

/// <summary>
/// Turns a bot's Markdown reply into TextBlock runs for the pill: bold, italic and code spans,
/// links as their text, headings as bold, and list items as bullets. Line breaks become spaces
/// (the pill has two lines), with bullets kept so lists still read as lists.
/// </summary>
internal static partial class MarkdownInlines
{
    public static string ToPlain(string markdown)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (text, _, _, _) in Spans(Flatten(markdown))) sb.Append(text);
        return sb.ToString();
    }

    public static void Fill(InlineCollection inlines, string markdown, int maxChars)
    {
        inlines.Clear();
        var used = 0;
        foreach (var (text, bold, italic, code) in Spans(Flatten(markdown)))
        {
            if (used >= maxChars) break;
            var t = used + text.Length > maxChars ? text[..(maxChars - used - 1)] + "…" : text;
            used += t.Length;
            var run = new Run { Text = t };
            if (bold) run.FontWeight = FontWeights.SemiBold;
            if (italic) run.FontStyle = Windows.UI.Text.FontStyle.Italic;
            if (code) run.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas");
            if (bold) run.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
            inlines.Add(run);
        }
    }

    /// <summary>Joins lines, turning list markers into bullets and headings into bold.</summary>
    private static string Flatten(string markdown)
    {
        var parts = new List<string>();
        foreach (var raw in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("```") || HorizontalRule().IsMatch(line)) continue;
            var heading = Heading().Match(line);
            if (heading.Success) line = $"**{heading.Groups[1].Value.Trim().Trim('*')}**";
            line = ListItem().Replace(line, "• ");
            line = Quote().Replace(line, "");
            parts.Add(line);
        }
        return string.Join("  ", parts);
    }

    private static IEnumerable<(string Text, bool Bold, bool Italic, bool Code)> Spans(string text)
    {
        text = Link().Replace(text, "$1");
        var pos = 0;
        foreach (Match m in Emphasis().Matches(text))
        {
            if (m.Index > pos) yield return (text[pos..m.Index], false, false, false);
            if (m.Groups["code"].Success) yield return (m.Groups["code"].Value, false, false, true);
            else if (m.Groups["bi"].Success) yield return (m.Groups["bi"].Value, true, true, false);
            else if (m.Groups["b"].Success) yield return (m.Groups["b"].Value, true, false, false);
            else yield return (m.Groups["i"].Value, false, true, false);
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) yield return (text[pos..], false, false, false);
    }

    [GeneratedRegex(@"`(?<code>[^`]+)`|\*\*\*(?<bi>[^*]+)\*\*\*|(?:\*\*|__)(?<b>.+?)(?:\*\*|__)|(?<![\w*])[*_](?<i>[^*_\s][^*_]*?)[*_](?![\w*])")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"!?\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"^#{1,6}\s+(.*)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(?:[-*+]|\d+[.)])\s+")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^>\s?")]
    private static partial Regex Quote();

    [GeneratedRegex(@"^(?:-{3,}|\*{3,}|_{3,})$")]
    private static partial Regex HorizontalRule();
}

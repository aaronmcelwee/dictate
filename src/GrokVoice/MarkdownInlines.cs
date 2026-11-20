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

    /// <summary>Plain text as paragraphs, one per line.</summary>
    public static void FillPlain(BlockCollection blocks, string text)
    {
        blocks.Clear();
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var p = new Paragraph();
            p.Inlines.Add(new Run { Text = line });
            blocks.Add(p);
        }
    }

    /// <summary>
    /// The whole reply, keeping its shape: paragraphs, headings in bold, bulleted and numbered
    /// lists with hanging indents, quotes and code in a monospaced font, plus inline emphasis.
    /// </summary>
    public static void FillBlocks(BlockCollection blocks, string markdown)
    {
        blocks.Clear();
        var inCode = false;
        var gap = false;
        foreach (var raw in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```"))
            {
                inCode = !inCode;
                continue;
            }
            if (inCode)
            {
                var code = new Paragraph { FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5 };
                code.Inlines.Add(new Run { Text = line });
                blocks.Add(code);
                continue;
            }
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || HorizontalRule().IsMatch(trimmed))
            {
                gap = blocks.Count > 0;
                continue;
            }

            var p = new Paragraph();
            if (gap) p.Margin = new Microsoft.UI.Xaml.Thickness(0, 6, 0, 0);
            gap = false;
            var indent = (raw.Length - raw.TrimStart().Length) / 2 * 14.0;

            var heading = Heading().Match(trimmed);
            var bullet = Bullet().Match(trimmed);
            var number = Numbered().Match(trimmed);
            if (heading.Success)
            {
                p.FontWeight = FontWeights.SemiBold;
                p.Foreground = White;
                AddSpans(p.Inlines, heading.Groups[1].Value.Trim());
            }
            else if (bullet.Success || number.Success)
            {
                var marker = bullet.Success ? "•" : number.Groups[1].Value + ".";
                var rest = bullet.Success ? bullet.Groups[1].Value : number.Groups[2].Value;
                p.Margin = new Microsoft.UI.Xaml.Thickness(indent + 4, p.Margin.Top, 0, 0);
                p.Inlines.Add(new Run { Text = marker + (marker.Length > 1 ? " " : "\u2002") });
                AddSpans(p.Inlines, rest);
            }
            else if (trimmed.StartsWith('>'))
            {
                p.Margin = new Microsoft.UI.Xaml.Thickness(12, p.Margin.Top, 0, 0);
                p.FontStyle = Windows.UI.Text.FontStyle.Italic;
                AddSpans(p.Inlines, Quote().Replace(trimmed, ""));
            }
            else
            {
                AddSpans(p.Inlines, trimmed);
            }
            blocks.Add(p);
        }
    }

    private static Microsoft.UI.Xaml.Media.SolidColorBrush White => new(Microsoft.UI.Colors.White);

    private static void AddSpans(InlineCollection inlines, string text)
    {
        foreach (var (t, bold, italic, code) in Spans(text))
        {
            var run = new Run { Text = t };
            if (bold) { run.FontWeight = FontWeights.SemiBold; run.Foreground = White; }
            if (italic) run.FontStyle = Windows.UI.Text.FontStyle.Italic;
            if (code) run.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas");
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

    [GeneratedRegex(@"^[-*+]\s+(.*)$")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^(\d+)[.)]\s+(.*)$")]
    private static partial Regex Numbered();

    [GeneratedRegex(@"^>\s?")]
    private static partial Regex Quote();

    [GeneratedRegex(@"^(?:-{3,}|\*{3,}|_{3,})$")]
    private static partial Regex HorizontalRule();
}

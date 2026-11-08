using System.Text;
using System.Text.RegularExpressions;

namespace MiniTui.Desk.Reader;

/// <summary>The styles a page is drawn with, from the theme (spec §13.10).</summary>
internal sealed record PageStyles(Style Heading, Style Code, Style Link, Style Muted, Style Bar)
{
    public static PageStyles From(Theme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return new(theme.TitleFocused.Bold(), theme.Info, theme.Info.Underlined(), theme.TextMuted, Style.Default.WithFg(theme.BorderFocused.Fg ?? Color.Reset));
    }
}

/// <summary>
/// Inline Markdown (spec §13.10): emphasis, code, links and images, as text in styles without the marks. A mark with
/// nothing to close it is text.
/// </summary>
internal static partial class Inline
{
    /// <summary>
    /// <paramref name="text"/> as styled pieces, <paramref name="style"/> under them all. A <c>\n</c> piece is a line
    /// break.
    /// </summary>
    public static List<Span> Parse(string text, Style style, PageStyles styles)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(styles);
        var spans = new List<Span>();
        Run(text, 0, text.Length, style, styles, spans);
        return spans;
    }

    private static void Run(string t, int from, int to, Style style, PageStyles styles, List<Span> spans)
    {
        var plain = new StringBuilder();
        void Flush()
        {
            if (plain.Length == 0) return;
            spans.Add(new Span(plain.ToString(), style));
            plain.Clear();
        }

        var i = from;
        while (i < to)
        {
            var c = t[i];
            if (c == '\\' && i + 1 < to && char.IsAsciiLetterOrDigit(t[i + 1]) is false && t[i + 1] is > ' ' and < '\x7f')
            {
                plain.Append(t[i + 1]);
                i += 2;
                continue;
            }
            if (c == '\n')
            {
                Flush();
                spans.Add(new Span("\n", style));
                i++;
                continue;
            }
            if (c == '`')
            {
                var n = RunLength(t, i, to, '`');
                var close = FindRun(t, i + n, to, '`', n);
                if (close < 0)
                {
                    plain.Append('`', n);
                    i += n;
                    continue;
                }
                var code = t[(i + n)..close].Replace('\n', ' ');
                if (code.Length >= 2 && code[0] == ' ' && code[^1] == ' ' && code.Trim().Length > 0) code = code[1..^1];
                Flush();
                spans.Add(new Span(code, style.Patch(styles.Code)));
                i = close + n;
                continue;
            }
            if ((c == '[' || (c == '!' && i + 1 < to && t[i + 1] == '[')) && Link(t, c == '!' ? i + 1 : i, to) is { } link)
            {
                Flush();
                if (c == '!')
                {
                    var alt = new List<Span>();
                    Run(t, link.TextFrom, link.TextTo, style, styles, alt);
                    spans.Add(new Span($"[image: {string.Concat(alt.Select(s => s.Content))}]", style.Patch(styles.Muted)));
                }
                else
                {
                    Run(t, link.TextFrom, link.TextTo, style.Patch(styles.Link), styles, spans);
                }
                i = link.End;
                continue;
            }
            if (c == '<' && AutoLink().Match(t, i, to - i) is { Success: true } auto)
            {
                Flush();
                spans.Add(new Span(auto.Groups[1].Value, style.Patch(styles.Link)));
                i += auto.Length;
                continue;
            }
            if (c is '*' or '_' or '~' && Emphasis(t, i, to) is { } emphasis)
            {
                Flush();
                Run(t, i + emphasis.Marks, emphasis.Close, style.Patch(emphasis.Style), styles, spans);
                i = emphasis.Close + emphasis.Marks;
                continue;
            }
            if (c is '*' or '_' or '~')
            {
                // A run that doesn't open goes as text whole, so its marks can't close a later one
                var n = RunLength(t, i, to, c);
                plain.Append(c, n);
                i += n;
                continue;
            }
            plain.Append(c);
            i++;
        }
        Flush();
    }

    private readonly record struct LinkAt(int TextFrom, int TextTo, int End);

    // [text](url "title"), at the [
    private static LinkAt? Link(string t, int open, int to)
    {
        var depth = 0;
        var close = -1;
        for (var j = open; j < to; j++)
        {
            if (t[j] == '\\') { j++; continue; }
            if (t[j] == '[') depth++;
            else if (t[j] == ']' && --depth == 0) { close = j; break; }
        }
        if (close < 0 || close + 1 >= to || t[close + 1] != '(') return null;
        var parens = 0;
        for (var j = close + 1; j < to; j++)
        {
            if (t[j] == '\\') { j++; continue; }
            if (t[j] == '(') parens++;
            else if (t[j] == ')' && --parens == 0) return new LinkAt(open + 1, close, j + 1);
        }
        return null;
    }

    private readonly record struct EmphasisAt(int Marks, int Close, Style Style);

    // *a* _a_ **a** __a__ ***a*** ~~a~~: an opener followed by text, and a closer of the same run after text
    private static EmphasisAt? Emphasis(string t, int i, int to)
    {
        var c = t[i];
        var n = RunLength(t, i, to, c);
        if (c == '~' ? n != 2 : n > 3) return null;
        if (i + n >= to || char.IsWhiteSpace(t[i + n])) return null;
        if (c == '_' && i > 0 && char.IsLetterOrDigit(t[i - 1])) return null; // snake_case
        for (var j = i + n; j < to; j++)
        {
            if (t[j] == '`')
            {
                // Marks inside code don't count
                var m = RunLength(t, j, to, '`');
                var end = FindRun(t, j + m, to, '`', m);
                j = end < 0 ? j + m - 1 : end + m - 1;
                continue;
            }
            if (t[j] != c) continue;
            var run = RunLength(t, j, to, c);
            if (run == n && !char.IsWhiteSpace(t[j - 1]) && !(c == '_' && j + n < to && char.IsLetterOrDigit(t[j + n])))
            {
                var style = c == '~' ? Style.Default.CrossedOut()
                    : n == 1 ? Style.Default.Italic()
                    : n == 2 ? Style.Default.Bold()
                    : Style.Default.Bold().Italic();
                return new EmphasisAt(n, j, style);
            }
            j += run - 1;
        }
        return null;
    }

    private static int RunLength(string t, int i, int to, char c)
    {
        var n = 0;
        while (i + n < to && t[i + n] == c) n++;
        return n;
    }

    // The next run of exactly n c's from i, or -1
    private static int FindRun(string t, int i, int to, char c, int n)
    {
        for (var j = i; j < to; j++)
        {
            if (t[j] != c) continue;
            var run = RunLength(t, j, to, c);
            if (run == n) return j;
            j += run - 1;
        }
        return -1;
    }

    [GeneratedRegex(@"\G<([a-zA-Z][a-zA-Z0-9+.-]{1,31}:[^\s<>]*|[^\s@<>]+@[^\s@<>]+\.[^\s@<>]+)>")]
    private static partial Regex AutoLink();
}

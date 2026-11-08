namespace MiniTui.Desk.Reader;

/// <summary>
/// Blocks laid out at a width (spec §13.10): the rows drawn, and which block each row belongs to. Blank rows between
/// blocks belong to none.
/// </summary>
internal sealed class Page
{
    private Page(List<Line> rows, List<int> rowBlock, List<(int First, int Last)> blockRows)
    {
        Rows = rows;
        RowBlock = rowBlock;
        BlockRows = blockRows;
    }

    public IReadOnlyList<Line> Rows { get; }

    /// <summary>Each row's block, or -1 for a blank row between blocks.</summary>
    public IReadOnlyList<int> RowBlock { get; }

    /// <summary>Each block's first and last row.</summary>
    public IReadOnlyList<(int First, int Last)> BlockRows { get; }

    public int BlockCount => BlockRows.Count;

    /// <summary>The rows of blocks <paramref name="first"/> to <paramref name="last"/> as shown, one line each.</summary>
    public string Shown(int first, int last) =>
        string.Join('\n', Enumerable.Range(BlockRows[first].First, BlockRows[last].Last - BlockRows[first].First + 1)
            .Select(r => string.Concat(Rows[r].Spans.Select(s => s.Content)).TrimEnd()));

    public static Page Layout(IReadOnlyList<Block> blocks, int width, PageStyles styles)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(styles);
        width = Math.Max(width, 8);
        var rows = new List<Line>();
        var rowBlock = new List<int>();
        var blockRows = new List<(int, int)>();
        for (var b = 0; b < blocks.Count; b++)
        {
            var block = blocks[b];
            if (b > 0 && !(block.InList && blocks[b - 1].InList && block.FirstLine == blocks[b - 1].LastLine + 1))
            {
                rows.Add(new Line(Bars(Math.Min(block.Quote, blocks[b - 1].Quote), styles)));
                rowBlock.Add(-1);
            }
            var first = rows.Count;
            foreach (var row in Block(block, width, styles))
            {
                rows.Add(row);
                rowBlock.Add(b);
            }
            blockRows.Add((first, rows.Count - 1));
        }
        return new Page(rows, rowBlock, blockRows);
    }

    private static List<Line> Block(Block block, int width, PageStyles styles)
    {
        var lead = Bars(block.Quote, styles);
        var leadWidth = 2 * block.Quote + block.Indent;
        var room = Math.Max(4, width - leadWidth);
        List<List<Span>> body = block.Kind switch
        {
            BlockKind.Heading => Heading(block, room, styles),
            BlockKind.Paragraph => Wrap.Words(Inline.Parse(block.Text, default, styles), room),
            BlockKind.Code => block.Lines.SelectMany(l => Wrap.Characters(l, room - 2)).Select(t => new List<Span> { new("  " + t, styles.Code) }).ToList(),
            BlockKind.Rule => [[new Span(new string('─', room), styles.Muted)]],
            _ => block.Lines.SelectMany(l => Wrap.Text(l, room)).Select(t => new List<Span> { new(t, default) }).ToList(),
        };
        if (body.Count == 0) body.Add([]);

        var rows = new List<Line>();
        for (var r = 0; r < body.Count; r++)
        {
            var spans = new List<Span>(lead);
            if (block.Indent > 0)
            {
                var marker = r == 0 && block.Marker is { } m ? m + " " : "";
                spans.Add(new Span(new string(' ', block.Indent - UnicodeWidth.Of(marker)) + marker, default));
            }
            spans.AddRange(body[r]);
            rows.Add(new Line(spans));
        }
        return rows;
    }

    private static List<List<Span>> Heading(Block block, int room, PageStyles styles)
    {
        var style = block.Level <= 2 ? styles.Heading : Style.Default.Bold();
        var rows = Wrap.Words(Inline.Parse(block.Text, style, styles), room);
        if (block.Level <= 2)
        {
            var wide = Math.Max(1, rows.Max(r => r.Sum(s => UnicodeWidth.Of(s.Content))));
            rows.Add([new Span(new string(block.Level == 1 ? '═' : '─', wide), styles.Heading)]);
        }
        return rows;
    }

    private static List<Span> Bars(int quote, PageStyles styles) =>
        quote == 0 ? [] : [new Span(string.Concat(Enumerable.Repeat("│ ", quote)), styles.Muted)];
}

/// <summary>Breaking text into rows of at most a width, in display columns.</summary>
internal static class Wrap
{
    /// <summary>Styled text at its spaces, a line break at each <c>\n</c> piece. A word wider than a row is broken.</summary>
    public static List<List<Span>> Words(IReadOnlyList<Span> spans, int width)
    {
        ArgumentNullException.ThrowIfNull(spans);
        // Words, each in pieces of one style; null is a line break
        var words = new List<(List<Span> Pieces, Style Space)?>();
        List<Span>? word = null;
        var space = Style.Default;
        foreach (var span in spans)
        {
            if (span.Content == "\n")
            {
                if (word is not null) words.Add((word, space));
                word = null;
                words.Add(null);
                continue;
            }
            var parts = span.Content.Split(' ');
            for (var p = 0; p < parts.Length; p++)
            {
                if (p > 0)
                {
                    if (word is not null) words.Add((word, space));
                    word = null;
                    space = span.Style;
                }
                if (parts[p].Length == 0) continue;
                word ??= [];
                word.Add(new Span(parts[p], span.Style));
            }
        }
        if (word is not null) words.Add((word, space));

        var rows = new List<List<Span>> { new() };
        var used = 0;
        foreach (var entry in words)
        {
            if (entry is not { } w)
            {
                rows.Add([]);
                used = 0;
                continue;
            }
            var wide = w.Pieces.Sum(s => UnicodeWidth.Of(s.Content));
            if (used > 0 && used + 1 + wide <= width)
            {
                rows[^1].Add(new Span(" ", w.Space));
                rows[^1].AddRange(w.Pieces);
                used += 1 + wide;
                continue;
            }
            if (used > 0)
            {
                rows.Add([]);
                used = 0;
            }
            if (wide <= width)
            {
                rows[^1].AddRange(w.Pieces);
                used = wide;
                continue;
            }
            // Too wide for a row: broken anywhere
            foreach (var piece in w.Pieces)
            {
                foreach (var grapheme in UnicodeWidth.Graphemes(piece.Content))
                {
                    var g = UnicodeWidth.OfGrapheme(grapheme);
                    if (used + g > width && used > 0)
                    {
                        rows.Add([]);
                        used = 0;
                    }
                    rows[^1].Add(new Span(grapheme, piece.Style));
                    used += g;
                }
            }
        }
        return rows;
    }

    /// <summary>A line as written, broken at the width wherever it falls.</summary>
    public static List<string> Characters(string line, int width)
    {
        ArgumentNullException.ThrowIfNull(line);
        var rows = new List<string>();
        var row = new System.Text.StringBuilder();
        var used = 0;
        foreach (var grapheme in UnicodeWidth.Graphemes(line))
        {
            var g = UnicodeWidth.OfGrapheme(grapheme);
            if (used + g > width && used > 0)
            {
                rows.Add(row.ToString());
                row.Clear();
                used = 0;
            }
            row.Append(grapheme);
            used += g;
        }
        rows.Add(row.ToString());
        return rows;
    }

    /// <summary>A line as written, its spaces kept, broken at the last space that fits, or anywhere if none does.</summary>
    public static List<string> Text(string line, int width)
    {
        ArgumentNullException.ThrowIfNull(line);
        var rows = new List<string>();
        var rest = line;
        while (UnicodeWidth.Of(rest) > width)
        {
            var graphemes = UnicodeWidth.Graphemes(rest).ToList();
            int used = 0, fit = 0, chars = 0, lastSpace = -1;
            for (; fit < graphemes.Count; fit++)
            {
                var g = UnicodeWidth.OfGrapheme(graphemes[fit]);
                if (used + g > width) break;
                if (graphemes[fit] == " " && chars > 0) lastSpace = chars;
                used += g;
                chars += graphemes[fit].Length;
            }
            if (chars == 0) chars = graphemes[0].Length; // a grapheme wider than the row
            var cut = lastSpace > 0 ? lastSpace : chars;
            rows.Add(rest[..cut].TrimEnd());
            rest = rest[cut..].TrimStart(' ');
            if (rest.Length == 0) return rows;
        }
        rows.Add(rest);
        return rows;
    }
}

namespace MiniTui.Vt;

/// <summary>
/// One screen's cells. Keeps wide characters whole: a wide cell always has its continuation cell
/// to its right, and overwriting either half blanks the other.
/// </summary>
internal sealed class VtGrid
{
    private Cell[][] _rows;
    private Cell[][] _scratch; // Holds lines during a scroll, which happens too often to allocate for.

    public VtGrid(int width, int height)
    {
        Width = width;
        Height = height;
        _rows = new Cell[height][];
        _scratch = new Cell[height][];
        for (var y = 0; y < height; y++) _rows[y] = NewRow(width, Cell.Empty);
    }

    public int Width { get; private set; }
    public int Height { get; private set; }

    public Cell this[int x, int y] => _rows[y][x];

    /// <summary>Write a cell of the given width. The caller has checked a wide cell fits.</summary>
    public void Put(int x, int y, Cell cell, int width)
    {
        var row = _rows[y];
        SplitAt(row, x);
        SplitAt(row, x + width);
        row[x] = cell;
        if (width == 2) row[x + 1] = cell with { Symbol = "" };
    }

    /// <summary>Replace a cell's symbol, for a mark joining the character already there.</summary>
    public void SetSymbol(int x, int y, string symbol)
    {
        ref var cell = ref _rows[y][x];
        cell = cell with { Symbol = symbol };
    }

    /// <summary>Turn a single-width cell into a wide one. The caller has checked it fits.</summary>
    public void Widen(int x, int y, string symbol)
    {
        var row = _rows[y];
        SplitAt(row, x + 2);
        row[x] = row[x] with { Symbol = symbol };
        row[x + 1] = row[x] with { Symbol = "" };
    }

    /// <summary>Fill columns <paramref name="from"/> to <paramref name="to"/> (exclusive) with a cell.</summary>
    public void Fill(int y, int from, int to, Cell cell)
    {
        if (from >= to) return;
        var row = _rows[y];
        SplitAt(row, from);
        SplitAt(row, to);
        row.AsSpan(from, to - from).Fill(cell);
    }

    /// <summary>Scroll lines <paramref name="top"/> to <paramref name="bottom"/> (inclusive) up.</summary>
    public void ScrollUp(int top, int bottom, int count, Cell blank)
    {
        var region = bottom - top + 1;
        count = Math.Min(count, region);
        var rows = _rows.AsSpan(top, region);
        rows[..count].CopyTo(_scratch);
        rows[count..].CopyTo(rows);
        for (var i = 0; i < count; i++)
        {
            _scratch[i].AsSpan().Fill(blank);
            rows[region - count + i] = _scratch[i];
        }
    }

    /// <summary>Scroll lines <paramref name="top"/> to <paramref name="bottom"/> (inclusive) down.</summary>
    public void ScrollDown(int top, int bottom, int count, Cell blank)
    {
        var region = bottom - top + 1;
        count = Math.Min(count, region);
        var rows = _rows.AsSpan(top, region);
        rows[(region - count)..].CopyTo(_scratch);
        rows[..(region - count)].CopyTo(rows[count..]);
        for (var i = 0; i < count; i++)
        {
            _scratch[i].AsSpan().Fill(blank);
            rows[i] = _scratch[i];
        }
    }

    /// <summary>Open a gap at <paramref name="x"/>, pushing cells off the right edge.</summary>
    public void InsertCells(int x, int y, int count, Cell blank)
    {
        var row = _rows[y];
        count = Math.Min(count, Width - x);
        SplitAt(row, x);
        // A wide character cut in two by the right edge can't stay.
        SplitAt(row, Width - count);
        row.AsSpan(x, Width - x - count).CopyTo(row.AsSpan(x + count));
        row.AsSpan(x, count).Fill(blank);
    }

    /// <summary>Remove cells at <paramref name="x"/>, pulling the rest left and blanking the end.</summary>
    public void DeleteCells(int x, int y, int count, Cell blank)
    {
        var row = _rows[y];
        count = Math.Min(count, Width - x);
        SplitAt(row, x);
        SplitAt(row, x + count);
        row.AsSpan(x + count).CopyTo(row.AsSpan(x));
        row.AsSpan(Width - count).Fill(blank);
    }

    /// <summary>
    /// Change size. Lines leave from the top only as far as needed to keep
    /// <paramref name="keepLine"/> on screen, then from the bottom. Lines aren't reflowed.
    /// </summary>
    /// <returns>How many lines left from the top.</returns>
    public int Resize(int width, int height, int keepLine)
    {
        var fromTop = Math.Clamp(keepLine - height + 1, 0, Math.Max(0, Height - height));
        var rows = new Cell[height][];
        for (var y = 0; y < height; y++)
        {
            var source = y + fromTop;
            rows[y] = source < Height ? ResizeRow(_rows[source], width) : NewRow(width, Cell.Empty);
        }
        _rows = rows;
        _scratch = new Cell[height][];
        Width = width;
        Height = height;
        return fromTop;
    }

    private Cell[] ResizeRow(Cell[] row, int width)
    {
        if (width == Width) return row;
        var result = NewRow(width, Cell.Empty);
        row.AsSpan(0, Math.Min(width, Width)).CopyTo(result);
        // Half of a wide character can't stay at the new edge.
        if (width < Width && row[width].IsContinuation) result[width - 1] = Cell.Empty;
        return result;
    }

    private static Cell[] NewRow(int width, Cell fill)
    {
        var row = new Cell[width];
        row.AsSpan().Fill(fill);
        return row;
    }

    /// <summary>
    /// Make a boundary before column <paramref name="x"/> safe to write on either side of,
    /// by blanking a wide character that straddles it.
    /// </summary>
    private void SplitAt(Cell[] row, int x)
    {
        if (x <= 0 || x >= Width || !row[x].IsContinuation) return;
        row[x - 1] = Cell.Empty;
        row[x] = Cell.Empty;
    }
}

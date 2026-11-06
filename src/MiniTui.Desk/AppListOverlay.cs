namespace MiniTui.Desk;

/// <summary>A row of an <see cref="AppListOverlay"/>.</summary>
internal readonly record struct AppListRow(string Label, string? Detail = null, string? Hint = null);

/// <summary>
/// A list to choose from, filtered by typing: the switcher and the launcher (spec §12.4). ↑ ↓ and Tab move, Enter or a
/// click chooses, Esc or a click outside closes.
/// </summary>
internal sealed class AppListOverlay : Overlay
{
    private const int MaxRows = 12;

    private readonly string _title;
    private readonly AppListRow[] _rows;
    private readonly Action<int> _choose;
    private readonly TextFieldState _filter = new();
    private List<int> _matches;
    private string _filtered = "";
    private int _selected;
    private int _offset;
    private Rect _rowsArea;

    public AppListOverlay(string title, IEnumerable<AppListRow> rows, int selected, Action<int> choose)
    {
        _title = title;
        _rows = [.. rows];
        _choose = choose;
        _matches = [.. Enumerable.Range(0, _rows.Length)];
        _selected = _rows.Length == 0 ? 0 : Math.Clamp(selected, 0, _rows.Length - 1);
    }

    public override string? Title => _title;
    public override bool CapturesText => true;
    public override bool CloseOnClickOutside => true;

    /// <summary>The rows showing, in order, for tests.</summary>
    internal IEnumerable<string> Showing => _matches.Select(i => _rows[i].Label);

    public override Rect Placement(Rect screen)
    {
        var rows = Math.Clamp(_rows.Length, 1, MaxRows);
        return Centered(screen, Math.Max(40, screen.Width * 60 / 100), rows + 3);
    }

    public override bool HandleKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                return false;
            case ConsoleKey.Enter when (key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) == 0:
                Choose();
                return true;
            case ConsoleKey.UpArrow:
                Move(-1);
                return true;
            case ConsoleKey.DownArrow:
                Move(1);
                return true;
            case ConsoleKey.Tab:
                Move((key.Modifiers & ConsoleModifiers.Shift) != 0 ? -1 : 1);
                return true;
        }
        _filter.HandleKey(key);
        Refilter();
        return true;
    }

    public override bool HandlePaste(string text)
    {
        var handled = _filter.Paste(text);
        Refilter();
        return handled;
    }

    public override bool HandleMouse(MouseEvent mouse)
    {
        ArgumentNullException.ThrowIfNull(mouse);
        switch (mouse.Action)
        {
            case MouseAction.ScrollUp:
                _selected = Math.Max(0, _selected - 1);
                return true;
            case MouseAction.ScrollDown:
                _selected = Math.Clamp(_selected + 1, 0, Math.Max(0, _matches.Count - 1));
                return true;
        }
        if (_rowsArea.Contains(mouse.X, mouse.Y) && _offset + (mouse.Y - _rowsArea.Y) is var row && row < _matches.Count)
        {
            if (mouse.Action == MouseAction.Move) _selected = row;
            if (mouse.IsClick)
            {
                _selected = row;
                Choose();
            }
        }
        return true;
    }

    public override void Render(CellBuffer buf, Rect area, RenderContext ctx)
    {
        ArgumentNullException.ThrowIfNull(buf);
        ArgumentNullException.ThrowIfNull(ctx);
        _rowsArea = default;
        if (area.IsEmpty) return;

        new TextFieldBuilder().State(_filter).Focused(ctx.Focused).Placeholder("Type to filter").Draw(buf, area with { Height = 1 });

        var theme = Theme.Current;
        var rows = new Rect(area.X, area.Y + 1, area.Width, area.Height - 1);
        if (rows.Height <= 0) return;
        if (_matches.Count == 0)
        {
            buf.SetString(rows.X, rows.Y, "Nothing matches", theme.TextMuted, rows.Width);
            return;
        }

        if (_selected < _offset) _offset = _selected;
        if (_selected >= _offset + rows.Height) _offset = _selected - rows.Height + 1;
        var visible = Math.Min(_matches.Count - _offset, rows.Height);
        _rowsArea = rows with { Height = visible };
        for (var i = 0; i < visible; i++) DrawRow(buf, rows with { Y = rows.Y + i, Height = 1 }, _offset + i, theme);
    }

    private void DrawRow(CellBuffer buf, Rect row, int index, Theme theme)
    {
        var item = _rows[_matches[index]];
        var hintWidth = item.Hint is { } hint ? UnicodeWidth.Of(hint) : 0;
        var room = row.Width - (hintWidth > 0 ? hintWidth + 2 : 0);
        var used = room > 0 ? buf.SetString(row.X, row.Y, item.Label, theme.Text, room) : 0;
        if (item.Detail is { } detail && room - used > 2)
            buf.SetString(row.X + used + 2, row.Y, detail, theme.TextMuted, room - used - 2);
        if (hintWidth > 0 && hintWidth < row.Width)
            buf.SetString(row.Right - hintWidth, row.Y, item.Hint!, theme.TextMuted, hintWidth);
        if (index == _selected) buf.SetStyle(row, theme.Selection);
    }

    private void Choose()
    {
        if (_selected >= _matches.Count) return;
        var item = _matches[_selected];
        Close();
        _choose(item);
    }

    private void Move(int by)
    {
        if (_matches.Count > 0) _selected = (_selected + by + _matches.Count) % _matches.Count;
    }

    // Best match first; an empty filter keeps the order and the first row selected
    private void Refilter()
    {
        var text = _filter.Text;
        if (text == _filtered) return;
        _filtered = text;
        _matches = [.. Enumerable.Range(0, _rows.Length)
            .Select(i => (Index: i, Match: Fuzzy.Match(text, _rows[i].Label)))
            .Where(m => m.Match is not null)
            .OrderByDescending(m => m.Match!.Value.Score)
            .ThenBy(m => m.Index)
            .Select(m => m.Index)];
        _selected = 0;
        _offset = 0;
    }
}

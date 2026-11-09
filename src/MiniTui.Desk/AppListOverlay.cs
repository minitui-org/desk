namespace MiniTui.Desk;

/// <summary>A row of an <see cref="AppListOverlay"/>.</summary>
internal readonly record struct AppListRow(string Label, string? Detail = null, string? Hint = null);

/// <summary>
/// A list to choose from, filtered by typing: the switcher and the launcher (spec §12.4), and the notifications. ↑ ↓
/// move, Tab swaps to the other list if there is one, Enter or a click chooses, Esc or a click outside closes. The
/// list is MiniTui's filter list.
/// </summary>
internal sealed class AppListOverlay : Overlay
{
    private const int MaxRows = 12;
    private const int FooterRows = 2;

    private readonly string _title;
    private readonly AppListRow[] _rows;
    private readonly Action<int> _choose;
    private readonly Action? _swap;
    private readonly string _empty;
    private readonly string[] _footer;
    private readonly FilterListState _list;
    private Rect _rowsArea;

    public AppListOverlay(
        string title,
        IEnumerable<AppListRow> rows,
        int selected,
        Action<int> choose,
        Action? swap,
        IEnumerable<string> footer,
        string empty = "No apps running")
    {
        _title = title;
        _rows = [.. rows];
        _choose = choose;
        _swap = swap;
        _footer = [.. footer];
        _empty = empty;
        _list = new FilterListState(_rows.Select(r => new FilterRow(r.Label, r.Detail, r.Hint))) { Wraps = true };
        if (_rows.Length > 0) _list.Select(Math.Clamp(selected, 0, _rows.Length - 1));
    }

    public override string? Title => _title;
    public override bool CapturesText => true;
    public override bool CloseOnClickOutside => true;

    /// <summary>The rows showing, in order, for tests.</summary>
    internal IEnumerable<string> Showing => _list.Matches.Select(i => _rows[i].Label);

    internal bool FilterIsEmpty => _list.Filter.Text.Length == 0;

    /// <summary>The launcher or the switcher, where the shell's commands work (spec §12.3).</summary>
    internal bool TakesShellCommands { get; init; }

    // The filter line, its divider, the rows and the footer
    public override Rect Placement(Rect screen)
    {
        var rows = Math.Clamp(_rows.Length, 1, MaxRows);
        return Centered(screen, Math.Max(64, screen.Width * 60 / 100), rows + FooterRows + 4);
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
            case ConsoleKey.Tab:
                _swap?.Invoke();
                return true;
        }
        _list.HandleKey(key);
        return true;
    }

    public override bool HandlePaste(string text) => _list.HandlePaste(text);

    // A click on a row chooses it; the list selects it, and the wheel and the pointer move the selection
    public override bool HandleMouse(MouseEvent mouse)
    {
        ArgumentNullException.ThrowIfNull(mouse);
        _list.HandleMouse(mouse);
        var shown = Math.Min(_list.Matches.Count, _rowsArea.Height);
        if (mouse.IsClick && _rowsArea.Contains(mouse.X, mouse.Y) && mouse.Y - _rowsArea.Y < shown) Choose();
        return true;
    }

    public override void Render(CellBuffer buf, Rect area, RenderContext ctx)
    {
        ArgumentNullException.ThrowIfNull(buf);
        ArgumentNullException.ThrowIfNull(ctx);
        _rowsArea = default;
        if (area.IsEmpty) return;

        var theme = Theme.Current;
        var footer = Math.Clamp(area.Height - 3, 0, FooterRows);
        DrawFooter(buf, new Rect(area.X, area.Bottom - footer, area.Width, footer), theme);

        // Over the frame's column of padding each side, so the rows line up with the footer and the divider meets the border
        var list = new Rect(area.X - 1, area.Y, area.Width + 2, area.Height - footer);
        Widget.FilterList(_list)
            .Placeholder("Type to filter")
            .Empty(_rows.Length == 0 ? _empty : "Nothing matches")
            .Focused(ctx.Focused)
            .Draw(buf, list);
        if (list.Height >= 2)
        {
            // Joined to the frame's sides, as the palette's is
            buf.SetString(list.X - 1, list.Y + 1, "├", theme.Border.Patch(theme.ModalBorder), 1);
            buf.SetString(list.Right, list.Y + 1, "┤", theme.Border.Patch(theme.ModalBorder), 1);
        }
        _rowsArea = list with { Y = list.Y + 2, Height = Math.Max(0, list.Height - 2) };
    }

    // Each key goes on the first row with room for it, and what doesn't fit is left to the help. An item is the key,
    // a space, and what it does: the key is drawn bold in the title's color and the rest as text, so they read as keys.
    private void DrawFooter(CellBuffer buf, Rect area, Theme theme)
    {
        var rows = new List<string>[area.Height];
        var widths = new int[area.Height];
        foreach (var item in _footer)
        {
            var width = UnicodeWidth.Of(item);
            for (var i = 0; i < rows.Length; i++)
            {
                var row = rows[i] ??= [];
                var needed = row.Count == 0 ? width : widths[i] + 2 + width;
                if (needed > area.Width) continue;
                row.Add(item);
                widths[i] = needed;
                break;
            }
        }
        var key = theme.Text.Patch(new Style(theme.ModalTitle.Fg)).Bold();
        for (var i = 0; i < rows.Length; i++)
        {
            var x = area.X;
            foreach (var item in rows[i] ?? [])
            {
                if (x > area.X) x += 2;
                var space = item.IndexOf(' ', StringComparison.Ordinal);
                var (k, does) = space < 0 ? (item, "") : (item[..space], item[space..]);
                x += buf.SetString(x, area.Y + i, k, key, area.Right - x);
                x += buf.SetString(x, area.Y + i, does, theme.Text, area.Right - x);
            }
        }
    }

    private void Choose()
    {
        if (_list.Selected is not { } item) return;
        Close();
        _choose(item);
    }
}

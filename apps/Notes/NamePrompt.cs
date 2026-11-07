namespace MiniTui.Desk.Notes;

/// <summary>
/// Asks for a note's name. Enter accepts it if <c>check</c> finds no problem, and otherwise shows the problem; Esc
/// cancels. MiniTui has no text prompt of its own (README, gaps).
/// </summary>
internal sealed class NamePrompt(string title, string initial, Func<string, string?> check, Action<string> accept) : Overlay
{
    private readonly TextFieldState _field = new(initial);
    private string? _problem;

    public override string? Title => $" {title} ";
    public override bool CapturesText => true;

    internal string Text => _field.Text;

    public override Rect Placement(Rect screen) => Centered(screen, Math.Max(40, screen.Width * 50 / 100), 6);

    public override bool HandleKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Escape) return false; // closes
        if (key.Key == ConsoleKey.Enter && (key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) == 0)
        {
            var name = _field.Text.Trim();
            _problem = check(name);
            if (_problem is not null) return true;
            Close();
            accept(name);
            return true;
        }
        if (_field.HandleKey(key))
        {
            _problem = null;
            return true;
        }
        return false;
    }

    public override bool HandlePaste(string text)
    {
        _problem = null;
        return _field.Paste(text);
    }

    public override void Render(CellBuffer buf, Rect area, RenderContext ctx)
    {
        ArgumentNullException.ThrowIfNull(buf);
        ArgumentNullException.ThrowIfNull(ctx);
        buf.SetString(area.X, area.Y, "Name", ctx.Theme.TextMuted, area.Width);
        Widget.TextField().State(_field).Focused().Draw(buf, new Rect(area.X, area.Y + 1, area.Width, 1));
        var hint = _problem ?? "Enter to accept, Esc to cancel";
        buf.SetString(area.X, area.Y + 3, hint, _problem is null ? ctx.Theme.TextMuted : ctx.Theme.Error, area.Width);
    }
}

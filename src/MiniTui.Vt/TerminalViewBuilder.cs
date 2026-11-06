namespace MiniTui.Vt;

/// <summary>Draws a <see cref="VtTerminal"/> in a <see cref="CellBuffer"/>, and keeps its size matching the area.</summary>
public sealed class TerminalViewBuilder
{
    private VtTerminal? _terminal;
    private BlockBuilder? _block;
    private bool _focused;

    /// <summary>The terminal to draw. Without one, only the block is drawn.</summary>
    public TerminalViewBuilder Terminal(VtTerminal terminal) { _terminal = terminal; return this; }

    /// <summary>Show the terminal's cursor. The default is off, as only the focused view should have one.</summary>
    public TerminalViewBuilder Focused(bool focused = true) { _focused = focused; return this; }

    /// <summary>A block around the terminal. It gets the area inside the block.</summary>
    public TerminalViewBuilder Block(BlockBuilder block) { _block = block; return this; }

    /// <summary>Resize the terminal to fit the area, copy its cells and place the cursor.</summary>
    public void Draw(CellBuffer buf, Rect area)
    {
        ArgumentNullException.ThrowIfNull(buf);
        var inner = _block is { } block ? block.Focused(_focused).Draw(buf, area) : area;
        if (_terminal is null || inner.Width < 1 || inner.Height < 1) return;
        _terminal.Resize(inner.Width, inner.Height);
        _terminal.DrawTo(buf, inner.X, inner.Y, _focused);
    }
}

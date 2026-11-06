using System.Globalization;
using System.Text;

namespace MiniTui.Vt;

/// <summary>
/// A terminal's screen: feed it what a program wrote, and read back the cells it would show.
/// Not thread-safe. Any bytes are legal input and never throw.
/// </summary>
public sealed partial class VtScreen : IVtParserHandler
{
    private const string ZeroWidthJoiner = "‍";
    private const int MaxSymbolLength = 32;
    private const int MaxSymbolCache = 4096;

    // Single-character strings for ASCII, so that printing plain text doesn't allocate.
    private static readonly string[] AsciiSymbols = Enumerable.Range(0, 128).Select(i => ((char)i).ToString()).ToArray();

    private readonly VtParser _parser;
    private readonly VtGrid _main;
    private readonly VtGrid _alternate;
    private readonly Dictionary<int, (string Symbol, int Width)> _symbols = [];
    private VtGrid _grid;
    private bool[] _tabStops;
    private int _top;
    private int _bottom;

    private int _x;
    private int _y;
    private bool _pendingWrap;
    private Pen _pen;

    // Where the last character was printed and where that left the cursor, so that a mark can join it.
    private int _lastX = -1;
    private int _lastY;
    private int _afterX;
    private int _afterY;
    private bool _afterPendingWrap;
    private Rune? _lastRune;

    /// <summary>Create a blank screen.</summary>
    /// <param name="width">Columns, at least 1.</param>
    /// <param name="height">Rows, at least 1.</param>
    public VtScreen(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        _parser = new VtParser(this);
        _main = new VtGrid(width, height);
        _alternate = new VtGrid(width, height);
        _grid = _main;
        _bottom = height - 1;
        _tabStops = NewTabStops(width);
    }

    /// <summary>Columns.</summary>
    public int Width => _grid.Width;

    /// <summary>Rows.</summary>
    public int Height => _grid.Height;

    /// <summary>
    /// The cell at a position, in the form MiniTui draws. A wide character's second column is a
    /// continuation cell. Throws if the position is off the screen.
    /// </summary>
    public Cell this[int x, int y]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)x, (uint)Width, nameof(x));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)y, (uint)Height, nameof(y));
            var cell = _grid[x, y];
            return _reverseVideo ? cell with { Modifiers = cell.Modifiers ^ Modifiers.Reversed } : cell;
        }
    }

    /// <summary>Where the cursor is, or null while it's hidden.</summary>
    public (int X, int Y)? Cursor => _cursorVisible ? (_x, _y) : null;

    /// <summary>Where the cursor is, even while it's hidden.</summary>
    internal (int X, int Y) CursorPosition => (_x, _y);

    /// <summary>The window title the program set, or null.</summary>
    public string? Title { get; private set; }

    /// <summary>The modes the program has set.</summary>
    public VtModes Modes => new(
        _grid == _alternate,
        _applicationCursorKeys,
        _applicationKeypad,
        _bracketedPaste,
        _mouseMode,
        _mouseEncoding,
        KeyboardStack.Count > 0 ? KeyboardStack[^1] : 0,
        _synchronizedOutput);

    /// <summary>Process output from the program. A sequence or character can be split across calls.</summary>
    public void Feed(ReadOnlySpan<byte> bytes) => _parser.Feed(bytes);

    /// <summary>
    /// Change size. Lines aren't reflowed: the program is told and redraws. When the screen gets
    /// shorter, lines leave from the bottom, then from the top if the cursor would otherwise be lost.
    /// </summary>
    /// <param name="width">Columns, at least 1.</param>
    /// <param name="height">Rows, at least 1.</param>
    public void Resize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (width == Width && height == Height) return;

        var mainIsActive = _grid == _main;
        var removed = _grid.Resize(width, height, _y);
        var other = mainIsActive ? _alternate : _main;
        var removedOther = other.Resize(width, height, (mainIsActive ? _savedAlternate : _savedMain)?.Y ?? 0);
        _savedMain = Moved(_savedMain, mainIsActive ? removed : removedOther, width, height);
        _savedAlternate = Moved(_savedAlternate, mainIsActive ? removedOther : removed, width, height);

        _y = Math.Clamp(_y - removed, 0, height - 1);
        _x = Math.Min(_x, width - 1);
        _pendingWrap = false;
        _lastX = -1;
        _top = 0;
        _bottom = height - 1;

        var tabStops = NewTabStops(width);
        _tabStops.AsSpan(0, Math.Min(_tabStops.Length, width)).CopyTo(tabStops);
        _tabStops = tabStops;
    }

    private static SavedCursor? Moved(SavedCursor? saved, int removed, int width, int height) =>
        saved is { } s
            ? s with { X = Math.Min(s.X, width - 1), Y = Math.Clamp(s.Y - removed, 0, height - 1), PendingWrap = false }
            : null;

    private static bool[] NewTabStops(int width)
    {
        var stops = new bool[width];
        for (var x = 8; x < width; x += 8) stops[x] = true;
        return stops;
    }

    void IVtParserHandler.Print(Rune rune) => Print(rune);

    private void Print(Rune rune)
    {
        string symbol;
        int width;
        if (rune.Value < 0x80)
        {
            var charset = _shiftOut ? _g1 : _g0;
            symbol = charset == Charset.Ascii ? AsciiSymbols[rune.Value] : Translate(charset, rune.Value);
            width = 1;
        }
        else
        {
            // C1 controls aren't obeyed in UTF-8 and have no glyph.
            if (rune.Value < 0xA0) return;
            (symbol, width) = Measure(rune);
            if (TryJoin(rune, symbol, width)) return;
            if (width == 0) return;
        }

        _lastRune = rune;
        if (_pendingWrap && _autoWrap) WrapLine();

        if (width == 2)
        {
            if (Width < 2)
            {
                symbol = " ";
                width = 1;
            }
            else if (_x == Width - 1)
            {
                // With no wrapping there's nowhere for it to go, so it's lost, as in tmux.
                if (!_autoWrap) return;
                WrapLine();
            }
        }

        if (_insert) _grid.InsertCells(_x, _y, width, Blank);
        if (_pen.Conceal)
        {
            // A hidden wide character is two spaces: a space can't be wide.
            for (var i = 0; i < width; i++) _grid.Put(_x + i, _y, _pen.Cell(" "), 1);
        }
        else
        {
            _grid.Put(_x, _y, _pen.Cell(symbol), width);
        }

        _lastX = _x;
        _lastY = _y;
        _x += width;
        if (_x >= Width)
        {
            _x = Width - 1;
            _pendingWrap = _autoWrap;
        }
        else
        {
            _pendingWrap = false;
        }
        _afterX = _x;
        _afterY = _y;
        _afterPendingWrap = _pendingWrap;
    }

    /// <summary>The cursor and everything DECSC saves with it.</summary>
    private readonly record struct SavedCursor(
        int X, int Y, Pen Pen, bool Origin, bool AutoWrap, bool PendingWrap, Charset G0, Charset G1, bool ShiftOut);

    private (string Symbol, int Width) Measure(Rune rune)
    {
        if (_symbols.TryGetValue(rune.Value, out var known)) return known;
        var symbol = rune.ToString();
        var width = IsZeroWidth(rune) ? 0 : UnicodeWidth.OfGrapheme(symbol);
        if (_symbols.Count == MaxSymbolCache) _symbols.Clear();
        return _symbols[rune.Value] = (symbol, width);
    }

    // Measured alone, because OfGrapheme reports a lone variation selector as wide. Soft hyphen is a
    // format character, but programs count it as one column.
    private static bool IsZeroWidth(Rune rune) =>
        rune.Value != 0xAD && Rune.GetUnicodeCategory(rune)
            is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format;

    /// <summary>
    /// Add a rune to the character just printed, if together they make one grapheme cluster. That can
    /// make the character wide (a variation selector, say), which then takes the next column too.
    /// </summary>
    private bool TryJoin(Rune rune, string symbol, int width)
    {
        if (_pen.Conceal || _lastX < 0 || _x != _afterX || _y != _afterY || _pendingWrap != _afterPendingWrap) return width == 0;

        var previous = _grid[_lastX, _lastY].Symbol;
        var mayJoin = width == 0
            || (rune.Value is >= 0x1F3FB and <= 0x1F3FF && previous.Length >= 2)
            || (rune.Value is >= 0x1F1E6 and <= 0x1F1FF && previous.Length == 2)
            || previous.EndsWith(ZeroWidthJoiner, StringComparison.Ordinal);
        if (!mayJoin) return false;

        // Anything that wouldn't join is dropped if it's zero-width, and otherwise printed as usual.
        if (previous.Length == 0) return width == 0;
        var joined = previous + symbol;
        if (joined.Length > MaxSymbolLength || StringInfo.GetNextTextElementLength(joined) != joined.Length) return width == 0;

        var before = Math.Clamp(UnicodeWidth.OfGrapheme(previous), 1, 2);
        var after = Math.Clamp(UnicodeWidth.OfGrapheme(joined), 1, 2);
        if (after <= before)
        {
            // A cell's width comes from its symbol, so it must never appear to get wider here.
            _grid.SetSymbol(_lastX, _lastY, joined);
            return true;
        }

        // No room to widen at the right edge: keep the character as it was.
        if (_lastX + 2 > Width) return true;
        _grid.Widen(_lastX, _lastY, joined);
        _x = _lastX + 2;
        _pendingWrap = false;
        if (_x >= Width)
        {
            _x = Width - 1;
            _pendingWrap = _autoWrap;
        }
        _afterX = _x;
        _afterPendingWrap = _pendingWrap;
        return true;
    }

    private void WrapLine()
    {
        _x = 0;
        _pendingWrap = false;
        Index();
    }

    private void Index()
    {
        _pendingWrap = false;
        if (_y == _bottom) _grid.ScrollUp(_top, _bottom, 1, Blank);
        else if (_y < Height - 1) _y++;
    }

    private void ReverseIndex()
    {
        _pendingWrap = false;
        if (_y == _top) _grid.ScrollDown(_top, _bottom, 1, Blank);
        else if (_y > 0) _y--;
    }

    private Cell Blank => _pen.Blank;
}

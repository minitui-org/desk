using System.Text;

namespace MiniTui.Vt;

public sealed partial class VtScreen
{
    private const int MaxKeyboardStack = 16;

    private enum Charset { Ascii, DecSpecial, Uk }

    // DEC Special Graphics, for 0x5F to 0x7E. ncurses draws boxes with this under xterm-256color.
    private static readonly string[] DecSpecialGraphics =
    [
        " ", "◆", "▒", "␉", "␌", "␍", "␊", "°", "±", "␤", "␋", "┘", "┐", "┌", "└", "┼",
        "⎺", "⎻", "─", "⎼", "⎽", "├", "┤", "┴", "┬", "│", "≤", "≥", "π", "≠", "£", "·"
    ];

    private readonly List<int> _keyboardMain = [];
    private readonly List<int> _keyboardAlternate = [];

    private bool _autoWrap = true;
    private bool _insert;
    private bool _newLineMode;
    private bool _origin;
    private bool _cursorVisible = true;
    private bool _reverseVideo;
    private bool _applicationCursorKeys;
    private bool _applicationKeypad;
    private bool _bracketedPaste;
    private bool _synchronizedOutput;
    private bool _focusReporting;
    private VtMouseMode _mouseMode;
    private VtMouseEncoding _mouseEncoding;
    private Charset _g0;
    private Charset _g1;
    private bool _shiftOut;
    private SavedCursor? _savedMain;
    private SavedCursor? _savedAlternate;

    private List<int> KeyboardStack => _grid == _main ? _keyboardMain : _keyboardAlternate;

    private static string Translate(Charset charset, int ascii) => charset switch
    {
        Charset.DecSpecial when ascii >= 0x5F && ascii < 0x7F => DecSpecialGraphics[ascii - 0x5F],
        Charset.Uk when ascii == '#' => "£",
        _ => AsciiSymbols[ascii]
    };

    void IVtParserHandler.Execute(byte control)
    {
        switch (control)
        {
            case 0x07:
                Host?.Bell();
                break;
            case 0x08:
                _pendingWrap = false;
                if (_x > 0) _x--;
                break;
            case 0x09:
                TabForward(1);
                break;
            case 0x0A or 0x0B or 0x0C:
                Index();
                if (_newLineMode) _x = 0;
                break;
            case 0x0D:
                _x = 0;
                _pendingWrap = false;
                break;
            case 0x0E:
                _shiftOut = true;
                break;
            case 0x0F:
                _shiftOut = false;
                break;
        }
    }

    void IVtParserHandler.EscDispatch(byte intermediate, byte final)
    {
        switch ((char)intermediate, (char)final)
        {
            case ('\0', '7'): SaveCursor(); break;
            case ('\0', '8'): RestoreCursor(); break;
            case ('\0', 'D'): Index(); break;
            case ('\0', 'E'):
                _x = 0;
                Index();
                break;
            case ('\0', 'H'): _tabStops[_x] = true; break;
            case ('\0', 'M'): ReverseIndex(); break;
            case ('\0', 'c'): FullReset(); break;
            case ('\0', '='): _applicationKeypad = true; break;
            case ('\0', '>'): _applicationKeypad = false; break;
            case ('#', '8'): AlignmentTest(); break;
            case ('(', _): _g0 = CharsetFor(final); break;
            case (')', _): _g1 = CharsetFor(final); break;
        }
    }

    private static Charset CharsetFor(byte final) => (char)final switch
    {
        '0' => Charset.DecSpecial,
        'A' => Charset.Uk,
        _ => Charset.Ascii
    };

    void IVtParserHandler.CsiDispatch(VtParams p, byte marker, byte intermediate, byte final)
    {
        switch ((char)marker, (char)intermediate, (char)final)
        {
            case ('\0', '\0', _): Csi((char)final, p); break;
            case ('?', '\0', 'h'): SetPrivateModes(p, true); break;
            case ('?', '\0', 'l'): SetPrivateModes(p, false); break;
            case ('?', '\0', 'J'): EraseInDisplay(p.Get(0)); break;
            case ('?', '\0', 'K'): EraseInLine(p.Get(0)); break;
            case ('\0', '!', 'p'): SoftReset(); break;
            case ('>', '\0', 'u'): PushKeyboardFlags(p.Get(0)); break;
            case ('<', '\0', 'u'): PopKeyboardFlags(p.Count1(0)); break;
            case ('=', '\0', 'u'): SetKeyboardFlags(p.Get(0), p.Get(1, 1)); break;
            case ('?', '\0', 'u'): ReportKeyboardFlags(); break;
            case ('>', '\0', 'c'): SecondaryDeviceAttributes(p.Get(0)); break;
            case ('\0', '$', 'p'): ReportMode(p.Get(0)); break;
            case ('?', '$', 'p'): ReportPrivateMode(p.Get(0)); break;
        }
    }

    private void Csi(char final, VtParams p)
    {
        switch (final)
        {
            case '@':
                _pendingWrap = false;
                _grid.InsertCells(_x, _y, p.Count1(0), Blank);
                break;
            case 'A': MoveUp(p.Count1(0)); break;
            case 'B' or 'e': MoveDown(p.Count1(0)); break;
            case 'C' or 'a': SetColumn(_x + p.Count1(0)); break;
            case 'D': SetColumn(_x - p.Count1(0)); break;
            case 'E':
                MoveDown(p.Count1(0));
                _x = 0;
                break;
            case 'F':
                MoveUp(p.Count1(0));
                _x = 0;
                break;
            case 'G' or '`': SetColumn(p.Count1(0) - 1); break;
            case 'H' or 'f': MoveTo(p.Count1(1) - 1, p.Count1(0) - 1); break;
            case 'I': TabForward(p.Count1(0)); break;
            case 'J': EraseInDisplay(p.Get(0)); break;
            case 'K': EraseInLine(p.Get(0)); break;
            case 'L': InsertLines(p.Count1(0)); break;
            case 'M': DeleteLines(p.Count1(0)); break;
            case 'P':
                _pendingWrap = false;
                _grid.DeleteCells(_x, _y, p.Count1(0), Blank);
                break;
            case 'S': _grid.ScrollUp(_top, _bottom, p.Count1(0), Blank); break;
            // With more parameters, this is the unrelated mouse-tracking request.
            case 'T' when p.Count <= 1: _grid.ScrollDown(_top, _bottom, p.Count1(0), Blank); break;
            case 'X':
                _pendingWrap = false;
                _grid.Fill(_y, _x, Math.Min(_x + p.Count1(0), Width), Blank);
                break;
            case 'Z': TabBackward(p.Count1(0)); break;
            case 'b': Repeat(p.Count1(0)); break;
            case 'c': DeviceAttributes(p.Get(0)); break;
            case 'd': MoveTo(_x, p.Count1(0) - 1); break;
            case 'g': ClearTabStops(p.Get(0)); break;
            case 'h': SetModes(p, true); break;
            case 'l': SetModes(p, false); break;
            case 'm': SelectGraphicRendition(p); break;
            case 'n': DeviceStatus(p.Get(0)); break;
            case 'r': SetScrollRegion(p.Count1(0) - 1, (p.Get(1) == 0 ? Height : p.Get(1)) - 1); break;
            case 's': SaveCursor(); break;
            case 'u': RestoreCursor(); break;
        }
    }

    void IVtParserHandler.OscDispatch(ReadOnlySpan<byte> data, bool bel)
    {
        var separator = data.IndexOf((byte)';');
        if (separator < 0) return;
        var command = data[..separator];
        var body = data[(separator + 1)..];
        if (command.SequenceEqual("0"u8) || command.SequenceEqual("2"u8))
        {
            var title = Clean(body);
            Title = title.Length == 0 ? null : title;
        }
        else if (command.SequenceEqual("9"u8)) NotifyFromOsc9(body);
        else if (command.SequenceEqual("10"u8)) ColorQuery(10, body, bel);
        else if (command.SequenceEqual("11"u8)) ColorQuery(11, body, bel);
        else if (command.SequenceEqual("52"u8)) ClipboardRequest(body, bel);
        else if (command.SequenceEqual("777"u8)) NotifyFromOsc777(body);
    }

    private void MoveUp(int count)
    {
        var limit = _y >= _top ? _top : 0;
        _y = Math.Max(_y - count, limit);
        _pendingWrap = false;
    }

    private void MoveDown(int count)
    {
        var limit = _y <= _bottom ? _bottom : Height - 1;
        _y = Math.Min(_y + count, limit);
        _pendingWrap = false;
    }

    private void SetColumn(int x)
    {
        _x = Math.Clamp(x, 0, Width - 1);
        _pendingWrap = false;
    }

    /// <summary>Move to a position. In origin mode, lines count from the scroll region and stay in it.</summary>
    private void MoveTo(int x, int y)
    {
        _x = Math.Clamp(x, 0, Width - 1);
        _y = _origin ? Math.Clamp(y + _top, _top, _bottom) : Math.Clamp(y, 0, Height - 1);
        _pendingWrap = false;
    }

    private void TabForward(int count)
    {
        for (var i = 0; i < count && _x < Width - 1; i++)
        {
            _pendingWrap = false;
            do _x++;
            while (_x < Width - 1 && !_tabStops[_x]);
        }
    }

    private void TabBackward(int count)
    {
        for (var i = 0; i < count && _x > 0; i++)
        {
            _pendingWrap = false;
            do _x--;
            while (_x > 0 && !_tabStops[_x]);
        }
    }

    private void ClearTabStops(int which)
    {
        if (which == 0) _tabStops[_x] = false;
        else if (which == 3) Array.Clear(_tabStops);
    }

    private void EraseInLine(int which)
    {
        _pendingWrap = false;
        switch (which)
        {
            case 0: _grid.Fill(_y, _x, Width, Blank); break;
            case 1: _grid.Fill(_y, 0, _x + 1, Blank); break;
            case 2: _grid.Fill(_y, 0, Width, Blank); break;
        }
    }

    private void EraseInDisplay(int which)
    {
        _pendingWrap = false;
        switch (which)
        {
            case 0:
                _grid.Fill(_y, _x, Width, Blank);
                FillLines(_y + 1, Height, Blank);
                break;
            case 1:
                FillLines(0, _y, Blank);
                _grid.Fill(_y, 0, _x + 1, Blank);
                break;
            case 2:
                FillLines(0, Height, Blank);
                break;
        }
    }

    private void FillLines(int from, int to, Cell cell)
    {
        for (var y = from; y < to; y++) _grid.Fill(y, 0, Width, cell);
    }

    private void InsertLines(int count)
    {
        if (_y < _top || _y > _bottom) return;
        _grid.ScrollDown(_y, _bottom, count, Blank);
        _x = 0;
        _pendingWrap = false;
    }

    private void DeleteLines(int count)
    {
        if (_y < _top || _y > _bottom) return;
        _grid.ScrollUp(_y, _bottom, count, Blank);
        _x = 0;
        _pendingWrap = false;
    }

    private void Repeat(int count)
    {
        if (_lastRune is not { } rune) return;
        count = Math.Min(count, Width * Height);
        for (var i = 0; i < count; i++) Print(rune);
    }

    private void SetScrollRegion(int top, int bottom)
    {
        bottom = Math.Min(bottom, Height - 1);
        if (top >= bottom) return;
        _top = top;
        _bottom = bottom;
        MoveTo(0, 0);
    }

    private void AlignmentTest()
    {
        FillLines(0, Height, Cell.Empty with { Symbol = "E" });
        _top = 0;
        _bottom = Height - 1;
        _x = 0;
        _y = 0;
        _pendingWrap = false;
    }

    private void SaveCursor()
    {
        var saved = new SavedCursor(_x, _y, _pen, _origin, _autoWrap, _pendingWrap, _g0, _g1, _shiftOut);
        if (_grid == _main) _savedMain = saved;
        else _savedAlternate = saved;
    }

    private void RestoreCursor()
    {
        var saved = _grid == _main ? _savedMain : _savedAlternate;
        if (saved is { } s)
        {
            _x = s.X;
            _y = s.Y;
            _pen = s.Pen;
            _origin = s.Origin;
            _autoWrap = s.AutoWrap;
            _pendingWrap = s.PendingWrap;
            _g0 = s.G0;
            _g1 = s.G1;
            _shiftOut = s.ShiftOut;
        }
        else
        {
            _x = 0;
            _y = 0;
            _pen = default;
            _origin = false;
            _pendingWrap = false;
            _g0 = _g1 = Charset.Ascii;
            _shiftOut = false;
        }
    }

    private void SetModes(VtParams p, bool on)
    {
        for (var i = 0; i < p.Count; i++)
        {
            switch (p.Get(i))
            {
                case 4: _insert = on; break;
                case 20: _newLineMode = on; break;
            }
        }
    }

    private void SetPrivateModes(VtParams p, bool on)
    {
        for (var i = 0; i < p.Count; i++)
        {
            switch (p.Get(i))
            {
                case 1: _applicationCursorKeys = on; break;
                // The size can't change here, but what the switch does to the screen still happens.
                case 3:
                    EraseInDisplay(2);
                    _top = 0;
                    _bottom = Height - 1;
                    _origin = false;
                    MoveTo(0, 0);
                    break;
                case 5: _reverseVideo = on; break;
                case 6:
                    _origin = on;
                    MoveTo(0, 0);
                    break;
                case 7:
                    _autoWrap = on;
                    if (!on) _pendingWrap = false;
                    break;
                case 9: SetMouse(VtMouseMode.X10, on); break;
                case 25: _cursorVisible = on; break;
                case 47: SwitchScreen(on); break;
                case 66: _applicationKeypad = on; break;
                case 1000: SetMouse(VtMouseMode.Normal, on); break;
                case 1002: SetMouse(VtMouseMode.ButtonEvent, on); break;
                case 1003: SetMouse(VtMouseMode.AnyEvent, on); break;
                case 1004: _focusReporting = on; break;
                case 1005: SetMouseEncoding(VtMouseEncoding.Utf8, on); break;
                case 1006: SetMouseEncoding(VtMouseEncoding.Sgr, on); break;
                case 1015: SetMouseEncoding(VtMouseEncoding.Urxvt, on); break;
                case 1047:
                    if (!on && _grid == _alternate) FillLines(0, Height, Blank);
                    SwitchScreen(on);
                    break;
                case 1048:
                    if (on) SaveCursor();
                    else RestoreCursor();
                    break;
                case 1049:
                    SetAlternateWithCursor(on);
                    break;
                case 2004: _bracketedPaste = on; break;
                case 2026:
                    if (on && !_synchronizedOutput) BeginSynchronizedOutput();
                    if (!on) _holding = false;
                    _synchronizedOutput = on;
                    break;
            }
        }
    }

    private void SetMouse(VtMouseMode mode, bool on)
    {
        if (on) _mouseMode = mode;
        else if (_mouseMode == mode) _mouseMode = VtMouseMode.None;
    }

    private void SetMouseEncoding(VtMouseEncoding encoding, bool on)
    {
        if (on) _mouseEncoding = encoding;
        else if (_mouseEncoding == encoding) _mouseEncoding = VtMouseEncoding.Default;
    }

    private void SwitchScreen(bool alternate)
    {
        var target = alternate ? _alternate : _main;
        if (_grid == target) return;
        _grid = target;
        _lastX = -1;
        _pendingWrap = false;
    }

    private void SetAlternateWithCursor(bool on)
    {
        if (on)
        {
            if (_grid == _alternate) return;
            SaveCursor();
            SwitchScreen(true);
            FillLines(0, Height, Blank);
        }
        else
        {
            if (_grid == _main) return;
            SwitchScreen(false);
            RestoreCursor();
        }
    }

    private void PushKeyboardFlags(int flags)
    {
        var stack = KeyboardStack;
        if (stack.Count == MaxKeyboardStack) stack.RemoveAt(0);
        stack.Add(flags);
    }

    private void PopKeyboardFlags(int count)
    {
        var stack = KeyboardStack;
        stack.RemoveRange(Math.Max(0, stack.Count - count), Math.Min(count, stack.Count));
    }

    private void SetKeyboardFlags(int flags, int mode)
    {
        var stack = KeyboardStack;
        var current = stack.Count > 0 ? stack[^1] : 0;
        var updated = mode switch
        {
            1 => flags,
            2 => current | flags,
            3 => current & ~flags,
            _ => current
        };
        if (stack.Count > 0) stack[^1] = updated;
        else stack.Add(updated);
    }

    /// <summary>DECSTR: modes and attributes go back to their defaults, and the screen stays.</summary>
    private void SoftReset()
    {
        _pen = default;
        _top = 0;
        _bottom = Height - 1;
        _insert = false;
        _origin = false;
        _autoWrap = true;
        _pendingWrap = false;
        _cursorVisible = true;
        _applicationCursorKeys = false;
        _applicationKeypad = false;
        _g0 = _g1 = Charset.Ascii;
        _shiftOut = false;
        _savedMain = null;
        _savedAlternate = null;
    }

    /// <summary>RIS: as new, apart from the size and the title.</summary>
    private void FullReset()
    {
        SoftReset();
        SwitchScreen(false);
        FillLines(0, Height, Cell.Empty);
        for (var y = 0; y < _alternate.Height; y++) _alternate.Fill(y, 0, _alternate.Width, Cell.Empty);
        _x = 0;
        _y = 0;
        _lastX = -1;
        _lastRune = null;
        _newLineMode = false;
        _reverseVideo = false;
        _bracketedPaste = false;
        _synchronizedOutput = false;
        _holding = false;
        _focusReporting = false;
        _mouseMode = VtMouseMode.None;
        _mouseEncoding = VtMouseEncoding.Default;
        _keyboardMain.Clear();
        _keyboardAlternate.Clear();
        _tabStops = NewTabStops(Width);
    }
}

namespace MiniTui.Vt;

public sealed partial class VtScreen
{
    private static readonly Color[] NamedColors =
    [
        Color.Black, Color.DarkRed, Color.DarkGreen, Color.DarkYellow,
        Color.DarkBlue, Color.DarkMagenta, Color.DarkCyan, Color.Gray,
        Color.DarkGray, Color.Red, Color.Green, Color.Yellow,
        Color.Blue, Color.Magenta, Color.Cyan, Color.White
    ];

    /// <summary>The attributes new characters get. <c>default</c> is the terminal's defaults.</summary>
    private readonly record struct Pen(Color Fg, Color Bg, Modifiers Modifiers, bool Conceal)
    {
        public Cell Cell(string symbol) => new(symbol, Fg, Bg, Modifiers);

        /// <summary>What erasing leaves: a blank in the current background (BCE), as xterm-256color promises.</summary>
        public Cell Blank => new(" ", Color.Reset, Bg, Modifiers.None);
    }

    private void SelectGraphicRendition(VtParams p)
    {
        if (p.Count == 0)
        {
            _pen = default;
            return;
        }

        var i = 0;
        while (i < p.Count)
        {
            var code = p.Get(i);
            var subs = 0;
            while (p.IsSub(i + 1 + subs)) subs++;

            switch (code)
            {
                case 0: _pen = default; break;
                case 1: Add(Modifiers.Bold); break;
                case 2: Add(Modifiers.Dim); break;
                case 3: Add(Modifiers.Italic); break;
                // 4:0 is "no underline". Other styles (double, curly...) become a plain underline.
                case 4:
                    if (subs > 0 && p.Get(i + 1) == 0) Remove(Modifiers.Underlined);
                    else Add(Modifiers.Underlined);
                    break;
                case 7: Add(Modifiers.Reversed); break;
                case 8: _pen = _pen with { Conceal = true }; break;
                case 9: Add(Modifiers.CrossedOut); break;
                case 21: Add(Modifiers.Underlined); break;
                case 22: Remove(Modifiers.Bold | Modifiers.Dim); break;
                case 23: Remove(Modifiers.Italic); break;
                case 24: Remove(Modifiers.Underlined); break;
                case 27: Remove(Modifiers.Reversed); break;
                case 28: _pen = _pen with { Conceal = false }; break;
                case 29: Remove(Modifiers.CrossedOut); break;
                case >= 30 and <= 37: _pen = _pen with { Fg = NamedColors[code - 30] }; break;
                case 39: _pen = _pen with { Fg = Color.Reset }; break;
                case >= 40 and <= 47: _pen = _pen with { Bg = NamedColors[code - 40] }; break;
                case 49: _pen = _pen with { Bg = Color.Reset }; break;
                case >= 90 and <= 97: _pen = _pen with { Fg = NamedColors[code - 90 + 8] }; break;
                case >= 100 and <= 107: _pen = _pen with { Bg = NamedColors[code - 100 + 8] }; break;
                case 38 or 48:
                    var color = subs > 0 ? ColonColor(p, i + 1, subs) : SemicolonColor(p, i + 1, out subs);
                    if (color is { } c) _pen = code == 38 ? _pen with { Fg = c } : _pen with { Bg = c };
                    break;
            }
            i += subs + 1;
        }
    }

    private void Add(Modifiers modifiers) => _pen = _pen with { Modifiers = _pen.Modifiers | modifiers };

    private void Remove(Modifiers modifiers) => _pen = _pen with { Modifiers = _pen.Modifiers & ~modifiers };

    /// <summary><c>38:5:n</c>, <c>38:2:r:g:b</c>, or <c>38:2:colorspace:r:g:b</c>.</summary>
    private static Color? ColonColor(VtParams p, int first, int subs) => p.Get(first) switch
    {
        5 when subs >= 2 => Indexed(p.Get(first + 1)),
        2 when subs >= 5 => Rgb(p.Get(first + 2), p.Get(first + 3), p.Get(first + 4)),
        2 when subs == 4 => Rgb(p.Get(first + 1), p.Get(first + 2), p.Get(first + 3)),
        _ => null
    };

    /// <summary><c>38;5;n</c> or <c>38;2;r;g;b</c>. A cut-off one uses up the rest of the sequence.</summary>
    private static Color? SemicolonColor(VtParams p, int first, out int used)
    {
        var available = p.Count - first;
        switch (p.Get(first))
        {
            case 5 when available >= 2:
                used = 2;
                return Indexed(p.Get(first + 1));
            case 2 when available >= 4:
                used = 4;
                return Rgb(p.Get(first + 1), p.Get(first + 2), p.Get(first + 3));
            default:
                used = available;
                return null;
        }
    }

    private static Color? Indexed(int index) => index <= 255 ? Color.Indexed((byte)index) : null;

    private static Color? Rgb(int r, int g, int b) =>
        r <= 255 && g <= 255 && b <= 255 ? Color.Rgb((byte)r, (byte)g, (byte)b) : null;
}

using System.Globalization;
using System.Text;

namespace MiniTui.Vt;

// Query replies (spec §11.4), and what a program asks of its host.
public sealed partial class VtScreen
{
    // xterm's defaults, for a host that doesn't know better.
    private static readonly Color FallbackForeground = Color.Rgb(0xE5, 0xE5, 0xE5);
    private static readonly Color FallbackBackground = Color.Rgb(0, 0, 0);

    /// <summary>Told about replies, the clipboard, the bell and notifications. Without one, they're dropped.</summary>
    public IVtHost? Host { get; set; }

    /// <summary>The color reported as the default foreground (OSC 10). Only an RGB color can be reported; anything else reports xterm's default.</summary>
    public Color DefaultForeground { get; set; } = FallbackForeground;

    /// <summary>The color reported as the default background (OSC 11), which is how programs choose a light or dark look. Only an RGB color can be reported; anything else reports xterm's default.</summary>
    public Color DefaultBackground { get; set; } = FallbackBackground;

    private void Reply(string text) => Host?.Reply(Encoding.UTF8.GetBytes(text));

    private void DeviceAttributes(int which)
    {
        // VT220 with ANSI color. Nothing else: an app that sees 4 (Sixel) or 1 (132 columns) would use them.
        if (which == 0) Reply("\e[?62;22c");
    }

    private void SecondaryDeviceAttributes(int which)
    {
        if (which == 0) Reply("\e[>1;10;0c");
    }

    private void DeviceStatus(int which)
    {
        switch (which)
        {
            case 5:
                Reply("\e[0n");
                break;
            case 6:
                var line = _origin ? _y - _top : _y;
                Reply(string.Create(CultureInfo.InvariantCulture, $"\e[{line + 1};{_x + 1}R"));
                break;
        }
    }

    private void ReportMode(int mode)
    {
        var state = mode switch
        {
            4 => Setting(_insert),
            20 => Setting(_newLineMode),
            _ => 0
        };
        Reply(string.Create(CultureInfo.InvariantCulture, $"\e[{mode};{state}$y"));
    }

    private void ReportPrivateMode(int mode)
    {
        var state = mode switch
        {
            1 => Setting(_applicationCursorKeys),
            3 => 4, // The shell decides the size, so 132 columns is never on.
            5 => Setting(_reverseVideo),
            6 => Setting(_origin),
            7 => Setting(_autoWrap),
            9 => Setting(_mouseMode == VtMouseMode.X10),
            25 => Setting(_cursorVisible),
            47 or 1047 or 1049 => Setting(_grid == _alternate),
            66 => Setting(_applicationKeypad),
            1000 => Setting(_mouseMode == VtMouseMode.Normal),
            1002 => Setting(_mouseMode == VtMouseMode.ButtonEvent),
            1003 => Setting(_mouseMode == VtMouseMode.AnyEvent),
            1004 => Setting(_focusReporting),
            1005 => Setting(_mouseEncoding == VtMouseEncoding.Utf8),
            1006 => Setting(_mouseEncoding == VtMouseEncoding.Sgr),
            1015 => Setting(_mouseEncoding == VtMouseEncoding.Urxvt),
            2004 => Setting(_bracketedPaste),
            2026 => Setting(_synchronizedOutput),
            _ => 0
        };
        Reply(string.Create(CultureInfo.InvariantCulture, $"\e[?{mode};{state}$y"));
    }

    // DECRPM's values: 1 is set, 2 is reset.
    private static int Setting(bool on) => on ? 1 : 2;

    private void ReportKeyboardFlags() =>
        Reply(string.Create(CultureInfo.InvariantCulture, $"\e[?{Modes.KeyboardFlags}u"));

    /// <summary>OSC 10 and 11: a query gets the color, with the terminator it came with. Setting them does nothing.</summary>
    private void ColorQuery(int which, ReadOnlySpan<byte> value, bool bel)
    {
        if (!value.SequenceEqual("?"u8)) return;
        var color = which == 10 ? DefaultForeground : DefaultBackground;
        var fallback = which == 10 ? FallbackForeground : FallbackBackground;

        // Color has no way to read its parts, but its text form is always #RRGGBB for an RGB color.
        var rgb = Convert.FromHexString((color.Kind == ColorKind.Rgb ? color : fallback).ToString().AsSpan(1));
        Reply(string.Create(CultureInfo.InvariantCulture,
            $"\e]{which};rgb:{rgb[0]:x2}{rgb[0]:x2}/{rgb[1]:x2}{rgb[1]:x2}/{rgb[2]:x2}{rgb[2]:x2}{(bel ? "\a" : "\e\\")}"));
    }

    /// <summary>OSC 52: writes go to the host. Reads are refused with an empty answer, so a program can't read the clipboard.</summary>
    private void ClipboardRequest(ReadOnlySpan<byte> body, bool bel)
    {
        var separator = body.IndexOf((byte)';');
        if (separator < 0) return;
        var selection = Encoding.UTF8.GetString(body[..separator]);
        var data = body[(separator + 1)..];
        if (data.SequenceEqual("?"u8))
        {
            Reply($"\e]52;{selection};{(bel ? "\a" : "\e\\")}");
            return;
        }

        var bytes = new byte[data.Length / 4 * 3 + 3];
        var base64 = Encoding.Latin1.GetString(data);
        if (Convert.TryFromBase64String(base64, bytes, out var written))
            Host?.SetClipboard(Encoding.UTF8.GetString(bytes, 0, written));
    }

    /// <summary>OSC 9. ConEmu uses the same number for other things, such as <c>9;4</c> for progress, and those aren't notifications.</summary>
    private void NotifyFromOsc9(ReadOnlySpan<byte> body)
    {
        var separator = body.IndexOf((byte)';');
        var head = separator < 0 ? body : body[..separator];
        if (!head.IsEmpty && head.Length <= 2 && head.IndexOfAnyExceptInRange((byte)'0', (byte)'9') < 0) return;
        Notify(null, Clean(body));
    }

    /// <summary>OSC 777: <c>notify;title;body</c>. The body can contain semicolons.</summary>
    private void NotifyFromOsc777(ReadOnlySpan<byte> body)
    {
        if (!body.StartsWith("notify;"u8)) return;
        var rest = body["notify;"u8.Length..];
        var separator = rest.IndexOf((byte)';');
        if (separator < 0)
        {
            Notify(null, Clean(rest));
            return;
        }
        var title = Clean(rest[..separator]);
        Notify(title.Length == 0 ? null : title, Clean(rest[(separator + 1)..]));
    }

    private void Notify(string? title, string body)
    {
        if (title is not null || body.Length > 0) Host?.Notify(title, body);
    }

    private static string Clean(ReadOnlySpan<byte> text) =>
        string.Concat(Encoding.UTF8.GetString(text).Where(c => !char.IsControl(c)));

    // ---- Synchronized output (mode 2026) ----

    private Cell[]? _heldCells;
    private (int X, int Y)? _heldCursor;
    private bool _holding;

    /// <summary>A frame is being held, and when it started, from <see cref="Environment.TickCount64"/>.</summary>
    internal bool IsHolding => _holding;

    /// <summary>When the frame being held was taken.</summary>
    internal long HoldStarted { get; private set; }

    private void BeginSynchronizedOutput()
    {
        var count = Width * Height;
        if (_heldCells is null || _heldCells.Length != count) _heldCells = new Cell[count];
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                _heldCells[y * Width + x] = this[x, y];
        _heldCursor = Cursor;
        _holding = true;
        HoldStarted = Environment.TickCount64;
    }

    /// <summary>Copy the screen into a buffer, or the frame being held if <paramref name="held"/> and there is one.</summary>
    /// <returns>Where the cursor is, or null while it's hidden.</returns>
    internal (int X, int Y)? CopyTo(CellBuffer buffer, int left, int top, bool held)
    {
        var useHeld = held && _holding;
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                buffer.Set(left + x, top + y, useHeld ? _heldCells![y * Width + x] : this[x, y]);
        return useHeld ? _heldCursor : Cursor;
    }
}

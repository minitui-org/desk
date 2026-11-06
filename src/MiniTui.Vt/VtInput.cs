using System.Buffers;
using System.Globalization;
using System.Text;

namespace MiniTui.Vt;

/// <summary>
/// Turns decoded keys, mouse events and pastes back into the bytes a program expects (spec §11.3). A program that
/// hasn't asked for more gets what xterm would send it. One that pushed kitty keyboard flags gets kitty's forms.
/// </summary>
public static class VtInput
{
    private const int KittyDisambiguate = 1;
    private const int KittyReportAll = 8;
    private const int KittyText = 16;
    private const int KittyF13 = 57376;
    private const int KittyKeypad0 = 57399;

    // Past these, the older mouse encodings have no way to say where the pointer is.
    private const int MaxDefaultCoordinate = 223 - 1;
    private const int MaxUtf8Coordinate = 2015 - 1;

    /// <summary>Encode a key for a program in the given modes.</summary>
    /// <remarks>
    /// MiniTui reports a character outside the Basic Multilingual Plane as two keys, one per surrogate, and a lone
    /// surrogate has no encoding, so this returns false for one. <see cref="VtTerminal.HandleKey"/> joins them.
    /// </remarks>
    /// <returns>True if anything was written.</returns>
    public static bool TryEncode(ConsoleKeyInfo key, VtModes modes, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var mods = key.Modifiers;
        var parameter = ModifierParameter(mods);
        var kitty = (modes.KeyboardFlags & (KittyDisambiguate | KittyReportAll)) != 0;
        var all = (modes.KeyboardFlags & KittyReportAll) != 0;

        switch (key.Key)
        {
            case ConsoleKey.UpArrow: Cursor(output, 'A', parameter, modes); return true;
            case ConsoleKey.DownArrow: Cursor(output, 'B', parameter, modes); return true;
            case ConsoleKey.RightArrow: Cursor(output, 'C', parameter, modes); return true;
            case ConsoleKey.LeftArrow: Cursor(output, 'D', parameter, modes); return true;
            case ConsoleKey.Home: Cursor(output, 'H', parameter, modes); return true;
            case ConsoleKey.End: Cursor(output, 'F', parameter, modes); return true;
            case ConsoleKey.Insert: Tilde(output, 2, parameter); return true;
            case ConsoleKey.Delete: Tilde(output, 3, parameter); return true;
            case ConsoleKey.PageUp: Tilde(output, 5, parameter); return true;
            case ConsoleKey.PageDown: Tilde(output, 6, parameter); return true;
            case >= ConsoleKey.F1 and <= ConsoleKey.F4:
                FunctionKey(output, key.Key - ConsoleKey.F1, parameter, kitty);
                return true;
            case >= ConsoleKey.F5 and <= ConsoleKey.F12:
                Tilde(output, TildeCode(key.Key), parameter);
                return true;
            case >= ConsoleKey.F13 and <= ConsoleKey.F24:
                if (kitty) CsiU(output, KittyF13 + (key.Key - ConsoleKey.F13), parameter);
                else FarFunctionKey(output, key.Key, mods);
                return true;
            case ConsoleKey.Enter: Control(output, 13, (byte)'\r', mods, kitty, all); return true;
            case ConsoleKey.Tab: Tab(output, mods, kitty, all); return true;
            case ConsoleKey.Backspace: Backspace(output, mods, kitty, all); return true;
            case ConsoleKey.Escape:
                if (kitty) CsiU(output, 27, parameter);
                else Prefixed(output, mods, 0x1B);
                return true;
            case ConsoleKey.Spacebar:
                if (kitty && (all || HasCommand(mods))) CsiU(output, 32, parameter);
                else Prefixed(output, mods, Has(mods, ConsoleModifiers.Control) ? (byte)0 : (byte)' ');
                return true;
            case >= ConsoleKey.A and <= ConsoleKey.Z:
                Letter(output, key, modes, kitty, all);
                return true;
            case >= ConsoleKey.D0 and <= ConsoleKey.D9:
                Digit(output, key, modes, kitty, all);
                return true;
            case >= ConsoleKey.NumPad0 and <= ConsoleKey.Divide:
                if (Keypad(output, key, modes, kitty, all)) return true;
                break;
        }

        return Character(output, key, modes, kitty, all);
    }

    /// <summary>Encode a mouse event for a program in the given modes. Positions are relative to the program's screen.</summary>
    /// <returns>False if the program didn't ask for this event, or its encoding can't express the position.</returns>
    public static bool TryEncode(MouseEvent mouse, VtModes modes, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(mouse);
        ArgumentNullException.ThrowIfNull(output);
        if (mouse.X < 0 || mouse.Y < 0 || !WantsMouse(mouse.Action, modes.Mouse)) return false;

        var button = mouse.Action switch
        {
            MouseAction.ScrollUp => 64,
            MouseAction.ScrollDown => 65,
            MouseAction.ScrollLeft => 66,
            MouseAction.ScrollRight => 67,
            MouseAction.Move => 35,
            _ => ButtonCode(mouse.Button) | (mouse.Action == MouseAction.Drag ? 32 : 0)
        };
        if (modes.Mouse != VtMouseMode.X10)
        {
            if (Has(mouse.Modifiers, ConsoleModifiers.Shift)) button |= 4;
            if (Has(mouse.Modifiers, ConsoleModifiers.Alt)) button |= 8;
            if (Has(mouse.Modifiers, ConsoleModifiers.Control)) button |= 16;
        }
        var release = mouse.Action == MouseAction.Release;

        switch (modes.MouseEncoding)
        {
            case VtMouseEncoding.Sgr:
                Ascii(output, string.Create(CultureInfo.InvariantCulture, $"\e[<{button};{mouse.X + 1};{mouse.Y + 1}{(release ? 'm' : 'M')}"));
                return true;
            case VtMouseEncoding.Urxvt:
                if (release) button = button & ~3 | 3;
                Ascii(output, string.Create(CultureInfo.InvariantCulture, $"\e[{button + 32};{mouse.X + 1};{mouse.Y + 1}M"));
                return true;
            case VtMouseEncoding.Utf8:
                if (mouse.X > MaxUtf8Coordinate || mouse.Y > MaxUtf8Coordinate) return false;
                if (release) button = button & ~3 | 3;
                Ascii(output, "\e[M");
                Text(output, new Rune(button + 32));
                Text(output, new Rune(mouse.X + 33));
                Text(output, new Rune(mouse.Y + 33));
                return true;
            default:
                if (mouse.X > MaxDefaultCoordinate || mouse.Y > MaxDefaultCoordinate) return false;
                if (release) button = button & ~3 | 3;
                Ascii(output, "\e[M");
                Bytes(output, (byte)(button + 32), (byte)(mouse.X + 33), (byte)(mouse.Y + 33));
                return true;
        }
    }

    /// <summary>
    /// Encode a paste. It's wrapped if the program enabled bracketed paste, line breaks become carriage returns as
    /// they would from a keyboard, and escapes are removed so that pasted text can't end the paste or run commands.
    /// </summary>
    public static void EncodePaste(string text, VtModes modes, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(output);
        var clean = text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r')
            .Replace("\u001b", "", StringComparison.Ordinal).Replace("\u009b", "", StringComparison.Ordinal);
        if (clean.Length == 0) return;

        if (modes.BracketedPaste) Ascii(output, "\e[200~");
        Encoding.UTF8.GetBytes(clean, output);
        if (modes.BracketedPaste) Ascii(output, "\e[201~");
    }

    /// <summary>Encode a focus change for a program that asked for them (mode 1004).</summary>
    /// <returns>False if it didn't ask.</returns>
    public static bool TryEncodeFocus(bool focused, VtModes modes, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!modes.FocusReporting) return false;
        Ascii(output, focused ? "\e[I" : "\e[O");
        return true;
    }

    /// <summary>Encode a character from outside the Basic Multilingual Plane, which has no <see cref="ConsoleKeyInfo"/> of its own.</summary>
    internal static void EncodeRune(Rune rune, ConsoleModifiers mods, VtModes modes, IBufferWriter<byte> output)
    {
        var kitty = (modes.KeyboardFlags & (KittyDisambiguate | KittyReportAll)) != 0;
        var all = (modes.KeyboardFlags & KittyReportAll) != 0;
        if (kitty && (all || HasCommand(mods))) CsiU(output, rune.Value, ModifierParameter(mods), TextFor(rune.Value, mods, modes));
        else
        {
            if (Has(mods, ConsoleModifiers.Alt)) Bytes(output, 0x1B);
            Text(output, rune);
        }
    }

    private static bool WantsMouse(MouseAction action, VtMouseMode mode) => action switch
    {
        MouseAction.Press or MouseAction.ScrollUp or MouseAction.ScrollDown or MouseAction.ScrollLeft or MouseAction.ScrollRight => mode != VtMouseMode.None,
        MouseAction.Release => mode is VtMouseMode.Normal or VtMouseMode.ButtonEvent or VtMouseMode.AnyEvent,
        MouseAction.Drag => mode is VtMouseMode.ButtonEvent or VtMouseMode.AnyEvent,
        MouseAction.Move => mode == VtMouseMode.AnyEvent,
        _ => false
    };

    private static int ButtonCode(MouseButton button) => button switch
    {
        MouseButton.Left => 0,
        MouseButton.Middle => 1,
        MouseButton.Right => 2,
        _ => 3
    };

    private static bool Has(ConsoleModifiers mods, ConsoleModifiers mod) => (mods & mod) != 0;

    private static bool HasCommand(ConsoleModifiers mods) => (mods & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) != 0;

    // 1 plus Shift 1, Alt 2, Ctrl 4, in both xterm's forms and kitty's.
    private static int ModifierParameter(ConsoleModifiers mods) =>
        1 + (Has(mods, ConsoleModifiers.Shift) ? 1 : 0) + (Has(mods, ConsoleModifiers.Alt) ? 2 : 0) + (Has(mods, ConsoleModifiers.Control) ? 4 : 0);

    private static void Cursor(IBufferWriter<byte> output, char final, int parameter, VtModes modes)
    {
        if (parameter > 1) Ascii(output, string.Create(CultureInfo.InvariantCulture, $"\e[1;{parameter}{final}"));
        else Ascii(output, string.Create(CultureInfo.InvariantCulture, $"{(modes.ApplicationCursorKeys ? "\eO" : "\e[")}{final}"));
    }

    private static void Tilde(IBufferWriter<byte> output, int code, int parameter)
    {
        Ascii(output, parameter > 1
            ? string.Create(CultureInfo.InvariantCulture, $"\e[{code};{parameter}~")
            : string.Create(CultureInfo.InvariantCulture, $"\e[{code}~"));
    }

    private static int TildeCode(ConsoleKey key) => key switch
    {
        ConsoleKey.F5 => 15,
        ConsoleKey.F6 => 17,
        ConsoleKey.F7 => 18,
        ConsoleKey.F8 => 19,
        ConsoleKey.F9 => 20,
        ConsoleKey.F10 => 21,
        ConsoleKey.F11 => 23,
        _ => 24
    };

    /// <summary>F1 to F4. Kitty's forms differ in two ways: no SS3, and F3 is <c>CSI 13 ~</c>, as <c>CSI R</c> is also a cursor report.</summary>
    private static void FunctionKey(IBufferWriter<byte> output, int index, int parameter, bool kitty)
    {
        var final = (char)('P' + index);
        if (kitty && index == 2) Tilde(output, 13, parameter);
        else if (parameter > 1) Ascii(output, string.Create(CultureInfo.InvariantCulture, $"\e[1;{parameter}{final}"));
        else Ascii(output, string.Create(CultureInfo.InvariantCulture, $"{(kitty ? "\e[" : "\eO")}{final}"));
    }

    /// <summary>F13 to F24, which xterm-256color's terminfo describes as F1 to F12 with Shift.</summary>
    private static void FarFunctionKey(IBufferWriter<byte> output, ConsoleKey key, ConsoleModifiers mods)
    {
        var parameter = ModifierParameter(mods | ConsoleModifiers.Shift);
        var low = ConsoleKey.F1 + (key - ConsoleKey.F13);
        if (low <= ConsoleKey.F4) FunctionKey(output, low - ConsoleKey.F1, parameter, kitty: false);
        else Tilde(output, TildeCode(low), parameter);
    }

    private static void Control(IBufferWriter<byte> output, int code, byte plain, ConsoleModifiers mods, bool kitty, bool all)
    {
        var parameter = ModifierParameter(mods);
        if (kitty && (all || parameter > 1)) CsiU(output, code, parameter);
        else Prefixed(output, mods, plain);
    }

    private static void Tab(IBufferWriter<byte> output, ConsoleModifiers mods, bool kitty, bool all)
    {
        var parameter = ModifierParameter(mods);
        if (kitty && (all || parameter > 1)) CsiU(output, 9, parameter);
        else if (Has(mods, ConsoleModifiers.Shift))
            Ascii(output, parameter == 2 ? "\e[Z" : string.Create(CultureInfo.InvariantCulture, $"\e[1;{parameter}Z"));
        else Prefixed(output, mods, (byte)'\t');
    }

    private static void Backspace(IBufferWriter<byte> output, ConsoleModifiers mods, bool kitty, bool all)
    {
        var parameter = ModifierParameter(mods);
        if (kitty && (all || parameter > 1)) CsiU(output, 127, parameter);
        else Prefixed(output, mods, Has(mods, ConsoleModifiers.Control) ? (byte)0x08 : (byte)0x7F);
    }

    private static void Letter(IBufferWriter<byte> output, ConsoleKeyInfo key, VtModes modes, bool kitty, bool all)
    {
        var mods = key.Modifiers;
        var code = 'a' + (key.Key - ConsoleKey.A);
        var shifted = Has(mods, ConsoleModifiers.Shift) ? code - 'a' + 'A' : code;
        if (kitty && (all || HasCommand(mods)))
        {
            CsiU(output, code, ModifierParameter(mods), TextFor(shifted, mods, modes));
            return;
        }
        var value = Has(mods, ConsoleModifiers.Control) ? code - 'a' + 1 : shifted;
        Prefixed(output, mods, (byte)value);
    }

    private static void Digit(IBufferWriter<byte> output, ConsoleKeyInfo key, VtModes modes, bool kitty, bool all)
    {
        var mods = key.Modifiers;
        var digit = '0' + (key.Key - ConsoleKey.D0);
        if (kitty && (all || HasCommand(mods)))
        {
            CsiU(output, digit, ModifierParameter(mods), TextFor(digit, mods, modes));
            return;
        }
        var control = Has(mods, ConsoleModifiers.Control) ? ControlFor((char)digit) : null;
        Prefixed(output, mods, control ?? (byte)digit);
    }

    /// <summary>Keypad keys. Only a terminal in kitty's report-all mode tells them apart, but MiniTui can decode them.</summary>
    private static bool Keypad(IBufferWriter<byte> output, ConsoleKeyInfo key, VtModes modes, bool kitty, bool all)
    {
        var index = key.Key switch
        {
            >= ConsoleKey.NumPad0 and <= ConsoleKey.NumPad9 => key.Key - ConsoleKey.NumPad0,
            ConsoleKey.Decimal => 10,
            ConsoleKey.Divide => 11,
            ConsoleKey.Multiply => 12,
            ConsoleKey.Subtract => 13,
            ConsoleKey.Add => 14,
            ConsoleKey.Separator => 17,
            _ => -1
        };
        if (index < 0) return false;

        var parameter = ModifierParameter(key.Modifiers);
        if (kitty && (all || HasCommand(key.Modifiers)))
        {
            CsiU(output, KittyKeypad0 + index, parameter, TextFor(key.KeyChar, key.Modifiers, modes));
            return true;
        }
        if (modes.ApplicationKeypad && parameter == 1 && !kitty)
        {
            var final = "pqrstuvwxynojmk??l"[index];
            Ascii(output, string.Create(CultureInfo.InvariantCulture, $"\eO{final}"));
            return true;
        }
        return false;
    }

    private static bool Character(IBufferWriter<byte> output, ConsoleKeyInfo key, VtModes modes, bool kitty, bool all)
    {
        var c = key.KeyChar;
        var mods = key.Modifiers;
        if (c == '\0' || char.IsSurrogate(c)) return false;

        if (kitty && (all || HasCommand(mods)))
        {
            // What a legacy terminal reports as Ctrl+\ and so on arrives as the control character.
            var code = c switch
            {
                '\x1C' => '\\',
                '\x1D' => ']',
                '\x1E' => '6',
                '\x1F' => '/',
                _ => c
            };
            CsiU(output, code, ModifierParameter(mods | (c < 0x20 ? ConsoleModifiers.Control : 0)), TextFor(c, mods, modes));
            return true;
        }

        if (Has(mods, ConsoleModifiers.Control) && ControlFor(c) is { } control)
        {
            Prefixed(output, mods, control);
            return true;
        }
        if (Has(mods, ConsoleModifiers.Alt)) Bytes(output, 0x1B);
        Text(output, new Rune(c));
        return true;
    }

    /// <summary>What xterm sends for Ctrl with a key that has no control character of its own, or null if it sends the key.</summary>
    private static byte? ControlFor(char c) => c switch
    {
        '2' or '@' or '`' or ' ' => 0x00,
        '3' or '[' => 0x1B,
        '4' or '\\' => 0x1C,
        '5' or ']' => 0x1D,
        '6' or '^' or '~' => 0x1E,
        '7' or '_' or '/' => 0x1F,
        '8' or '?' => 0x7F,
        _ => null
    };

    private static int? TextFor(int value, ConsoleModifiers mods, VtModes modes) =>
        (modes.KeyboardFlags & KittyText) != 0 && (modes.KeyboardFlags & KittyReportAll) != 0 && !HasCommand(mods) && value >= 0x20
            ? value
            : null;

    private static void CsiU(IBufferWriter<byte> output, int code, int parameter, int? text = null)
    {
        if (text is { } t)
            Ascii(output, parameter > 1
                ? string.Create(CultureInfo.InvariantCulture, $"\e[{code};{parameter};{t}u")
                : string.Create(CultureInfo.InvariantCulture, $"\e[{code};;{t}u"));
        else
            Ascii(output, parameter > 1
                ? string.Create(CultureInfo.InvariantCulture, $"\e[{code};{parameter}u")
                : string.Create(CultureInfo.InvariantCulture, $"\e[{code}u"));
    }

    private static void Prefixed(IBufferWriter<byte> output, ConsoleModifiers mods, byte value)
    {
        if (Has(mods, ConsoleModifiers.Alt)) Bytes(output, 0x1B, value);
        else Bytes(output, value);
    }

    private static void Bytes(IBufferWriter<byte> output, params ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(output.GetSpan(bytes.Length));
        output.Advance(bytes.Length);
    }

    private static void Ascii(IBufferWriter<byte> output, string text) => Encoding.ASCII.GetBytes(text, output);

    private static void Text(IBufferWriter<byte> output, Rune rune)
    {
        Span<byte> bytes = stackalloc byte[4];
        Bytes(output, bytes[..rune.EncodeToUtf8(bytes)]);
    }
}

using System.Buffers;
using System.Text;

namespace MiniTui.Vt.Tests;

public class VtInputTests
{
    private const ConsoleModifiers Shift = ConsoleModifiers.Shift;
    private const ConsoleModifiers Alt = ConsoleModifiers.Alt;
    private const ConsoleModifiers Ctrl = ConsoleModifiers.Control;

    private static readonly VtModes Legacy = default;
    private static readonly VtModes Kitty = default(VtModes) with { KeyboardFlags = 1 };
    private static readonly VtModes KittyAll = default(VtModes) with { KeyboardFlags = 1 | 8 };

    private static string Encode(ConsoleKeyInfo key, VtModes modes)
    {
        var output = new ArrayBufferWriter<byte>();
        Assert.True(VtInput.TryEncode(key, modes, output), $"no encoding for {Show(key)}");
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, char c = '\0', ConsoleModifiers mods = 0) =>
        new(c, key, (mods & Shift) != 0, (mods & Alt) != 0, (mods & Ctrl) != 0);

    private static string Show(ConsoleKeyInfo k) => $"{k.Modifiers}+{k.Key}('\\u{(int)k.KeyChar:x4}')";

    /// <summary>Every key MiniTui's decoder can report, found by giving it every form it reads.</summary>
    private static List<ConsoleKeyInfo> EveryKey()
    {
        var inputs = new List<string>();

        // Plain bytes, and with Alt
        for (var b = 0; b < 0x7F; b++)
        {
            if (b == 0x1B) continue;
            inputs.Add(((char)b).ToString());
            inputs.Add("\e" + (char)b);
        }
        inputs.Add("\x7f");
        inputs.Add("\e\x7f");
        inputs.Add("\e");
        inputs.AddRange(["é", "日", "€"]);

        // xterm's forms, with every modifier
        foreach (var m in Enumerable.Range(1, 8))
        {
            foreach (var final in "ABCDHFPQRS") inputs.Add($"\e[1;{m}{final}");
            foreach (var code in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 11, 12, 13, 14, 15, 17, 18, 19, 20, 21, 23, 24 })
                inputs.Add($"\e[{code};{m}~");
            inputs.Add($"\e[1;{m}Z");

            // Kitty's forms: every ASCII code, and every functional code it maps
            for (var code = 0x20; code < 0x7F; code++) inputs.Add($"\e[{code};{m}u");
            foreach (var code in new[] { 9, 13, 27, 127, 0xE9, 0x65E5 }) inputs.Add($"\e[{code};{m}u");
            for (var code = 57344; code <= 57430; code++) inputs.Add($"\e[{code};{m}u");
        }
        foreach (var final in "ABCDHFPQRS") inputs.Add($"\eO{final}");

        return inputs
            .SelectMany(input => RealDecoder.Keys(Encoding.UTF8.GetBytes(input)))
            .Distinct()
            .ToList();
    }

    [Fact]
    public void The_enumeration_covers_what_it_should()
    {
        var keys = EveryKey();
        Assert.True(keys.Count > 1000, $"only {keys.Count} keys");
        Assert.Contains(Key(ConsoleKey.Spacebar, '\0', Ctrl), keys);
        Assert.Contains(Key(ConsoleKey.Enter, '\r', Shift), keys);
        Assert.Contains(Key(ConsoleKey.F24, '\0', Ctrl | Alt), keys);
        Assert.Contains(Key(ConsoleKey.NumPad5, '5', Ctrl), keys);
        Assert.Contains(Key(ConsoleKey.LeftArrow, '\0', Alt), keys);
    }

    [Fact]
    public void Every_key_survives_kitty_report_all_exactly()
    {
        var failures = new List<string>();
        foreach (var key in EveryKey())
        {
            // A raw control character is what a terminal without kitty reports for these. Kitty names the key
            // instead, and the name for Ctrl+^ and Ctrl+_ isn't one that decodes to the control character.
            if (key.KeyChar is '\x1E' or '\x1F' && key.Key == 0) continue;

            var bytes = Encode(key, KittyAll);
            var back = RealDecoder.Keys(Encoding.UTF8.GetBytes(bytes));
            if (back.Count != 1 || back[0] != key)
                failures.Add($"{Show(key)} -> {Printable(bytes)} -> {string.Join(", ", back.Select(Show))}");
        }
        Assert.True(failures.Count == 0, $"{failures.Count} failed:\n" + string.Join("\n", failures.Take(40)));
    }

    [Fact]
    public void Every_key_survives_kitty_disambiguate_apart_from_shift_on_text()
    {
        var failures = new List<string>();
        foreach (var key in EveryKey())
        {
            if (key.KeyChar is '\x1E' or '\x1F' && key.Key == 0) continue;

            // In this mode a key that types text without Ctrl or Alt just types it, so its Shift can't show.
            // A letter's Shift shows in the capital. The keypad's digits are text too.
            var expected = key;
            var typesText = (key.Modifiers & (Ctrl | Alt)) == 0 && key.KeyChar >= ' ' && key.KeyChar != '\x7f';
            if (typesText && key.Key is not (>= ConsoleKey.A and <= ConsoleKey.Z) and not (ConsoleKey.Enter or ConsoleKey.Tab or ConsoleKey.Backspace or ConsoleKey.Escape))
                expected = RealDecoder.Keys(Encoding.UTF8.GetBytes(key.KeyChar.ToString())).Single();

            var bytes = Encode(key, Kitty);
            var back = RealDecoder.Keys(Encoding.UTF8.GetBytes(bytes));
            if (back.Count != 1 || back[0] != expected)
                failures.Add($"{Show(key)} -> {Printable(bytes)} -> {string.Join(", ", back.Select(Show))}");
        }
        Assert.True(failures.Count == 0, $"{failures.Count} failed:\n" + string.Join("\n", failures.Take(40)));
    }

    [Fact]
    public void Named_keys_survive_the_legacy_forms_exactly()
    {
        var failures = new List<string>();
        foreach (var key in EveryKey().Where(k => k.Key is
                     (>= ConsoleKey.LeftArrow and <= ConsoleKey.DownArrow) or ConsoleKey.Home or ConsoleKey.End
                     or ConsoleKey.PageUp or ConsoleKey.PageDown or ConsoleKey.Insert or ConsoleKey.Delete
                     or (>= ConsoleKey.F1 and <= ConsoleKey.F12)))
        {
            var bytes = Encode(key, Legacy);
            var back = RealDecoder.Keys(Encoding.UTF8.GetBytes(bytes));
            if (back.Count != 1 || back[0] != key)
                failures.Add($"{Show(key)} -> {Printable(bytes)} -> {string.Join(", ", back.Select(Show))}");
        }
        Assert.True(failures.Count == 0, $"{failures.Count} failed:\n" + string.Join("\n", failures.Take(40)));
    }

    [Fact]
    public void What_a_legacy_terminal_sends_decodes_to_the_same_key_after_a_round_trip()
    {
        // Anything the decoder got from one plain byte, with or without Alt, has an exact legacy form.
        var failures = new List<string>();
        for (var b = 0; b <= 0x7F; b++)
        {
            if (b == 0x1B) continue;
            foreach (var prefix in new[] { "", "\e" })
            {
                var input = prefix + (char)b;
                var key = RealDecoder.Keys(Encoding.UTF8.GetBytes(input)).Single();
                var bytes = Encode(key, Legacy);
                var back = RealDecoder.Keys(Encoding.UTF8.GetBytes(bytes)).Single();
                if (back != key) failures.Add($"{Printable(input)}: {Show(key)} -> {Printable(bytes)} -> {Show(back)}");
            }
        }
        Assert.True(failures.Count == 0, $"{failures.Count} failed:\n" + string.Join("\n", failures));
    }

    [Theory]
    // The keys from PRIOR-ART.md, as xterm sends them
    [InlineData(ConsoleKey.A, 'a', (ConsoleModifiers)0, "a")]
    [InlineData(ConsoleKey.Escape, '\x1b', (ConsoleModifiers)0, "\e")]
    [InlineData(ConsoleKey.B, '\x02', Ctrl, "\x02")]
    [InlineData(ConsoleKey.UpArrow, '\0', (ConsoleModifiers)0, "\e[A")]
    [InlineData(ConsoleKey.Spacebar, '\0', Ctrl, "\0")]
    [InlineData(ConsoleKey.LeftArrow, '\0', Alt, "\e[1;3D")]
    [InlineData(ConsoleKey.PageUp, '\0', Ctrl, "\e[5;5~")]
    [InlineData(ConsoleKey.UpArrow, '\0', Ctrl | Shift, "\e[1;6A")]
    [InlineData(ConsoleKey.Enter, '\r', Alt, "\e\r")]
    [InlineData(ConsoleKey.F10, '\0', (ConsoleModifiers)0, "\e[21~")]
    // And others worth pinning
    [InlineData(ConsoleKey.Enter, '\r', Shift, "\r")]
    [InlineData(ConsoleKey.Enter, '\r', Ctrl, "\r")]
    [InlineData(ConsoleKey.Tab, '\t', Shift, "\e[Z")]
    [InlineData(ConsoleKey.Backspace, '\b', (ConsoleModifiers)0, "\x7f")]
    [InlineData(ConsoleKey.Backspace, '\b', Ctrl, "\b")]
    [InlineData(ConsoleKey.Backspace, '\b', Alt, "\e\x7f")]
    [InlineData(ConsoleKey.F1, '\0', (ConsoleModifiers)0, "\eOP")]
    [InlineData(ConsoleKey.F1, '\0', Shift, "\e[1;2P")]
    [InlineData(ConsoleKey.F5, '\0', (ConsoleModifiers)0, "\e[15~")]
    [InlineData(ConsoleKey.F13, '\0', (ConsoleModifiers)0, "\e[1;2P")]
    [InlineData(ConsoleKey.F17, '\0', (ConsoleModifiers)0, "\e[15;2~")]
    [InlineData(ConsoleKey.Home, '\0', (ConsoleModifiers)0, "\e[H")]
    [InlineData(ConsoleKey.Delete, '\0', (ConsoleModifiers)0, "\e[3~")]
    [InlineData(ConsoleKey.A, 'A', Shift, "A")]
    [InlineData(ConsoleKey.A, '\x01', Ctrl | Shift, "\x01")]
    [InlineData(ConsoleKey.X, 'x', Alt, "\ex")]
    [InlineData(ConsoleKey.D2, '2', Ctrl, "\0")]
    [InlineData(ConsoleKey.D8, '8', Ctrl, "\x7f")]
    [InlineData(ConsoleKey.D1, '1', Ctrl, "1")]
    public void Legacy_bytes(ConsoleKey key, char c, ConsoleModifiers mods, string expected) =>
        Assert.Equal(Printable(expected), Printable(Encode(Key(key, c, mods), Legacy)));

    [Theory]
    [InlineData(ConsoleKey.A, 'a', (ConsoleModifiers)0, "a")]
    [InlineData(ConsoleKey.A, 'A', Shift, "A")]
    [InlineData(ConsoleKey.A, '\x01', Ctrl, "\e[97;5u")]
    [InlineData(ConsoleKey.A, 'a', Alt, "\e[97;3u")]
    [InlineData(ConsoleKey.A, '\x01', Ctrl | Shift, "\e[97;6u")]
    [InlineData(ConsoleKey.Enter, '\r', (ConsoleModifiers)0, "\r")]
    [InlineData(ConsoleKey.Enter, '\r', Shift, "\e[13;2u")]
    [InlineData(ConsoleKey.Tab, '\t', Shift, "\e[9;2u")]
    [InlineData(ConsoleKey.Backspace, '\b', Ctrl, "\e[127;5u")]
    [InlineData(ConsoleKey.Escape, '\x1b', (ConsoleModifiers)0, "\e[27u")]
    [InlineData(ConsoleKey.Spacebar, '\0', Ctrl, "\e[32;5u")]
    [InlineData(ConsoleKey.UpArrow, '\0', (ConsoleModifiers)0, "\e[A")]
    [InlineData(ConsoleKey.UpArrow, '\0', Ctrl | Shift, "\e[1;6A")]
    [InlineData(ConsoleKey.F1, '\0', (ConsoleModifiers)0, "\e[P")]
    [InlineData(ConsoleKey.F3, '\0', (ConsoleModifiers)0, "\e[13~")]
    [InlineData(ConsoleKey.F3, '\0', Ctrl, "\e[13;5~")]
    [InlineData(ConsoleKey.F13, '\0', (ConsoleModifiers)0, "\e[57376u")]
    public void Kitty_bytes(ConsoleKey key, char c, ConsoleModifiers mods, string expected) =>
        Assert.Equal(Printable(expected), Printable(Encode(Key(key, c, mods), Kitty)));

    [Fact]
    public void Report_all_sends_text_as_codes_with_the_text_if_asked()
    {
        Assert.Equal("\e[97u", Encode(Key(ConsoleKey.A, 'a'), KittyAll));
        Assert.Equal("\e[97;2u", Encode(Key(ConsoleKey.A, 'A', Shift), KittyAll));
        Assert.Equal("\e[13u", Encode(Key(ConsoleKey.Enter, '\r'), KittyAll));
        var withText = KittyAll with { KeyboardFlags = 1 | 8 | 16 };
        Assert.Equal("\e[97;;97u", Encode(Key(ConsoleKey.A, 'a'), withText));
        Assert.Equal("\e[97;2;65u", Encode(Key(ConsoleKey.A, 'A', Shift), withText));
        Assert.Equal("\e[97;5u", Encode(Key(ConsoleKey.A, '\x01', Ctrl), withText));
    }

    [Fact]
    public void Application_cursor_keys_use_ss3_unless_modified()
    {
        var modes = Legacy with { ApplicationCursorKeys = true };
        Assert.Equal("\eOA", Encode(Key(ConsoleKey.UpArrow), modes));
        Assert.Equal("\eOH", Encode(Key(ConsoleKey.Home), modes));
        Assert.Equal("\e[1;3A", Encode(Key(ConsoleKey.UpArrow, '\0', Alt), modes));
    }

    [Fact]
    public void Application_keypad_uses_ss3()
    {
        var modes = Legacy with { ApplicationKeypad = true };
        Assert.Equal("\eOu", Encode(Key(ConsoleKey.NumPad5, '5'), modes));
        Assert.Equal("\eOk", Encode(Key(ConsoleKey.Add, '+'), modes));
        Assert.Equal("5", Encode(Key(ConsoleKey.NumPad5, '5'), Legacy));
    }

    [Fact]
    public void Nothing_to_send_is_false()
    {
        var output = new ArrayBufferWriter<byte>();
        Assert.False(VtInput.TryEncode(Key(0), Legacy, output));
        Assert.False(VtInput.TryEncode(Key(0, '\uD83D'), Legacy, output));
        Assert.Equal(0, output.WrittenCount);
    }

    // ---- Mouse ----

    private static string? Mouse(MouseEvent mouse, VtModes modes)
    {
        var output = new ArrayBufferWriter<byte>();
        return VtInput.TryEncode(mouse, modes, output) ? Encoding.Latin1.GetString(output.WrittenSpan) : null;
    }

    private static VtModes MouseModes(VtMouseMode mode, VtMouseEncoding encoding = VtMouseEncoding.Sgr) =>
        Legacy with { Mouse = mode, MouseEncoding = encoding };

    [Fact]
    public void The_mode_decides_which_events_are_sent()
    {
        var press = new MouseEvent(MouseAction.Press, MouseButton.Left, 0, 0);
        var release = new MouseEvent(MouseAction.Release, MouseButton.Left, 0, 0);
        var drag = new MouseEvent(MouseAction.Drag, MouseButton.Left, 0, 0);
        var move = new MouseEvent(MouseAction.Move, MouseButton.None, 0, 0);
        var wheel = new MouseEvent(MouseAction.ScrollUp, MouseButton.None, 0, 0);

        bool[] Sent(VtMouseMode mode) => [.. new[] { press, release, drag, move, wheel }.Select(e => Mouse(e, MouseModes(mode)) is not null)];

        Assert.Equal([false, false, false, false, false], Sent(VtMouseMode.None));
        Assert.Equal([true, false, false, false, true], Sent(VtMouseMode.X10));
        Assert.Equal([true, true, false, false, true], Sent(VtMouseMode.Normal));
        Assert.Equal([true, true, true, false, true], Sent(VtMouseMode.ButtonEvent));
        Assert.Equal([true, true, true, true, true], Sent(VtMouseMode.AnyEvent));
    }

    [Fact]
    public void Each_encoding_says_it_its_way()
    {
        var press = new MouseEvent(MouseAction.Press, MouseButton.Right, 9, 19, Ctrl);
        Assert.Equal("\e[<18;10;20M", Mouse(press, MouseModes(VtMouseMode.Normal)));
        Assert.Equal("\e[50;10;20M", Mouse(press, MouseModes(VtMouseMode.Normal, VtMouseEncoding.Urxvt)));
        Assert.Equal("\e[M\x32\x2a\x34", Mouse(press, MouseModes(VtMouseMode.Normal, VtMouseEncoding.Default)));

        var release = new MouseEvent(MouseAction.Release, MouseButton.Right, 9, 19);
        Assert.Equal("\e[<2;10;20m", Mouse(release, MouseModes(VtMouseMode.Normal)));
        Assert.Equal("\e[M\x23\x2a\x34", Mouse(release, MouseModes(VtMouseMode.Normal, VtMouseEncoding.Default)));
    }

    [Fact]
    public void Older_encodings_stop_where_they_can_no_longer_say_where()
    {
        var far = new MouseEvent(MouseAction.Press, MouseButton.Left, 300, 0);
        Assert.Null(Mouse(far, MouseModes(VtMouseMode.Normal, VtMouseEncoding.Default)));
        Assert.NotNull(Mouse(far, MouseModes(VtMouseMode.Normal, VtMouseEncoding.Utf8)));
        Assert.Equal("\e[<0;301;1M", Mouse(far, MouseModes(VtMouseMode.Normal)));
        Assert.Null(Mouse(new MouseEvent(MouseAction.Press, MouseButton.Left, 3000, 0), MouseModes(VtMouseMode.Normal, VtMouseEncoding.Utf8)));
    }

    [Fact]
    public void Utf8_coordinates_are_encoded()
    {
        var bytes = Mouse(new MouseEvent(MouseAction.Press, MouseButton.Left, 300, 0), MouseModes(VtMouseMode.Normal, VtMouseEncoding.Utf8))!;
        Assert.Equal("\e[M " + Encoding.Latin1.GetString(Encoding.UTF8.GetBytes("ō")) + "!", bytes);
    }

    [Fact]
    public void X10_has_no_modifiers()
    {
        var press = new MouseEvent(MouseAction.Press, MouseButton.Left, 0, 0, Ctrl);
        Assert.Equal("\e[<0;1;1M", Mouse(press, MouseModes(VtMouseMode.X10)));
    }

    [Fact]
    public void Mouse_events_survive_a_round_trip()
    {
        var actions = new (MouseAction Action, MouseButton Button)[]
        {
            (MouseAction.Press, MouseButton.Left), (MouseAction.Press, MouseButton.Middle), (MouseAction.Press, MouseButton.Right),
            (MouseAction.Release, MouseButton.Left), (MouseAction.Release, MouseButton.Right),
            (MouseAction.Drag, MouseButton.Left), (MouseAction.Drag, MouseButton.Right), (MouseAction.Move, MouseButton.None),
            (MouseAction.ScrollUp, MouseButton.None), (MouseAction.ScrollDown, MouseButton.None),
            (MouseAction.ScrollLeft, MouseButton.None), (MouseAction.ScrollRight, MouseButton.None)
        };
        ConsoleModifiers[] modifiers = [0, Shift, Alt, Ctrl, Shift | Alt | Ctrl];
        foreach (var (action, button) in actions)
        {
            foreach (var mods in modifiers)
            {
                var mouse = new MouseEvent(action, button, 7, 3, mods);
                var bytes = Mouse(mouse, MouseModes(VtMouseMode.AnyEvent))!;
                Assert.Equal(mouse, RealDecoder.Mice(Encoding.Latin1.GetBytes(bytes)).Single());
            }
        }
    }

    // ---- Paste ----

    private static string Paste(string text, VtModes modes)
    {
        var output = new ArrayBufferWriter<byte>();
        VtInput.EncodePaste(text, modes, output);
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }

    [Fact]
    public void Paste_is_wrapped_only_when_asked()
    {
        Assert.Equal("hi", Paste("hi", Legacy));
        Assert.Equal("\e[200~hi\e[201~", Paste("hi", Legacy with { BracketedPaste = true }));
    }

    [Fact]
    public void Pasted_text_cannot_end_the_paste()
    {
        var modes = Legacy with { BracketedPaste = true };
        Assert.Equal("\e[200~a[201~rm -rf\e[201~", Paste("a\e[201~rm -rf", modes));
        Assert.Equal("a[201~b", Paste("a\e[201~b", Legacy));
        Assert.Equal("ab", Paste("a\u009bb", Legacy));
        Assert.Equal("", Paste("\e", modes));
    }

    [Fact]
    public void Pasted_line_breaks_become_carriage_returns()
    {
        Assert.Equal("a\rb\rc\rd", Paste("a\nb\r\nc\rd", Legacy));
    }

    [Fact]
    public void Paste_keeps_unicode()
    {
        Assert.Equal("日本語🎉", Paste("日本語🎉", Legacy));
    }

    [Fact]
    public void Focus_is_reported_only_when_asked()
    {
        var output = new ArrayBufferWriter<byte>();
        Assert.False(VtInput.TryEncodeFocus(true, Legacy, output));
        var modes = Legacy with { FocusReporting = true };
        Assert.True(VtInput.TryEncodeFocus(true, modes, output));
        Assert.True(VtInput.TryEncodeFocus(false, modes, output));
        Assert.Equal("\e[I\e[O", Encoding.ASCII.GetString(output.WrittenSpan));
    }

    private static string Printable(string s) => s.Replace("\e", "ESC", StringComparison.Ordinal).Replace("\0", "NUL", StringComparison.Ordinal).Replace("\r", "CR", StringComparison.Ordinal).Replace("\t", "TAB", StringComparison.Ordinal).Replace("\b", "BS", StringComparison.Ordinal).Replace("\x7f", "DEL", StringComparison.Ordinal);
}

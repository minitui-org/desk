using System.Diagnostics;
using MiniTui.Vt;

namespace MiniTui.Desk.Tests;

/// <summary>The real tuidesk, inside a <see cref="VtTerminal"/> as its terminal, hosting real children.</summary>
public sealed class EndToEndTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly ConsoleKeyInfo Prefix = new('\0', ConsoleKey.Spacebar, false, false, true);

    private readonly Scripts _files = new();
    private VtTerminal? _desk;

    private VtTerminal Desk => _desk!;

    public void Dispose()
    {
        if (_desk is not null)
        {
            _desk.Kill();
            _desk.Dispose();
        }
        _files.Dispose();
    }

    private void Start(string shellBody, int width = 80, int height = 24, string? apps = null, Color? background = null)
    {
        var config = Directory.CreateDirectory(_files.Path("config/tuidesk")).FullName;
        if (apps is not null) File.WriteAllText(Path.Combine(config, "apps.json"), apps);
        _desk = VtTerminal.Start(new VtStartInfo(Path.Combine(AppContext.BaseDirectory, "tuidesk"))
        {
            Width = width,
            Height = height,
            Environment = new Dictionary<string, string?>
            {
                ["XDG_CONFIG_HOME"] = _files.Path("config"),
                ["SHELL"] = _files.Script(shellBody),
            },
        }, terminal =>
        {
            if (background is { } color) terminal.Screen.DefaultBackground = color;
        });
    }

    private string[] Rows()
    {
        lock (Desk.Lock)
        {
            var screen = Desk.Screen;
            return [.. Enumerable.Range(0, screen.Height)
                .Select(y => string.Concat(Enumerable.Range(0, screen.Width).Select(x => screen[x, y].Symbol)))];
        }
    }

    private string Text() => string.Join('\n', Rows());

    private static void Until(Func<bool> condition, Func<string> describe)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < Timeout, "Timed out: " + describe());
            Thread.Sleep(5);
        }
    }

    private void UntilShows(string text) =>
        Until(() => Text().Contains(text, StringComparison.Ordinal), () => $"\"{text}\" in\n{Text()}");

    private void UntilFile(string path, Func<string, bool> condition) =>
        Until(
            () => File.Exists(path) && condition(File.ReadAllText(path)),
            () => $"{path}: {(File.Exists(path) ? File.ReadAllText(path) : "(missing)")}\n{Text()}");

    private void Press(params ConsoleKeyInfo[] keys)
    {
        foreach (var key in keys) Desk.HandleKey(key);
    }

    private static ConsoleKeyInfo Key(char c, ConsoleKey key) => new(c, key, false, false, false);

    private void Type(string text)
    {
        foreach (var c in text)
        {
            Press(c switch
            {
                '\r' => Key('\r', ConsoleKey.Enter),
                ' ' => Key(' ', ConsoleKey.Spacebar),
                _ when char.IsAsciiLetterLower(c) => Key(c, ConsoleKey.A + (c - 'a')),
                _ when char.IsAsciiDigit(c) => Key(c, ConsoleKey.D0 + (c - '0')),
                _ => Key(c, 0),
            });
        }
    }

    // ---- Screen ----

    [Fact]
    public void With_no_config_it_opens_a_shell_with_every_row_but_one()
    {
        Start("stty size; exec cat");
        UntilShows("23 80");
        Assert.StartsWith("23 80", Rows()[0], StringComparison.Ordinal);
        Assert.Contains("DND", Rows()[23], StringComparison.Ordinal);
    }

    [Fact]
    public void Resizing_reaches_every_app_including_those_in_the_background()
    {
        var sizes = _files.Path("sizes");
        const string Report = "report() {{ stty size > '{0}'; }}; trap report WINCH; report; while :; do sleep 0.01; done";
        Start(string.Format(null, Report, sizes), apps: $$"""[{ "name": "two", "command": "{{_files.Script(string.Format(null, Report, sizes + "2"))}}" }]""");
        UntilFile(sizes, s => s.Trim() == "23 80");

        Press(Prefix, Key(' ', ConsoleKey.Spacebar));
        UntilShows("Launch");
        Type("two\r");
        UntilFile(sizes + "2", s => s.Trim() == "23 80");

        Desk.Resize(100, 30);
        UntilFile(sizes, s => s.Trim() == "29 100");
        UntilFile(sizes + "2", s => s.Trim() == "29 100");
        Until(() => Rows()[29].Contains("DND", StringComparison.Ordinal), Text);
    }

    [Fact]
    public void An_app_that_redraws_on_resize_has_nothing_off_screen()
    {
        Start("redraw() { printf '\\033[2J\\033[H'; n=$(stty size | cut -d' ' -f1); printf '\\033[%d;1Hbottom' \"$n\"; printf '\\033[1;1Htop'; }; trap redraw WINCH; redraw; while :; do sleep 0.01; done");
        Until(() => Rows()[22].StartsWith("bottom", StringComparison.Ordinal), Text);

        Desk.Resize(60, 10);
        Until(() => Rows()[8].StartsWith("bottom", StringComparison.Ordinal), Text);
        Assert.StartsWith("top", Rows()[0], StringComparison.Ordinal);
    }

    // ---- Keys ----

    public static TheoryData<char, ConsoleKey, ConsoleModifiers> EveryKindOfKey => new()
    {
        { 'a', ConsoleKey.A, 0 },
        { 'A', ConsoleKey.A, ConsoleModifiers.Shift },
        { '\u0001', ConsoleKey.A, ConsoleModifiers.Control },
        { '\u0003', ConsoleKey.C, ConsoleModifiers.Control },
        { '\u0004', ConsoleKey.D, ConsoleModifiers.Control },
        { '\u000a', ConsoleKey.J, ConsoleModifiers.Control },
        { '\u0011', ConsoleKey.Q, ConsoleModifiers.Control },
        { '\u001a', ConsoleKey.Z, ConsoleModifiers.Control },
        { 'a', ConsoleKey.A, ConsoleModifiers.Alt },
        { '\r', ConsoleKey.Enter, 0 },
        { '\t', ConsoleKey.Tab, 0 },
        { '\t', ConsoleKey.Tab, ConsoleModifiers.Shift },
        { '\b', ConsoleKey.Backspace, 0 },
        { '\u001b', ConsoleKey.Escape, 0 },
        { '\0', ConsoleKey.UpArrow, 0 },
        { '\0', ConsoleKey.DownArrow, ConsoleModifiers.Control },
        { '\0', ConsoleKey.LeftArrow, ConsoleModifiers.Shift },
        { '\0', ConsoleKey.RightArrow, ConsoleModifiers.Alt },
        { '\0', ConsoleKey.Home, 0 },
        { '\0', ConsoleKey.End, 0 },
        { '\0', ConsoleKey.PageUp, 0 },
        { '\0', ConsoleKey.PageDown, ConsoleModifiers.Control },
        { '\0', ConsoleKey.Insert, 0 },
        { '\0', ConsoleKey.Delete, 0 },
        { '\0', ConsoleKey.F1, 0 },
        { '\0', ConsoleKey.F5, 0 },
        { '\0', ConsoleKey.F12, ConsoleModifiers.Shift },
        { 'é', 0, 0 },
    };

    [Theory]
    [MemberData(nameof(EveryKindOfKey))]
    public void A_key_reaches_the_app_as_it_reaches_a_program_with_nothing_in_between(char c, ConsoleKey key, ConsoleModifiers modifiers)
    {
        var info = new ConsoleKeyInfo(
            c, key,
            (modifiers & ConsoleModifiers.Shift) != 0,
            (modifiers & ConsoleModifiers.Alt) != 0,
            (modifiers & ConsoleModifiers.Control) != 0);

        var expected = new System.Buffers.ArrayBufferWriter<byte>();
        Assert.True(VtInput.TryEncode(info, default, expected));

        var output = _files.Path("out");
        var ready = _files.Path("ready");
        Start($"stty raw -echo; : > '{ready}'; exec cat > '{output}'");
        UntilFile(ready, _ => true);
        Thread.Sleep(50);
        UntilShows("DND");

        Press(info);
        var want = expected.WrittenSpan.ToArray();
        Until(() => File.Exists(output) && new FileInfo(output).Length >= want.Length, () => $"{want.Length} bytes\n{Text()}");
        Thread.Sleep(50);
        Assert.Equal(want, File.ReadAllBytes(output));
    }

    [Fact]
    public void Shift_Enter_reaches_an_app_that_asked_for_the_kitty_protocol()
    {
        var output = _files.Path("out");
        Start($"stty raw -echo; printf '\\033[>1uready'; exec cat > '{output}'");

        // Desk has seen the app's request once it shows what came after it, and has asked for the protocol itself
        UntilShows("ready");
        Until(() => { lock (Desk.Lock) return Desk.Screen.Modes.KeyboardFlags != 0; }, Text);

        Press(new ConsoleKeyInfo('\r', ConsoleKey.Enter, true, false, false));
        UntilFile(output, s => s.Length >= 7);
        Assert.Equal("\u001b[13;2u", File.ReadAllText(output));
    }

    [Fact]
    public void The_prefix_is_kept_from_the_app()
    {
        var output = _files.Path("out");
        Start($"stty raw -echo; exec cat > '{output}'");
        Until(() => File.Exists(output), Text);
        UntilShows("DND");

        Press(Prefix);
        UntilShows("Tab switch");
        Press(Key('\u001b', ConsoleKey.Escape));
        Until(() => !Text().Contains("Tab switch", StringComparison.Ordinal), Text);
        Press(Key('b', ConsoleKey.B));
        UntilFile(output, s => s.Length > 0);
        Assert.Equal("b", File.ReadAllText(output));
    }

    // ---- Lifecycle, without a mouse ----

    [Fact]
    public void A_crashed_app_keeps_its_screen_and_says_how_it_ended()
    {
        Start("echo last words; exit 7");
        UntilShows("exited 7");
        Assert.StartsWith("last words", Rows()[0], StringComparison.Ordinal);
        Assert.Contains("r restart", Rows()[23], StringComparison.Ordinal);

        Press(Key('c', ConsoleKey.C));
        UntilShows("Launch");
    }

    [Fact]
    public void Ctrl_C_during_yes_gets_back_to_a_prompt_in_under_half_a_second()
    {
        // A real shell, reading commands from the terminal, with a prompt to look for
        Start("PS1='prompt> ' exec /bin/sh -i");
        UntilShows("prompt> ");
        Type("yes\r");
        Until(() => Rows()[10].StartsWith('y'), Text);
        Thread.Sleep(200); // well into it

        var watch = Stopwatch.StartNew();
        Press(new ConsoleKeyInfo('\u0003', ConsoleKey.C, false, false, true));
        Until(() => Rows().Any(r => r.StartsWith("prompt>", StringComparison.Ordinal)), Text);
        watch.Stop();
        Assert.True(watch.ElapsedMilliseconds < 500, $"{watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Typing_exit_opens_the_launcher_and_quitting_from_there_needs_no_confirmation()
    {
        Start("PS1='prompt> ' exec /bin/sh -i");
        UntilShows("prompt> ");
        Type("exit\r");
        UntilShows("Launch");
        Assert.False(Desk.HasExited);

        Press(Key('\u001b', ConsoleKey.Escape));
        UntilShows("No apps");
        Press(Prefix, Key('q', ConsoleKey.Q));
        Until(() => Desk.HasExited, Text);
        Assert.Equal(new PtyExit(0, null), Desk.Exit);
    }

    [Fact]
    public void Quit_closes_the_apps_and_restores_the_terminal()
    {
        var hung = _files.Path("hung");
        var ready = _files.Path("ready");
        Start($"trap 'echo > \"{hung}\"; exit 0' HUP; : > '{ready}'; while :; do sleep 0.01; done");
        UntilFile(ready, _ => true);
        UntilShows("DND");
        lock (Desk.Lock) Assert.True(Desk.Screen.Modes.AlternateScreen);

        Press(Prefix, Key('q', ConsoleKey.Q));
        UntilShows("Close and quit?");
        Press(Key('y', ConsoleKey.Y));
        Until(() => Desk.HasExited, Text);
        Assert.Equal(new PtyExit(0, null), Desk.Exit);
        Assert.True(File.Exists(hung));
        lock (Desk.Lock)
        {
            Assert.False(Desk.Screen.Modes.AlternateScreen);
            Assert.Equal(VtMouseMode.None, Desk.Screen.Modes.Mouse);
            Assert.False(Desk.Screen.Modes.BracketedPaste);
        }
    }

    [Fact]
    public void Quit_kills_an_app_that_ignores_the_hangup()
    {
        var ready = _files.Path("ready");
        Start($"trap '' HUP; : > '{ready}'; while :; do sleep 0.01; done");
        UntilFile(ready, _ => true);
        UntilShows("DND");

        var watch = Stopwatch.StartNew();
        Press(Prefix, Key('q', ConsoleKey.Q));
        UntilShows("Close and quit?");
        Press(Key('y', ConsoleKey.Y));
        Until(() => Desk.HasExited, Text);
        Assert.InRange(watch.Elapsed.TotalSeconds, 2.5, 8);
    }

    // ---- Colors ----

    [Fact]
    public void Apps_are_told_the_real_terminals_background_including_ones_started_later()
    {
        // Asks once a moment, as Desk only learns the color after it has started
        var first = _files.Path("first");
        var second = _files.Path("second");
        Start(
            $"stty raw -echo; (while :; do printf '\\033]11;?\\007'; sleep 0.05; done) & exec cat > '{first}'",
            apps: $$"""[{ "name": "two", "command": "{{_files.Script($"stty raw -echo; printf '\\033]11;?\\007'; exec cat > '{second}'")}}" }]""",
            background: Color.Rgb(0x12, 0x34, 0x56));
        UntilFile(first, s => s.Contains("rgb:1212/3434/5656", StringComparison.Ordinal));

        Press(Prefix, Key(' ', ConsoleKey.Spacebar));
        UntilShows("Launch");
        Type("two\r");
        UntilFile(second, s => s.Length > 0);
        Assert.Contains("rgb:1212/3434/5656", File.ReadAllText(second), StringComparison.Ordinal);
    }

    // ---- Clipboard ----

    [Fact]
    public void Only_the_foreground_app_can_set_the_clipboard()
    {
        var go = _files.Path("go");
        var ready = _files.Path("ready");
        Start(
            $"printf '\\033]52;c;Zmlyc3Q=\\007'; : > '{ready}'; while [ ! -e '{go}' ]; do sleep 0.01; done; printf '\\033]52;c;aGlkZGVu\\007'; exec cat",
            apps: $$"""[{ "name": "two", "command": "{{_files.Script("exec cat")}}" }]""");
        var clipboard = new List<string>();
        Desk.ClipboardSet += text =>
        {
            lock (clipboard) clipboard.Add(text);
        };
        UntilFile(ready, _ => true);
        Until(() => { lock (clipboard) return clipboard.Count == 1; }, Text);

        Press(Prefix, Key(' ', ConsoleKey.Spacebar));
        UntilShows("Launch");
        Type("two\r");
        Until(() => !Text().Contains("Launch", StringComparison.Ordinal), Text);
        File.WriteAllText(go, "");
        Thread.Sleep(300);
        lock (clipboard) Assert.Equal(["first"], clipboard);
    }
}

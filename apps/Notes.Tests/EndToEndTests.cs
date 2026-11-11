using System.Diagnostics;
using MiniTui.Vt;

namespace MiniTui.Desk.Notes.Tests;

/// <summary>The real tuinotes, on its own and inside the real tuidesk, each in a <see cref="VtTerminal"/> (spec §13.6).</summary>
public sealed class EndToEndTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly string Notes = Path.Combine(AppContext.BaseDirectory, "tuinotes");
    private static readonly string Desk = Path.Combine(AppContext.BaseDirectory, "tuidesk");
    private static readonly ConsoleKeyInfo Prefix = new('\0', ConsoleKey.Spacebar, false, false, true);
    private static readonly DateTime LastYear = new(2025, 3, 4, 10, 0, 0, DateTimeKind.Utc); // shown as a date, whatever the time now

    private readonly TempFolder _folder = new();
    private readonly List<VtTerminal> _terminals = [];

    public void Dispose()
    {
        foreach (var terminal in _terminals)
        {
            terminal.Kill();
            terminal.Dispose();
        }
        _folder.Dispose();
    }

    private string Data => _folder.Path("data");
    private string NotePath(string name) => Path.Combine(Data, "tuinotes", name + ".md");

    private void Seed(string name, string text, int hoursAgo = 0)
    {
        Directory.CreateDirectory(Path.Combine(Data, "tuinotes"));
        File.WriteAllText(NotePath(name), text);
        File.SetLastWriteTimeUtc(NotePath(name), LastYear.AddHours(-hoursAgo));
    }

    // Desk's state, as someone who has seen its welcome and found the prefix, so neither is in the way (spec §15)
    private string Welcomed()
    {
        var state = _folder.Path("state");
        Directory.CreateDirectory(Path.Combine(state, "tuidesk"));
        File.WriteAllText(Path.Combine(state, "tuidesk", "state.json"), """{ "welcomed": true, "prefixUsed": true }""");
        return state;
    }

    private Dictionary<string, string?> Environment() => new()
    {
        ["XDG_DATA_HOME"] = Data,
        ["XDG_CONFIG_HOME"] = _folder.Path("config"),
        ["XDG_RUNTIME_DIR"] = _folder.Path("run"),
        ["XDG_STATE_HOME"] = Welcomed(),
        ["MINITUI_DESK_SOCKET"] = null,
        ["MINITUI_DESK_APP_ID"] = null,
        ["TZ"] = "UTC",
    };

    private VtTerminal Run(string program, int width, int height, Color? background = null)
    {
        var terminal = VtTerminal.Start(new VtStartInfo(program) { Width = width, Height = height, Environment = Environment() }, t =>
        {
            if (background is { } color) t.Screen.DefaultBackground = color;
        });
        _terminals.Add(terminal);
        return terminal;
    }

    /// <summary>tuinotes on its own.</summary>
    private VtTerminal Alone(int width = 80, int height = 23, Color? background = null)
    {
        var notes = Run(Notes, width, height, background);
        UntilShows(notes, "┌ Notes ");
        return notes;
    }

    /// <summary>tuidesk, with Notes opened from the launcher.</summary>
    private VtTerminal InDesk(int width = 80, int height = 24)
    {
        var config = Directory.CreateDirectory(_folder.Path("config/tuidesk")).FullName;
        File.WriteAllText(Path.Combine(config, "apps.json"), $$"""[{ "id": "notes", "name": "Notes", "command": "{{Notes}}" }]""");
        var shell = _folder.Path("shell");
        File.WriteAllText(shell, "#!/bin/sh\necho the shell\nexec cat\n");
        File.SetUnixFileMode(shell, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var desk = VtTerminal.Start(new VtStartInfo(Desk)
        {
            Width = width,
            Height = height,
            Environment = new Dictionary<string, string?>(Environment()) { ["SHELL"] = shell },
        });
        _terminals.Add(desk);
        UntilShows(desk, "the shell");
        desk.HandleKey(Prefix);
        UntilShows(desk, "Launch");
        Type(desk, "notes");
        Press(desk, ConsoleKey.Enter);
        UntilShows(desk, "┌ Notes ");
        return desk;
    }

    private static string[] Rows(VtTerminal terminal)
    {
        lock (terminal.Lock)
        {
            var screen = terminal.Screen;
            return [.. Enumerable.Range(0, screen.Height)
                .Select(y => string.Concat(Enumerable.Range(0, screen.Width).Select(x => screen[x, y].Symbol)))];
        }
    }

    private static Cell[] Cells(VtTerminal terminal, int rows)
    {
        lock (terminal.Lock)
        {
            var screen = terminal.Screen;
            return [.. Enumerable.Range(0, rows).SelectMany(y => Enumerable.Range(0, screen.Width).Select(x => screen[x, y]))];
        }
    }

    private static string Text(VtTerminal terminal) => string.Join('\n', Rows(terminal));

    private static void Until(Func<bool> condition, Func<string> describe)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < Timeout, "Timed out: " + describe());
            Thread.Sleep(5);
        }
    }

    private static void UntilShows(VtTerminal terminal, string text) =>
        Until(() => Text(terminal).Contains(text, StringComparison.Ordinal), () => $"\"{text}\" in\n{Text(terminal)}");

    private void UntilSaved(string name, string text) =>
        Until(() => File.Exists(NotePath(name)) && File.ReadAllText(NotePath(name)) == text,
            () => $"{name}: {(File.Exists(NotePath(name)) ? File.ReadAllText(NotePath(name)) : "(missing)")}");

    private static void Press(VtTerminal terminal, ConsoleKey key, char c = '\0', bool control = false) =>
        terminal.HandleKey(new ConsoleKeyInfo(c, key, char.IsUpper(c), false, control));

    private static void Ctrl(VtTerminal terminal, char letter) =>
        Press(terminal, ConsoleKey.A + (letter - 'a'), (char)(letter - 'a' + 1), control: true);

    private static void Type(VtTerminal terminal, string text)
    {
        foreach (var c in text)
        {
            Press(terminal, c switch
            {
                ' ' => ConsoleKey.Spacebar,
                _ when char.IsAsciiLetterLower(c) => ConsoleKey.A + (c - 'a'),
                _ when char.IsAsciiLetterUpper(c) => ConsoleKey.A + (c - 'A'),
                _ => 0,
            }, c);
        }
    }

    // ---- The same either way ----

    [Fact]
    public void The_screen_is_the_same_in_Desk_and_outside_it()
    {
        Seed("Shopping", "# Shopping\n\n- milk\n- eggs\n", hoursAgo: 0);
        Seed("Work plan", "Ship Notes.\n", hoursAgo: 1);

        var alone = Alone();
        UntilShows(alone, "- eggs");
        var desk = InDesk();
        UntilShows(desk, "- eggs");

        // Everything but Desk's status line, cell by cell: the characters, colors and styles
        Until(() => Cells(alone, 23).SequenceEqual(Cells(desk, 23)),
            () => $"different screens:\n{Text(alone)}\n---\n{Text(desk)}");
        Assert.Contains("2025-03-04", Text(alone), StringComparison.Ordinal);
    }

    [Fact]
    public void With_no_notes_it_is_the_same_too()
    {
        var alone = Alone();
        var desk = InDesk();
        UntilShows(alone, "No notes yet. Ctrl+N makes one.");
        Until(() => Cells(alone, 23).SequenceEqual(Cells(desk, 23)),
            () => $"different screens:\n{Text(alone)}\n---\n{Text(desk)}");
        Assert.False(Directory.Exists(Path.Combine(Data, "tuinotes")));
    }

    [Fact]
    public void The_theme_follows_the_terminals_background()
    {
        Seed("Plan", "text");
        var dark = Alone(background: Color.Rgb(0, 0, 0));
        var light = Alone(background: Color.Rgb(255, 255, 255));
        UntilShows(dark, "text");
        UntilShows(light, "text");
        Until(() => Rows(dark).SequenceEqual(Rows(light)) && !Cells(dark, 23).SequenceEqual(Cells(light, 23)),
            () => $"the same text in different colors:\n{Text(dark)}\n---\n{Text(light)}");
    }

    [Fact]
    public void A_click_selects_a_note_on_its_own_and_in_Desk()
    {
        Seed("Shopping", "milk", hoursAgo: 0);
        Seed("Work plan", "Ship Notes.", hoursAgo: 1);
        foreach (var terminal in new[] { Alone(), InDesk() })
        {
            UntilShows(terminal, "milk");
            var row = Array.FindIndex(Rows(terminal), r => r.StartsWith("│ Wor", StringComparison.Ordinal)); // the name is cut to fit
            // The terminal reports the mouse only if the app asked for it
            Until(() => terminal.HandleMouse(new MouseEvent(MouseAction.Press, MouseButton.Left, 3, row)), () => Text(terminal));
            terminal.HandleMouse(new MouseEvent(MouseAction.Release, MouseButton.Left, 3, row));
            UntilShows(terminal, "Ship Notes.");
        }
    }

    // ---- Inside Desk ----

    [Fact]
    public void Desk_shows_the_name_and_the_open_note()
    {
        Seed("Shopping", "");
        var desk = InDesk();
        Until(() => Rows(desk)[^1].Contains(" 2 Notes: Shopping ", StringComparison.Ordinal),
            () => Rows(desk)[^1]);
    }

    [Fact]
    public void Ctrl_x_in_Desk_saves_and_closes_Notes()
    {
        Seed("Plan", "");
        var desk = InDesk();
        Press(desk, ConsoleKey.Enter, '\r');
        Type(desk, "from desk");
        UntilShows(desk, "from desk");
        desk.HandleKey(Prefix);
        UntilShows(desk, "Launch");
        Ctrl(desk, 'x');
        UntilShows(desk, "the shell");
        UntilSaved("Plan", "from desk");
    }

    // ---- On its own ----

    [Fact]
    public void A_hangup_saves_what_was_typed()
    {
        Seed("Plan", "");
        var notes = Alone();
        Press(notes, ConsoleKey.Enter, '\r');
        Type(notes, "before the hangup");
        UntilShows(notes, "before the hangup");
        notes.Hangup();
        Until(() => notes.HasExited, () => "still running");
        Assert.Equal("before the hangup", File.ReadAllText(NotePath("Plan")));
    }

    [Fact]
    public void Ctrl_q_saves_and_ends_it_with_the_terminal_restored()
    {
        var notes = Alone();
        Ctrl(notes, 'n');
        UntilShows(notes, "New note");
        Type(notes, "First");
        Press(notes, ConsoleKey.Enter, '\r');
        UntilShows(notes, "┌ First ");
        Type(notes, "hello");
        UntilShows(notes, "hello");
        Ctrl(notes, 'q');
        Until(() => notes.HasExited, () => "still running");
        Assert.Equal(0, notes.Exit!.Value.Code);
        Assert.Equal("hello", File.ReadAllText(NotePath("First")));
        lock (notes.Lock) Assert.False(notes.Screen.Modes.AlternateScreen);
    }
}

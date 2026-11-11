using System.Diagnostics;
using MiniTui.Vt;

namespace MiniTui.Desk.Reader.Tests;

/// <summary>
/// The real tuireader, on its own and inside the real tuidesk, each in a <see cref="VtTerminal"/> (spec §13.6), and
/// a passage going to the real tuinotes both ways.
/// </summary>
public sealed class EndToEndTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly string Reader = Path.Combine(AppContext.BaseDirectory, "tuireader");
    private static readonly string Notes = Path.Combine(AppContext.BaseDirectory, "tuinotes");
    private static readonly string Desk = Path.Combine(AppContext.BaseDirectory, "tuidesk");
    private static readonly ConsoleKeyInfo Prefix = new('\0', ConsoleKey.Spacebar, false, false, true);
    private const string Garden = "# Garden plan\n\nTomatoes need six hours of **sun**.\n\n- milk\n- eggs\n";

    private readonly TempFolder _folder = new();
    private readonly List<VtTerminal> _terminals = [];

    public EndToEndTests()
    {
        Directory.CreateDirectory(Docs);
        File.WriteAllText(Path.Combine(Docs, "garden-plan.md"), Garden);
        File.WriteAllText(Path.Combine(Docs, "notes.txt"), "plain text");
    }

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
    private string Docs => _folder.Path("docs");
    private string NotePath(string name) => Path.Combine(Data, "tuinotes", name + ".md");

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
        ["HOME"] = _folder.Directory, // so paths show the same, as ~/docs
        ["TZ"] = "UTC",
    };

    private VtTerminal Alone(int width = 80, int height = 23, Color? background = null)
    {
        var terminal = VtTerminal.Start(new VtStartInfo(Reader)
        {
            Args = ["garden-plan.md"],
            WorkingDirectory = Docs,
            Width = width,
            Height = height,
            Environment = Environment(),
        }, t =>
        {
            if (background is { } color) t.Screen.DefaultBackground = color;
        });
        _terminals.Add(terminal);
        UntilShows(terminal, "Tomatoes need");
        return terminal;
    }

    /// <summary>tuidesk, with Reader opened from the launcher, and Notes in apps.json as <c>notes</c>.</summary>
    private VtTerminal InDesk(int width = 80, int height = 24)
    {
        var config = Directory.CreateDirectory(_folder.Path("config/tuidesk")).FullName;
        File.WriteAllText(Path.Combine(config, "apps.json"), $$"""
            [
              { "id": "reader", "name": "Reader", "command": "{{Reader}}", "args": ["garden-plan.md"], "cwd": "{{Docs}}" },
              { "id": "notes", "name": "Notes", "command": "{{Notes}}" }
            ]
            """);
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
        Type(desk, "reader");
        Press(desk, ConsoleKey.Enter);
        UntilShows(desk, "Tomatoes need");
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

    private void UntilNote(string name, string text) =>
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

    private const string Sent = "> Tomatoes need six hours of **sun**.\n\n— ~/docs/garden-plan.md\n\n";

    // ---- The same either way ----

    [Fact]
    public void The_screen_is_the_same_in_Desk_and_outside_it()
    {
        var alone = Alone();
        var desk = InDesk();
        Press(alone, ConsoleKey.DownArrow);
        Press(desk, ConsoleKey.DownArrow);
        UntilShows(alone, "▌ Tomatoes need six hours of sun.");
        UntilShows(desk, "▌ Tomatoes need six hours of sun.");
        // Everything but Desk's status line, cell by cell: the characters, colors and styles
        Until(() => Cells(alone, 23).SequenceEqual(Cells(desk, 23)),
            () => $"different screens:\n{Text(alone)}\n---\n{Text(desk)}");
        Assert.Contains("┌ ~/docs ", Text(alone), StringComparison.Ordinal);
    }

    [Fact]
    public void The_theme_follows_the_terminals_background()
    {
        var dark = Alone(background: Color.Rgb(0, 0, 0));
        var light = Alone(background: Color.Rgb(255, 255, 255));
        Until(() => Rows(dark).SequenceEqual(Rows(light)) && !Cells(dark, 23).SequenceEqual(Cells(light, 23)),
            () => $"the same text in different colors:\n{Text(dark)}\n---\n{Text(light)}");
    }

    // ---- The mouse ----

    private static int RowOf(VtTerminal terminal, string text) => Array.FindIndex(Rows(terminal), r => r.Contains(text, StringComparison.Ordinal));

    [Fact]
    public void The_mouse_works_on_its_own_and_in_Desk_clicking_a_file_and_scrolling_with_the_wheel()
    {
        File.WriteAllText(Path.Combine(Docs, "long.md"), string.Join("\n\n", Enumerable.Range(1, 60).Select(i => $"Paragraph {i}.")));
        foreach (var terminal in new[] { Alone(), InDesk() })
        {
            // The terminal reports the mouse only if the app asked for it
            Until(() => terminal.HandleMouse(new MouseEvent(MouseAction.Press, MouseButton.Left, 3, RowOf(terminal, "long.md"))), () => Text(terminal));
            terminal.HandleMouse(new MouseEvent(MouseAction.Release, MouseButton.Left, 3, RowOf(terminal, "long.md")));
            UntilShows(terminal, "Paragraph 1.");
            for (var i = 0; i < 10; i++) terminal.HandleMouse(new MouseEvent(MouseAction.ScrollDown, MouseButton.None, 50, 10));
            Until(() => !Text(terminal).Contains("Paragraph 1.", StringComparison.Ordinal), () => Text(terminal));
        }
    }

    // ---- To Notes ----

    [Fact]
    public void In_Desk_ctrl_n_opens_Notes_in_front_with_the_passage_to_write_under()
    {
        var desk = InDesk();
        Until(() => Rows(desk)[^1].Contains(" 2 Reader: garden-plan.md ", StringComparison.Ordinal), () => Rows(desk)[^1]);
        Press(desk, ConsoleKey.DownArrow);
        Ctrl(desk, 'n');
        UntilShows(desk, "┌ Notes ");
        UntilNote("garden-plan", Sent);
        Until(() => Rows(desk)[^1].Contains(" Notes: garden-plan ", StringComparison.Ordinal), () => Rows(desk)[^1]);
        Type(desk, "Plant in May");
        UntilNote("garden-plan", Sent + "Plant in May");
    }

    [Fact]
    public void Outside_Desk_ctrl_n_runs_Notes_here_and_Reader_comes_back_when_it_quits()
    {
        var alone = Alone();
        Press(alone, ConsoleKey.DownArrow);
        Ctrl(alone, 'n');
        UntilShows(alone, "┌ Notes ");
        UntilNote("garden-plan", Sent);
        Type(alone, "Plant in May");
        Ctrl(alone, 'q');
        UntilShows(alone, "▌ Tomatoes need");
        UntilNote("garden-plan", Sent + "Plant in May");

        // A second passage from the same file goes under the first
        Press(alone, ConsoleKey.DownArrow);
        Ctrl(alone, 'n');
        UntilShows(alone, "┌ Notes ");
        UntilNote("garden-plan", Sent + "Plant in May\n\n> - milk\n\n— ~/docs/garden-plan.md\n\n");
    }

    [Fact]
    public void Ctrl_q_ends_it_and_the_next_start_opens_where_it_was()
    {
        var alone = Alone();
        Press(alone, ConsoleKey.End);
        UntilShows(alone, "▌ • eggs");
        Ctrl(alone, 'q');
        Until(() => alone.HasExited, () => Text(alone));
        Assert.Equal(0, alone.Exit!.Value.Code);
        Assert.Contains("\t" + Path.Combine(Docs, "garden-plan.md"), File.ReadAllText(Path.Combine(Data, "tuireader", "places.txt")), StringComparison.Ordinal);

        var again = Alone();
        UntilShows(again, "▌ • eggs");
    }
}

using System.Diagnostics;
using MiniTui.Vt;

namespace MiniTui.Desk.Tasks.Tests;

/// <summary>The real tuitasks, on its own and inside the real tuidesk, each in a <see cref="VtTerminal"/> (spec §13.6).</summary>
public sealed class EndToEndTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly string Tasks = Path.Combine(AppContext.BaseDirectory, "tuitasks");
    private static readonly string Desk = Path.Combine(AppContext.BaseDirectory, "tuidesk");
    private static readonly ConsoleKeyInfo Prefix = new('\0', ConsoleKey.Spacebar, false, false, true);

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
    private string TodoPath => Path.Combine(Data, "tuitasks", "todo.txt");

    private void Seed(params string[] lines)
    {
        Directory.CreateDirectory(Path.Combine(Data, "tuitasks"));
        File.WriteAllText(TodoPath, string.Concat(lines.Select(l => l + "\n")));
    }

    private Dictionary<string, string?> Environment() => new()
    {
        ["XDG_DATA_HOME"] = Data,
        ["XDG_CONFIG_HOME"] = _folder.Path("config"),
        ["XDG_RUNTIME_DIR"] = _folder.Path("run"),
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

    /// <summary>tuitasks on its own.</summary>
    private VtTerminal Alone(int width = 80, int height = 23, Color? background = null)
    {
        var tasks = Run(Tasks, width, height, background);
        UntilShows(tasks, "┌ Tasks ");
        return tasks;
    }

    /// <summary>tuidesk, with Tasks opened from the launcher.</summary>
    private VtTerminal InDesk(int width = 80, int height = 24)
    {
        var config = Directory.CreateDirectory(_folder.Path("config/tuidesk")).FullName;
        File.WriteAllText(Path.Combine(config, "apps.json"), $$"""[{ "id": "tasks", "name": "Tasks", "command": "{{Tasks}}" }]""");
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
        Type(desk, "tasks");
        Press(desk, ConsoleKey.Enter);
        UntilShows(desk, "┌ Tasks ");
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

    private void UntilSaved(string text) =>
        Until(() => File.Exists(TodoPath) && File.ReadAllText(TodoPath) == text,
            () => File.Exists(TodoPath) ? File.ReadAllText(TodoPath) : "(missing)");

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
        // Due in another year, so the hints are the same whenever this runs, and nothing is overdue to notify
        Seed("(A) Call the dentist due:2099-01-04 at:15:00 +health", "Read the upgrade notes", "x 2025-03-04 Renew passport");

        var alone = Alone();
        UntilShows(alone, "Renew passport");
        var desk = InDesk();
        UntilShows(desk, "Renew passport");

        // Everything but Desk's status line, cell by cell: the characters, colors and styles
        Until(() => Cells(alone, 23).SequenceEqual(Cells(desk, 23)),
            () => $"different screens:\n{Text(alone)}\n---\n{Text(desk)}");
        Assert.Contains("2099-01-04 15:00", Text(alone), StringComparison.Ordinal);
    }

    [Fact]
    public void A_click_selects_a_task_on_its_own_and_in_Desk()
    {
        Seed("Alpha", "Beta", "Gamma");
        foreach (var (terminal, task) in new[] { (Alone(), "Beta"), (InDesk(), "Gamma") })
        {
            UntilShows(terminal, task);
            var row = Array.FindIndex(Rows(terminal), r => r.Contains(task, StringComparison.Ordinal));
            // The terminal reports the mouse only if the app asked for it
            Until(() => terminal.HandleMouse(new MouseEvent(MouseAction.Press, MouseButton.Left, 3, row)), () => Text(terminal));
            terminal.HandleMouse(new MouseEvent(MouseAction.Release, MouseButton.Left, 3, row));
            Thread.Sleep(200); // the click goes before Ctrl+D, which can't overtake it, but give the app the press first
            Ctrl(terminal, 'd');
            Until(() => File.ReadAllLines(TodoPath).Any(l => l.StartsWith("x ", StringComparison.Ordinal) && l.EndsWith(" " + task, StringComparison.Ordinal)),
                () => File.ReadAllText(TodoPath));
        }
    }

    [Fact]
    public void With_no_tasks_it_is_the_same_too()
    {
        var alone = Alone();
        var desk = InDesk();
        UntilShows(alone, "No tasks yet. Ctrl+N adds one.");
        Until(() => Cells(alone, 23).SequenceEqual(Cells(desk, 23)),
            () => $"different screens:\n{Text(alone)}\n---\n{Text(desk)}");
        Assert.False(Directory.Exists(Path.Combine(Data, "tuitasks")));
    }

    // ---- Inside Desk ----

    [Fact]
    public void Desk_shows_the_name_and_what_is_overdue_and_counts_its_notification()
    {
        Seed("Call due:2025-03-04");
        var desk = InDesk();
        Until(() => Rows(desk)[^1].Contains(" 2 Tasks: 1 overdue ", StringComparison.Ordinal), () => Rows(desk)[^1]);
        Assert.DoesNotContain("Call: Overdue", Text(desk), StringComparison.Ordinal);   // no toast of its own
    }

    // ---- On its own ----

    [Fact]
    public void An_overdue_task_is_a_toast_on_its_own()
    {
        Seed("Call due:2025-03-04");
        var tasks = Alone();
        UntilShows(tasks, "Call: Overdue · 2025-03-04");
    }

    [Fact]
    public void Adding_and_finishing_a_task_goes_to_the_file_and_ctrl_q_restores_the_terminal()
    {
        var tasks = Alone();
        Ctrl(tasks, 'n');
        UntilShows(tasks, "New task");
        Type(tasks, "Buy milk");
        Press(tasks, ConsoleKey.Enter, '\r');
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        UntilSaved($"{today} Buy milk\n");
        Ctrl(tasks, 'd');
        UntilSaved($"x {today} {today} Buy milk\n");
        Ctrl(tasks, 'q');
        Until(() => tasks.HasExited, () => "still running");
        Assert.Equal(0, tasks.Exit!.Value.Code);
        lock (tasks.Lock) Assert.False(tasks.Screen.Modes.AlternateScreen);
    }
}

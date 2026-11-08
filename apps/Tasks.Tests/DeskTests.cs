using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using MiniTui.Desk.Protocol;

namespace MiniTui.Desk.Tasks.Tests;

/// <summary>Tasks inside Desk (spec §13.9), with a real client and a scripted shell.</summary>
public sealed class DeskTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly TempFolder _folder = new();
    private readonly FakeShell _shell = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
    private readonly TaskStore _store;
    private TasksScreen? _tasks;
    private ComponentHarness? _harness;

    public DeskTests() => _store = new TaskStore(_folder.Path("tuitasks"));

    public void Dispose()
    {
        _harness?.Dispose();
        _shell.Dispose();
        _folder.Dispose();
    }

    private ComponentHarness Harness => _harness!;

    private void Seed(params string[] lines)
    {
        Directory.CreateDirectory(_store.Directory);
        File.WriteAllText(_store.FilePath, string.Concat(lines.Select(l => l + "\n")));
    }

    private void Start()
    {
        DeskClient? desk = null;
        _tasks = new TasksScreen(_store, _time, title => desk?.SetTitle(title), n => desk?.Notify(n));
        _harness = new ComponentHarness(_tasks, 80, 16, _time);
        Harness.App.WithDesk("Tasks", out desk, null, _shell.SocketPath, "tasks-1");
        DeskLink.Join(_tasks, desk);
        Assert.Contains("\"hello\"", _shell.Next(), StringComparison.Ordinal);
    }

    private void Until(Func<bool> condition, Func<string> describe)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            Harness.RunUntilIdle();
            if (condition()) return;
            Assert.True(clock.Elapsed < Timeout, "Timed out: " + describe());
            Thread.Sleep(5);
        }
    }

    private string Next()
    {
        Harness.RunUntilIdle();
        return _shell.Next();
    }

    [Fact]
    public void The_title_and_a_reminder_go_to_the_shell_and_done_comes_back()
    {
        Seed("Call the dentist due:2026-11-07 at:12:30");
        Start();                                  // the first title goes before the shell is connected here

        _time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(
            """{"type":"notify","id":"task-1","title":"Call the dentist","body":"Due 12:30","urgency":"normal","actions":[{"id":"done","label":"Done"},{"id":"snooze","label":"Snooze 1 hour"}]}""",
            Next());

        _shell.Send("""{"type":"notificationAction","id":"task-1","action":"done"}""");
        Until(() => File.ReadAllText(_store.FilePath) == "x 2026-11-07 Call the dentist due:2026-11-07 at:12:30\n", () => File.ReadAllText(_store.FilePath));
        Assert.Equal("""{"type":"title","text":""}""", Next());
    }

    [Fact]
    public void Getting_the_focus_back_shows_what_changed()
    {
        Seed("Call");
        Start();
        _shell.Send("""{"type":"focus","focused":false}""");
        File.WriteAllText(_store.FilePath, "Call\nAdded elsewhere\n");
        _shell.Send("""{"type":"focus","focused":true}""");
        Until(() => Harness.Lines.Any(l => l.Contains("Added elsewhere", StringComparison.Ordinal)), () => string.Join('\n', Harness.Lines));
    }

    [Fact]
    public void A_request_to_quit_quits_as_everything_is_saved()
    {
        Seed("Call");
        Start();
        Harness.Press("ctrl+d");
        _shell.Send("""{"type":"quitRequest"}""");
        Until(() => Harness.App.Stopping.IsCancellationRequested, () => "still running");
        Assert.StartsWith("x ", File.ReadAllText(_store.FilePath), StringComparison.Ordinal);
    }
}

using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using MiniTui.Desk.Protocol;

namespace MiniTui.Desk.Notes.Tests;

/// <summary>Notes inside Desk (spec §13.8), with a real client and a scripted shell. The clock doesn't move, so nothing here is an autosave.</summary>
public sealed class DeskTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly TempFolder _folder = new();
    private readonly FakeShell _shell = new();
    private readonly FakeTimeProvider _time = new();
    private readonly NotesStore _store;
    private NotesScreen? _notes;
    private ComponentHarness? _harness;

    public DeskTests() => _store = new NotesStore(_folder.Path("tuinotes"));

    public void Dispose()
    {
        _harness?.Dispose();
        _shell.Dispose();
        _folder.Dispose();
    }

    private ComponentHarness Harness => _harness!;

    private void Start()
    {
        DeskClient? desk = null;
        _notes = new NotesScreen(_store, _time, title => desk?.SetTitle(title));
        _harness = new ComponentHarness(_notes, 100, 20, _time);
        Harness.App.WithDesk("Notes", out desk, null, _shell.SocketPath, "notes-1");
        DeskLink.Join(_notes, desk);
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

    private void Edit(string text)
    {
        Harness.Press("enter");
        Harness.Type(text);
        Harness.RunUntilIdle();
        Assert.True(_notes!.HasUnsavedChanges);
    }

    [Fact]
    public void The_title_is_the_open_note()
    {
        _store.Write("Plan", "");
        Start();
        Harness.Press("f2 ctrl+a");
        Harness.Type("Big plan");
        Harness.Press("enter");
        Harness.RunUntilIdle();
        Assert.Equal("""{"type":"title","text":"Big plan"}""", _shell.Next());
    }

    [Fact]
    public void The_title_is_sent_only_when_it_changes()
    {
        _store.Write("Plan", "before");
        Start();
        File.WriteAllText(_store.PathOf("Plan"), "after");
        Harness.App.Post(_notes!.Refresh); // opens it again, with the same name
        Until(() => Harness.Lines.Any(l => l.Contains("after", StringComparison.Ordinal)), () => string.Join('\n', Harness.Lines));
        _shell.AssertQuiet();
    }

    [Fact]
    public void Losing_the_focus_saves_at_once()
    {
        _store.Write("Plan", "");
        Start();
        Edit("typed");
        _shell.Send("""{"type":"focus","focused":false}""");
        Until(() => _store.Read("Plan") == "typed", () => _store.Read("Plan") ?? "(gone)");
    }

    [Fact]
    public void Getting_the_focus_back_shows_what_changed()
    {
        _store.Write("Plan", "before");
        Start();
        _shell.Send("""{"type":"focus","focused":false}""");
        File.WriteAllText(_store.PathOf("Plan"), "after");
        _shell.Send("""{"type":"focus","focused":true}""");
        Until(() => Harness.Lines.Any(l => l.Contains("after", StringComparison.Ordinal)), () => string.Join('\n', Harness.Lines));
    }

    [Fact]
    public void A_request_to_quit_saves_and_quits()
    {
        _store.Write("Plan", "");
        Start();
        Edit("last words");
        _shell.Send("""{"type":"quitRequest"}""");
        Until(() => Harness.App.Stopping.IsCancellationRequested, () => "still running");
        Assert.Equal("last words", _store.Read("Plan"));
    }

    [Fact]
    public void A_request_to_quit_that_cannot_save_is_declined()
    {
        _store.Write("Plan", "");
        Start();
        Edit("precious");
        File.SetUnixFileMode(_store.Directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        _shell.Send("""{"type":"quitRequest"}""");
        Until(() => Harness.Lines.Any(l => l.Contains("Can't save", StringComparison.Ordinal)), () => string.Join('\n', Harness.Lines));

        Assert.Equal("""{"type":"quitDeclined"}""", _shell.Next());
        Harness.RunUntilIdle();
        Assert.False(Harness.App.Stopping.IsCancellationRequested);
        Assert.True(_notes!.HasUnsavedChanges);
    }
}

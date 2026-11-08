using Microsoft.Extensions.Time.Testing;
using MiniTui.Desk.Protocol;

namespace MiniTui.Desk.Tasks.Tests;

/// <summary>Tasks with the keyboard alone (spec §13.9), on a TestBackend with a clock the test moves.</summary>
public sealed class ScreenTests : IDisposable
{
    // A Saturday at noon, in UTC, which is the fake clock's local time
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
    private readonly TempFolder _folder = new();
    private readonly TaskStore _store;
    private readonly List<string> _titles = [];
    private readonly List<DeskNotification> _sent = [];
    private TasksScreen? _screen;
    private ComponentHarness? _harness;

    public ScreenTests() => _store = new TaskStore(_folder.Path("tuitasks"));

    public void Dispose()
    {
        _harness?.Dispose();
        _folder.Dispose();
    }

    private TasksScreen Tasks => _screen!;
    private ComponentHarness Harness => _harness!;
    private string Screen => string.Join('\n', Harness.Lines);
    private string File_ => File.ReadAllText(_store.FilePath);

    private void Seed(params string[] lines)
    {
        Directory.CreateDirectory(_store.Directory);
        File.WriteAllText(_store.FilePath, string.Concat(lines.Select(l => l + "\n")));
    }

    private void Start(bool inDesk = false, int width = 80, int height = 16)
    {
        _screen = new TasksScreen(_store, _time, _titles.Add, inDesk ? _sent.Add : null);
        _harness = new ComponentHarness(_screen, width, height, _time);
        Harness.RunUntilIdle();
    }

    private void Press(string keys)
    {
        Harness.Press(keys);
        Harness.RunUntilIdle();
    }

    private void Type(string text)
    {
        Harness.Type(text);
        Harness.RunUntilIdle();
    }

    private void Wait(TimeSpan time)
    {
        _time.Advance(time);
        Harness.RunUntilIdle();
    }

    private string Row(string containing) => Harness.Lines.First(l => l.Contains(containing, StringComparison.Ordinal));

    // ---- With no tasks ----

    [Fact]
    public void With_no_tasks_it_says_how_to_add_one_and_makes_no_folder()
    {
        Start();
        Assert.Contains("No tasks yet. Ctrl+N adds one.", Screen, StringComparison.Ordinal);
        Assert.Contains("ctrl+n New", Harness.Lines[^1], StringComparison.Ordinal);
        Assert.False(Directory.Exists(_store.Directory));
        Assert.DoesNotContain("overdue", Screen, StringComparison.Ordinal);
    }

    [Fact]
    public void Ctrl_n_adds_a_task_made_today_from_the_filter_and_selects_it()
    {
        Seed("Read the notes");
        Start();
        Type("water");
        Press("ctrl+n");
        Assert.Contains("New task", Screen, StringComparison.Ordinal);
        Type(" the plants");
        Press("enter");

        Assert.Equal("Read the notes\n2026-11-07 water the plants\n", File_);
        Assert.Equal("water the plants", Tasks.Selected?.Title);
        Assert.Equal(["Read the notes", "water the plants"], Tasks.Showing);     // the filter is cleared
    }

    [Fact]
    public void A_task_needs_text()
    {
        Start();
        Press("ctrl+n");
        Press("enter");
        Assert.Contains("A task needs some text", Screen, StringComparison.Ordinal);
        Press("esc");
        Assert.False(File.Exists(_store.FilePath));
    }

    // ---- The list ----

    [Fact]
    public void Due_tasks_come_first_by_when_then_by_priority_then_the_done_ones_last_done_first()
    {
        Seed(
            "x 2026-11-01 Old done",
            "Later due:2026-11-20",
            "(B) Some time",
            "Plain",
            "(A) Important",
            "Late due:2026-11-05",
            "x 2026-11-06 Recently done",
            "Soon due:2026-11-07 at:15:00");
        Start();
        Assert.Equal(
            ["Late", "Soon", "Later", "(A) Important", "(B) Some time", "Plain", "✓ Recently done", "✓ Old done"],
            Harness.Lines.Skip(3).Take(8).Select(l => l.Trim('│', ' ').Split("  ")[0]));
        Assert.EndsWith("Overdue · Nov 5 │", Harness.Lines[3], StringComparison.Ordinal);
        Assert.EndsWith("Today 15:00 │", Row("Soon"), StringComparison.Ordinal);
    }

    [Fact]
    public void Typing_filters()
    {
        Seed("Buy milk", "Call the dentist", "Buy eggs");
        Start();
        Type("buy");
        Assert.Equal(["Buy eggs", "Buy milk"], Tasks.Showing.Order());
        Press("esc");
        Assert.Equal(3, Tasks.Showing.Count());
    }

    [Fact]
    public void Home_and_end_go_to_the_first_and_last_with_nothing_typed()
    {
        Seed("a", "b", "c");
        Start();
        Press("end");
        Assert.Equal("c", Tasks.Selected?.Title);
        Press("home");
        Assert.Equal("a", Tasks.Selected?.Title);
    }

    [Fact]
    public void Ctrl_d_marks_done_and_back_and_the_selection_follows_the_task()
    {
        Seed("(A) Call", "Buy milk");
        Start();
        Press("ctrl+d");
        Assert.Equal("x 2026-11-07 Call pri:A\nBuy milk\n", File_);
        Assert.Equal("(A) Call", Tasks.Selected?.Title);
        Assert.True(Tasks.Selected?.Done);
        Assert.Contains("✓ (A) Call", Harness.Lines[4], StringComparison.Ordinal);    // now after Buy milk

        Press("ctrl+d");
        Assert.Equal("(A) Call\nBuy milk\n", File_);
    }

    [Fact]
    public void Enter_edits_the_text_priority_and_tags()
    {
        Seed("2026-11-01 Call due:2026-11-09");
        Start();
        Press("enter");
        Assert.Contains("Call due:2026-11-09", Screen, StringComparison.Ordinal);
        Press("ctrl+a");
        Type("(B) Call the dentist +health due:2026-11-09");
        Press("enter");
        Assert.Equal("(B) 2026-11-01 Call the dentist +health due:2026-11-09\n", File_);
    }

    [Fact]
    public void A_double_click_edits()
    {
        Seed("a", "b");
        Start();
        Harness.DoubleClick(5, 4);
        Harness.RunUntilIdle();
        Assert.Equal("b", Tasks.Selected?.Title);
        Assert.Contains("Edit task", Screen, StringComparison.Ordinal);
    }

    [Fact]
    public void Ctrl_t_sets_when_it_is_due_and_empty_clears_it()
    {
        Seed("Call", "Other");
        Start();
        Press("ctrl+t");
        Type("soon");
        Press("enter");
        Assert.Contains("isn't a day", Screen, StringComparison.Ordinal);
        Press("ctrl+a");
        Type("mon 15:00");
        Press("enter");
        Assert.Equal("Call due:2026-11-09 at:15:00\nOther\n", File_);
        Assert.EndsWith("Mon 15:00 │", Row("Call"), StringComparison.Ordinal);

        Press("ctrl+t");
        Assert.Contains("2026-11-09 15:00", Screen, StringComparison.Ordinal);
        Press("ctrl+a backspace enter");
        Assert.Equal("Call\nOther\n", File_);
    }

    [Fact]
    public void Delete_asks_first_and_selects_the_next()
    {
        Seed("a", "b", "c");
        Start();
        Press("down delete");
        Assert.Contains("Delete “b”?", Screen, StringComparison.Ordinal);
        Press("enter");                          // Cancel is selected first
        Assert.Equal("a\nb\nc\n", File_);

        Press("delete left enter");
        Assert.Equal("a\nc\n", File_);
        Assert.Equal("c", Tasks.Selected?.Title);
    }

    [Fact]
    public void A_task_changed_elsewhere_is_not_overwritten()
    {
        Seed("Call", "Other");
        Start();
        File.WriteAllText(_store.FilePath, "Call the dentist\nOther\n");
        Press("ctrl+d");
        Assert.Equal("Call the dentist\nOther\n", File_);
        Assert.Contains("changed elsewhere", Screen, StringComparison.Ordinal);
        Assert.Contains("Call the dentist", Screen, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_can_not_be_written_says_why_and_shows_it_as_it_is()
    {
        Seed("Call");
        Start();
        File.SetUnixFileMode(_store.Directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        Press("ctrl+d");
        Assert.Contains("Can't save", Screen, StringComparison.Ordinal);
        Assert.False(Tasks.Selected?.Done);
    }

    [Fact]
    public void The_file_is_read_again_a_minute_after_it_changes()
    {
        Seed("Call");
        Start();
        File.WriteAllText(_store.FilePath, "Call\nAdded elsewhere\n");
        File.SetLastWriteTimeUtc(_store.FilePath, DateTime.UtcNow.AddSeconds(5));
        Assert.DoesNotContain("Added elsewhere", Screen, StringComparison.Ordinal);
        Wait(TasksScreen.CheckEvery);
        Assert.Contains("Added elsewhere", Screen, StringComparison.Ordinal);
    }

    // ---- Title and reminders ----

    [Fact]
    public void The_title_counts_overdue_and_today_and_moves_with_the_clock()
    {
        Seed("A due:2026-11-06", "B due:2026-11-07 at:15:00", "C due:2026-11-07 at:18:00", "x 2026-11-07 D due:2026-11-06");
        Start();
        Assert.Equal("1 overdue, 2 today", _titles[^1]);
        Wait(TimeSpan.FromHours(3.5));
        Assert.Equal("2 overdue, 1 today", _titles[^1]);
    }

    [Fact]
    public void No_title_when_nothing_is_due_soon()
    {
        Seed("Plain", "Later due:2026-11-20");
        Start();
        Assert.DoesNotContain(_titles, t => t.Length > 0);
    }

    [Fact]
    public void A_task_falling_due_notifies_once_on_time_with_done_and_snooze()
    {
        Seed("Call the dentist due:2026-11-07 at:12:30");
        Start(inDesk: true);
        Assert.Empty(_sent);

        Wait(TimeSpan.FromMinutes(29));
        Assert.Empty(_sent);
        Wait(TimeSpan.FromMinutes(1));
        var sent = Assert.Single(_sent);
        Assert.Equal("Call the dentist", sent.Title);
        Assert.Equal(["done", "snooze"], sent.Actions!.Select(a => a.Id));

        Wait(TimeSpan.FromMinutes(5));
        Assert.Single(_sent);
    }

    [Fact]
    public void Several_overdue_at_start_are_one_notification_and_one_is_its_own()
    {
        Seed("A due:2026-11-06", "B due:2026-11-07");
        Start(inDesk: true);
        var sent = Assert.Single(_sent);
        Assert.Equal("2 tasks overdue", sent.Title);
        Assert.Null(sent.Actions);

        _harness!.Dispose();
        _sent.Clear();
        Seed("A due:2026-11-06");
        Start(inDesk: true);
        Assert.Equal("A", Assert.Single(_sent).Title);
        Assert.NotNull(_sent[0].Actions);
    }

    [Fact]
    public void Outside_Desk_a_reminder_is_a_toast()
    {
        Seed("Call due:2026-11-07 at:12:30");
        Start();
        Wait(TimeSpan.FromMinutes(30));
        Assert.Contains("Call: Due 12:30", Screen, StringComparison.Ordinal);
    }

    [Fact]
    public void Done_from_a_notification_marks_it_done()
    {
        Seed("Call due:2026-11-07 at:12:30", "Other");
        Start(inDesk: true);
        Wait(TimeSpan.FromMinutes(30));
        Tasks.Act(_sent[0].Id, "done");
        Harness.RunUntilIdle();
        Assert.Equal("x 2026-11-07 Call due:2026-11-07 at:12:30\nOther\n", File_);
    }

    [Fact]
    public void Snooze_reminds_again_in_an_hour_and_says_so_in_the_file()
    {
        Seed("Call due:2026-11-07 at:12:30");
        Start(inDesk: true);
        Wait(TimeSpan.FromMinutes(30));
        Tasks.Act(_sent[0].Id, "snooze");
        Harness.RunUntilIdle();
        Assert.Equal("Call due:2026-11-07 at:12:30 snooze:2026-11-07T13:30\n", File_);

        Wait(TimeSpan.FromMinutes(59));
        Assert.Single(_sent);
        Wait(TimeSpan.FromMinutes(1));
        Assert.Equal(2, _sent.Count);
        Assert.Equal("Call", _sent[1].Title);
    }

    [Fact]
    public void An_action_on_a_task_changed_since_does_nothing_but_say_so()
    {
        Seed("Call due:2026-11-07 at:12:30");
        Start(inDesk: true);
        Wait(TimeSpan.FromMinutes(30));
        File.WriteAllText(_store.FilePath, "Call someone else due:2026-11-07 at:12:30\n");
        Tasks.Act(_sent[0].Id, "done");
        Tasks.Act("unknown", "done");
        Harness.RunUntilIdle();
        Assert.Equal("Call someone else due:2026-11-07 at:12:30\n", File_);
        Assert.Contains("changed since", Screen, StringComparison.Ordinal);
    }

    [Fact]
    public void F1_shows_the_keys()
    {
        Start();
        Press("f1");
        Assert.IsType<HelpOverlay>(Harness.App.Overlays.Top);
        Assert.Contains("ctrl+t", Screen, StringComparison.Ordinal);
    }
}

namespace MiniTui.Desk.Tasks.Tests;

/// <summary>todo.txt lines (spec §13.9) and what's typed for a due date.</summary>
public sealed class TodoTaskTests
{
    private static readonly DateOnly Today = new(2026, 11, 7); // a Saturday

    [Theory]
    [InlineData("Buy milk", false, null, null, "Buy milk")]
    [InlineData("(A) Call the dentist +health", false, 'A', null, "Call the dentist +health")]
    [InlineData("(A) 2026-11-01 Call the dentist", false, 'A', "2026-11-01", "Call the dentist")]
    [InlineData("2026-11-01 Buy milk", false, null, "2026-11-01", "Buy milk")]
    [InlineData("x 2026-11-07 2026-11-01 Renew passport pri:B", true, null, "2026-11-01", "Renew passport pri:B")]
    [InlineData("(a) lower case isn't a priority", false, null, null, "(a) lower case isn't a priority")]
    [InlineData("xylophone lessons", false, null, null, "xylophone lessons")]
    [InlineData("(A)no space", false, null, null, "(A)no space")]
    public void A_line_has_its_parts(string line, bool done, char? priority, string? created, string body)
    {
        var task = TodoTask.Parse(line);
        Assert.Equal(done, task.Done);
        Assert.Equal(priority, task.Priority);
        Assert.Equal(created is null ? null : DateOnly.Parse(created, System.Globalization.CultureInfo.InvariantCulture), task.Created);
        Assert.Equal(body, task.Body);
        Assert.Equal(line, task.Line);
    }

    [Fact]
    public void Due_tags_give_the_time_at_9_without_a_time_and_bad_ones_are_text()
    {
        Assert.Equal(new DateTime(2026, 11, 9, 15, 0, 0), TodoTask.Parse("Dentist due:2026-11-09 at:15:00").Due);
        Assert.Equal(new DateTime(2026, 11, 9, 9, 0, 0), TodoTask.Parse("Dentist due:2026-11-09").Due);
        Assert.Null(TodoTask.Parse("Dentist at:15:00").Due);                    // a time without a day
        var bad = TodoTask.Parse("Dentist due:soon at:noon");
        Assert.Null(bad.Due);
        Assert.Equal("Dentist due:soon at:noon", bad.Title);
    }

    [Fact]
    public void The_title_leaves_out_the_tags_shown_elsewhere_and_keeps_the_rest()
    {
        var task = TodoTask.Parse("(B) Call +health @phone due:2026-11-09 at:15:00 snooze:2026-11-09T16:00 url:https://x.org");
        Assert.Equal("(B) Call +health @phone url:https://x.org", task.Title);
        Assert.Equal("(B) Call +health @phone due:2026-11-09 at:15:00 snooze:2026-11-09T16:00 url:https://x.org", task.Editable);
    }

    [Fact]
    public void Done_moves_the_priority_to_a_tag_drops_a_snooze_and_undoing_puts_it_back()
    {
        var task = TodoTask.Parse("(A) 2026-11-01 Call due:2026-11-07 snooze:2026-11-07T10:00");
        var done = task.WithDone(true, Today);
        Assert.Equal("x 2026-11-07 2026-11-01 Call due:2026-11-07 pri:A", done.Line);
        Assert.Equal("(A) Call", done.Title);
        Assert.Null(done.Reminder);

        var undone = TodoTask.Parse(done.Line).WithDone(false, Today);
        Assert.Equal("(A) 2026-11-01 Call due:2026-11-07", undone.Line);
        Assert.Same(undone, undone.WithDone(false, Today));
    }

    [Fact]
    public void A_due_date_replaces_the_tags_where_they_are_and_clearing_removes_them()
    {
        var task = TodoTask.Parse("Call due:2026-11-07 +health at:09:30 snooze:2026-11-07T10:00");
        Assert.Equal("Call due:2026-11-20 +health at:15:00", task.WithDue(new DateOnly(2026, 11, 20), new TimeOnly(15, 0)).Line);
        Assert.Equal("Call due:2026-11-20 +health", task.WithDue(new DateOnly(2026, 11, 20), null).Line);
        Assert.Equal("Call +health", task.WithDue(null, null).Line);
        Assert.Equal("Plan due:2026-11-20", TodoTask.Parse("Plan").WithDue(new DateOnly(2026, 11, 20), null).Line);
    }

    [Fact]
    public void A_snooze_reminds_instead_of_the_due_time()
    {
        var task = TodoTask.Parse("Call due:2026-11-07 at:09:00").WithSnooze(new DateTime(2026, 11, 7, 10, 30, 0));
        Assert.Equal("Call due:2026-11-07 at:09:00 snooze:2026-11-07T10:30", task.Line);
        Assert.Equal(new DateTime(2026, 11, 7, 10, 30, 0), TodoTask.Parse(task.Line).Reminder);
    }

    [Fact]
    public void New_and_edited_text_keep_the_dates_and_take_a_priority_in_front()
    {
        Assert.Equal("(C) 2026-11-07 Water plants", TodoTask.New("  (C) Water plants ", Today).Line);
        var task = TodoTask.Parse("(A) 2026-11-01 Call");
        Assert.Equal("2026-11-01 Call the dentist", task.WithText("Call the dentist").Line);
        Assert.Equal("(B) 2026-11-01 Call", task.WithText("(B) Call").Line);
        var done = TodoTask.Parse("x 2026-11-07 2026-11-01 Call pri:A");
        Assert.Equal("x 2026-11-07 2026-11-01 Call pri:B", done.WithText("(B) Call pri:A").Line);
    }

    [Theory]
    [InlineData("", "A task needs some text")]
    [InlineData("(A) ", "A task needs some text")]
    [InlineData("x marks the spot", "A task can't start with “x ” or a date")]
    [InlineData("2026-11-20 party", "A task can't start with “x ” or a date")]
    [InlineData("tab\there", "A task can't contain control characters")]
    [InlineData("Buy milk", null)]
    public void Problem_says_what_can_not_be_a_task(string typed, string? problem) => Assert.Equal(problem, TodoTask.Problem(typed));

    // ---- Typed due dates ----

    [Theory]
    [InlineData("today", "2026-11-07", null)]
    [InlineData("Tomorrow 15:00", "2026-11-08", "15:00")]
    [InlineData("mon", "2026-11-09", null)]
    [InlineData("saturday", "2026-11-14", null)]       // today is Saturday: the next one
    [InlineData("+3d", "2026-11-10", null)]
    [InlineData("+2w 9:30", "2026-11-21", "09:30")]
    [InlineData("2026-12-24", "2026-12-24", null)]
    [InlineData("18:00", "2026-11-07", "18:00")]
    public void A_typed_due_date_is_a_day_and_maybe_a_time(string typed, string date, string? time)
    {
        var (d, t, problem) = Dates.Parse(typed, Today);
        Assert.Null(problem);
        Assert.Equal(date, d?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(time, t is { } x ? Dates.Clock(x) : null);
    }

    [Theory]
    [InlineData("soon")]
    [InlineData("mo")]
    [InlineData("tomorrow noon")]
    [InlineData("+3m")]
    [InlineData("2026-13-01")]
    [InlineData("today 15:00 please")]
    public void Anything_else_has_a_problem(string typed) => Assert.NotNull(Dates.Parse(typed, Today).Problem);

    [Fact]
    public void Empty_is_no_due_date() => Assert.Equal((null, null, null), Dates.Parse("  ", Today));

    [Theory]
    [InlineData("Call due:2026-11-07 at:15:00", "Today 15:00")]
    [InlineData("Call due:2026-11-07 at:08:00", "Overdue · 08:00")]
    [InlineData("Call due:2026-11-07", "Overdue · Today")]               // 09:00 has gone
    [InlineData("Call due:2026-11-05", "Overdue · Nov 5")]
    [InlineData("Call due:2026-11-08", "Tomorrow")]
    [InlineData("Call due:2026-11-11", "Wed")]
    [InlineData("Call due:2026-11-20", "Nov 20")]
    [InlineData("Call due:2027-01-04", "2027-01-04")]
    [InlineData("Call", "")]
    [InlineData("x 2026-11-06 Call due:2026-11-05", "Done · Nov 6")]
    public void The_hint_says_when_it_is_due(string line, string hint) =>
        Assert.Equal(hint, Dates.Hint(TodoTask.Parse(line), new DateTime(2026, 11, 7, 12, 0, 0)));

    [Theory]
    [InlineData("Call due:2026-11-07 at:12:00", "Due 12:00")]
    [InlineData("Call due:2026-11-07", "Due today")]
    [InlineData("Call due:2026-11-05 snooze:2026-11-07T12:00", "Due Nov 5")]
    public void A_reminder_as_it_comes_says_due_not_overdue(string line, string body) =>
        Assert.Equal(body, Dates.Falling(TodoTask.Parse(line), new DateTime(2026, 11, 7, 12, 0, 30)));
}

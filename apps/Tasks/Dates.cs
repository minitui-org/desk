using System.Globalization;

namespace MiniTui.Desk.Tasks;

/// <summary>What's typed for <c>Ctrl+T</c>, and due times as the list shows them (spec §13.9).</summary>
internal static class Dates
{
    /// <summary>
    /// <c>today</c>, <c>tomorrow</c>, a weekday (the next one after today), <c>+3d</c>, <c>+2w</c> or <c>2026-11-20</c>,
    /// then optionally a time, <c>15:00</c>. A time alone is today. Empty is no due date.
    /// </summary>
    public static (DateOnly? Date, TimeOnly? Time, string? Problem) Parse(string typed, DateOnly today)
    {
        var words = typed.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return (null, null, null);
        if (words.Length > 2) return (null, null, "Type a day, then a time if you like: tomorrow 15:00");

        TimeOnly? time = null;
        if (TryTime(words[^1], out var t))
        {
            time = t;
            words = words[..^1];
            if (words.Length == 0) return (today, time, null);
        }
        if (words.Length != 1) return (null, null, $"“{words[^1]}” isn't a time like 15:00");
        return Day(words[0], today) is { } date
            ? (date, time, null)
            : (null, null, $"“{words[0]}” isn't a day. Try today, tomorrow, fri, +3d or 2026-11-20");
    }

    /// <summary>A task's due date and time as <see cref="Parse"/> takes them, to start the prompt with.</summary>
    public static string Typed(TodoTask task) => task.DueDate is { } date
        ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + (task.DueTime is { } time ? " " + Clock(time) : "")
        : "";

    /// <summary>When a task is due, for the right of its row: <c>Today 15:00</c>, <c>Overdue · Nov 5</c>.</summary>
    public static string Hint(TodoTask task, DateTime now)
    {
        if (task.Done) return task.DoneOn is { } doneOn ? "Done · " + Day(doneOn, DateOnly.FromDateTime(now)) : "Done";
        if (task.DueDate is not { } date) return "";
        var today = DateOnly.FromDateTime(now);
        var time = task.DueTime is { } t ? " " + Clock(t) : "";
        if (task.Due < now)
            return "Overdue · " + (date == today && task.DueTime is { } at ? Clock(at) : Day(date, today));
        return Day(date, today) + time;
    }

    /// <summary>
    /// What a reminder says as it comes: <c>Due 15:00</c>, <c>Due today</c>, or the day for a snooze that outlasted
    /// it. The moment has only just passed, so it isn't "overdue".
    /// </summary>
    public static string Falling(TodoTask task, DateTime now)
    {
        if (task.DueDate is not { } date) return "";
        var today = DateOnly.FromDateTime(now);
        if (date != today) return "Due " + Day(date, today);
        return task.DueTime is { } time ? "Due " + Clock(time) : "Due today";
    }

    /// <summary>Today, Tomorrow, a weekday within the week, Nov 20, or the full date in another year.</summary>
    public static string Day(DateOnly date, DateOnly today)
    {
        var days = date.DayNumber - today.DayNumber;
        if (days == 0) return "Today";
        if (days == 1) return "Tomorrow";
        if (days is > 1 and < 7) return date.ToString("ddd", CultureInfo.InvariantCulture);
        return date.Year == today.Year
            ? date.ToString("MMM d", CultureInfo.InvariantCulture)
            : date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    public static string Clock(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static DateOnly? Day(string word, DateOnly today)
    {
        switch (word)
        {
            case "today": return today;
            case "tomorrow": return today.AddDays(1);
        }
        // A weekday, from its first three letters to its whole name: the next one after today
        var day = Array.FindIndex(CultureInfo.InvariantCulture.DateTimeFormat.DayNames,
            name => word.Length >= 3 && name.StartsWith(word, StringComparison.OrdinalIgnoreCase));
        if (day >= 0)
        {
            var ahead = (day - (int)today.DayOfWeek + 7) % 7;
            return today.AddDays(ahead == 0 ? 7 : ahead);
        }
        if (word.Length >= 3 && word[0] == '+' && int.TryParse(word[1..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n <= 3650)
        {
            return word[^1] switch
            {
                'd' => today.AddDays(n),
                'w' => today.AddDays(7 * n),
                _ => null,
            };
        }
        return DateOnly.TryParseExact(word, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }

    private static bool TryTime(string word, out TimeOnly time) =>
        TimeOnly.TryParseExact(word, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
}

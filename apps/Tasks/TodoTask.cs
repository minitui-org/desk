using System.Globalization;
using System.Text.RegularExpressions;

namespace MiniTui.Desk.Tasks;

/// <summary>
/// One line of todo.txt (spec §13.9): <c>x</c> and the date it was done, a priority, the date it was made, then the
/// text with its tags. <see cref="Line"/> is the line as read, so a task nobody changes is written back as it was.
/// </summary>
internal sealed partial record TodoTask
{
    /// <summary>A task with only a date is due at this time on the day.</summary>
    public static readonly TimeOnly DefaultTime = new(9, 0);

    private const string DateFormat = "yyyy-MM-dd";

    private TodoTask(string line, bool done, DateOnly? doneOn, char? priority, DateOnly? created, string body)
    {
        Line = line;
        Done = done;
        DoneOn = doneOn;
        Priority = priority;
        Created = created;
        Body = body;
    }

    /// <summary>The line, as read or as it will be written.</summary>
    public string Line { get; }

    public bool Done { get; }
    public DateOnly? DoneOn { get; }

    /// <summary><c>A</c> to <c>Z</c>, or null. A done task keeps its priority as a <c>pri:</c> tag instead.</summary>
    public char? Priority { get; }

    public DateOnly? Created { get; }

    /// <summary>The text, tags included.</summary>
    public string Body { get; }

    public DateOnly? DueDate => Tag(Body, "due") is { } due && TryDate(due, out var date) ? date : null;

    /// <summary>The <c>at:</c> time, which only counts with a due date.</summary>
    public TimeOnly? DueTime => DueDate is not null && Tag(Body, "at") is { } at && TryTime(at, out var time) ? time : null;

    /// <summary>When it's due: its date at its time, or at 09:00.</summary>
    public DateTime? Due => DueDate is { } date ? date.ToDateTime(DueTime ?? DefaultTime) : null;

    /// <summary>A snoozed reminder, local time.</summary>
    public DateTime? Snoozed =>
        Tag(Body, "snooze") is { } snooze && DateTime.TryParseExact(snooze, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var at) ? at : null;

    /// <summary>When it reminds: a snooze, or when it's due. Never for a done task.</summary>
    public DateTime? Reminder => Done ? null : Snoozed ?? Due;

    /// <summary>The text without the tags Tasks shows another way, with the priority in front.</summary>
    public string Title
    {
        get
        {
            var words = Body.Split(' ').Where(w => !IsShownElsewhere(w));
            var text = string.Join(' ', words).Trim();
            var priority = Priority ?? (Tag(Body, "pri") is [var p] && char.IsAsciiLetterUpper(p) ? p : null);
            return priority is { } letter ? $"({letter}) {text}" : text;
        }
    }

    /// <summary>The priority and text as typed to edit them: <c>(A) Call the dentist due:2026-11-09</c>.</summary>
    public string Editable => Priority is { } p ? $"({p}) {Body}" : Body;

    public static TodoTask Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var rest = line;
        var done = false;
        DateOnly? doneOn = null;
        char? priority = null;
        if (rest.StartsWith("x ", StringComparison.Ordinal))
        {
            done = true;
            rest = rest[2..];
            doneOn = TakeDate(ref rest);
        }
        else if (rest.Length >= 4 && rest[0] == '(' && char.IsAsciiLetterUpper(rest[1]) && rest[2] == ')' && rest[3] == ' ')
        {
            priority = rest[1];
            rest = rest[4..];
        }
        var created = TakeDate(ref rest);
        return new TodoTask(line, done, doneOn, priority, created, rest);
    }

    /// <summary>A new task made today from what was typed, which may start with a priority.</summary>
    public static TodoTask New(string typed, DateOnly today)
    {
        var (priority, body) = SplitPriority(typed.Trim());
        return Build(false, null, priority, today, body);
    }

    /// <summary>Done today, or not done again. A priority becomes a <c>pri:</c> tag while it's done, and a snooze goes.</summary>
    public TodoTask WithDone(bool done, DateOnly today)
    {
        if (done == Done) return this;
        if (done)
        {
            var body = WithoutTag(Body, "snooze");
            if (Priority is { } p) body = WithTag(body, "pri", p.ToString());
            return Build(true, today, null, Created, body);
        }
        var priority = Tag(Body, "pri") is [var letter] && char.IsAsciiLetterUpper(letter) ? letter : (char?)null;
        return Build(false, null, priority, Created, priority is null ? Body : WithoutTag(Body, "pri"));
    }

    /// <summary>Due on <paramref name="date"/> at <paramref name="time"/>, or not due with a null date. A snooze goes.</summary>
    public TodoTask WithDue(DateOnly? date, TimeOnly? time)
    {
        var body = WithoutTag(Body, "snooze");
        body = date is { } d ? WithTag(body, "due", d.ToString(DateFormat, CultureInfo.InvariantCulture)) : WithoutTag(body, "due");
        body = date is not null && time is { } t ? WithTag(body, "at", t.ToString("HH:mm", CultureInfo.InvariantCulture)) : WithoutTag(body, "at");
        return Build(Done, DoneOn, Priority, Created, body);
    }

    public TodoTask WithSnooze(DateTime until) =>
        Build(Done, DoneOn, Priority, Created, WithTag(Body, "snooze", until.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture)));

    /// <summary>New text and priority, as <see cref="Editable"/> shows them. The dates stay.</summary>
    public TodoTask WithText(string typed)
    {
        var (priority, body) = SplitPriority(typed.Trim());
        // A done task's priority is a tag, so one typed in front goes there
        if (Done && priority is { } p) return Build(true, DoneOn, null, Created, WithTag(body, "pri", p.ToString()));
        return Build(Done, DoneOn, priority, Created, body);
    }

    /// <summary>Why <paramref name="typed"/> can't be a task's text, or null if it can.</summary>
    public static string? Problem(string typed)
    {
        var (_, body) = SplitPriority(typed.Trim());
        if (body.Length == 0) return "A task needs some text";
        if (body.Any(char.IsControl)) return "A task can't contain control characters";
        if (Parse(body) is { Done: true } || TakeDate(ref body) is not null) return "A task can't start with “x ” or a date";
        return null;
    }

    private static TodoTask Build(bool done, DateOnly? doneOn, char? priority, DateOnly? created, string body)
    {
        var parts = new List<string>();
        if (done)
        {
            parts.Add("x");
            if (doneOn is { } d) parts.Add(d.ToString(DateFormat, CultureInfo.InvariantCulture));
        }
        else if (priority is { } p)
        {
            parts.Add($"({p})");
        }
        if (created is { } c) parts.Add(c.ToString(DateFormat, CultureInfo.InvariantCulture));
        parts.Add(body);
        return new TodoTask(string.Join(' ', parts), done, done ? doneOn : null, done ? null : priority, created, body);
    }

    // "(A) text", or "(A)" alone, which is a priority with no text
    private static (char? Priority, string Body) SplitPriority(string typed) =>
        typed.Length >= 3 && typed[0] == '(' && char.IsAsciiLetterUpper(typed[1]) && typed[2] == ')' && (typed.Length == 3 || typed[3] == ' ')
            ? (typed[1], typed[3..].TrimStart())
            : (null, typed);

    private static DateOnly? TakeDate(ref string rest)
    {
        if (rest.Length >= 11 && rest[10] == ' ' && TryDate(rest[..10], out var date))
        {
            rest = rest[11..];
            return date;
        }
        return null;
    }

    private static bool TryDate(string text, out DateOnly date) =>
        DateOnly.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static bool TryTime(string text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    // The tags Tasks shows on the right, or not at all
    private static bool IsShownElsewhere(string word) => TagWord().Match(word) is { Success: true } m && m.Groups[1].Value switch
    {
        "due" => TryDate(m.Groups[2].Value, out _),
        "at" => TryTime(m.Groups[2].Value, out _),
        "snooze" => true,
        "pri" => m.Groups[2].Value is [var p] && char.IsAsciiLetterUpper(p),
        _ => false,
    };

    /// <summary>The first <c>key:value</c> tag's value.</summary>
    private static string? Tag(string body, string key) =>
        body.Split(' ').Select(w => TagWord().Match(w)).FirstOrDefault(m => m.Success && m.Groups[1].Value == key)?.Groups[2].Value;

    /// <summary>Replace the first <c>key:</c> tag's value where it is, or add the tag at the end.</summary>
    private static string WithTag(string body, string key, string value)
    {
        var words = body.Split(' ').ToList();
        var index = words.FindIndex(w => TagWord().Match(w) is { Success: true } m && m.Groups[1].Value == key);
        if (index >= 0) words[index] = $"{key}:{value}";
        else words.Add($"{key}:{value}");
        return string.Join(' ', words.Where(w => w.Length > 0));
    }

    private static string WithoutTag(string body, string key) =>
        string.Join(' ', body.Split(' ').Where(w => !(TagWord().Match(w) is { Success: true } m && m.Groups[1].Value == key)));

    [GeneratedRegex(@"^(due|at|snooze|pri):(\S+)$")]
    private static partial Regex TagWord();
}

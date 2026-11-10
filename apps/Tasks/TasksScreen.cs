using MiniTui.Desk.Protocol;

namespace MiniTui.Desk.Tasks;

/// <summary>
/// The tasks in one list, filtered by typing (spec §13.9). Every change goes straight to the file, so there's nothing
/// to save. Tasks falling due notify while it runs: through <c>notify</c> inside Desk, or as toasts.
/// </summary>
internal sealed class TasksScreen : Screen
{
    /// <summary>How often the file is looked at for changes made elsewhere, and reminders checked at the latest.</summary>
    internal static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(1);

    internal static readonly TimeSpan SnoozeFor = TimeSpan.FromHours(1);

    internal const string DoneAction = "done";
    internal const string SnoozeAction = "snooze";

    private readonly TaskStore _store;
    private readonly TimeProvider _time;
    private readonly Action<string> _setTitle;
    private readonly Action<DeskNotification>? _notify;
    private readonly TaskList _list;
    private readonly Dictionary<string, string> _notified = []; // notification id → the task's line then
    private ITimer? _timer;
    private App? _app;
    private List<TodoTask> _tasks = [];
    private DateTime? _read;      // the file's time when last read
    private DateTime _checked;    // reminders up to here have been given
    private int _nextId;
    private string? _title;

    public TasksScreen(TaskStore store, TimeProvider? time = null, Action<string>? setTitle = null, Action<DeskNotification>? notify = null)
    {
        _store = store;
        _time = time ?? TimeProvider.System;
        _setTitle = setTitle ?? (_ => { });
        _notify = notify;
        _list = new TaskList(this);
        AddChild(_list);
        Focus.Add(_list);

        Keys.Add("ctrl+n", "New", New)
            .Add("ctrl+d", "Done", ToggleDone)
            .Add("ctrl+t", "Due", SetDue)
            .Add("ctrl+q", "Quit", () => App.Quit());
    }

    public override string Title => "Tasks";

    /// <summary>The tasks in the list's order: due ones by when, then the rest by priority, then the done ones.</summary>
    internal IReadOnlyList<TodoTask> Tasks => _tasks;

    internal IEnumerable<string> Showing => _list.State.Matches.Select(i => _tasks[i].Title);

    internal TodoTask? Selected => _list.State.Selected is { } row ? _tasks[row] : null;

    private DateTime Now => _time.GetLocalNow().DateTime;

    private DateOnly Today => DateOnly.FromDateTime(Now);

    // ---- What the app does with it ----

    /// <summary>Back in front in Desk: show the file as it is now.</summary>
    public void Refresh() => Reload();

    /// <summary>A notification's action, chosen in Desk.</summary>
    public void Act(string notification, string action)
    {
        if (!_notified.TryGetValue(notification, out var line)) return;
        Reload();
        if (_tasks.FirstOrDefault(t => t.Line == line) is not { } task)
        {
            Toast("That task has changed since", ToastLevel.Warning);
            return;
        }
        switch (action)
        {
            case DoneAction:
                Change(task, task.WithDone(true, Today));
                break;
            case SnoozeAction:
                Change(task, task.WithSnooze(Now + SnoozeFor));
                break;
        }
    }

    // ---- Lifecycle ----

    protected override void OnMount()
    {
        _app = App;
        Reload();
        _checked = Now;
        var overdue = _tasks.Where(t => t.Reminder <= _checked).ToList();
        if (overdue is [var one]) Remind(one, Dates.Hint(one, _checked));
        else if (overdue.Count > 1) Notify("overdue", $"{overdue.Count} tasks overdue", string.Join(", ", overdue.Select(t => t.Title)), null);
        _timer = _time.CreateTimer(_ => App.Post(Check), null, NextCheck(), Timeout.InfiniteTimeSpan);
    }

    protected override void OnUnmount()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public override void Render(CellBuffer buf, Rect area, RenderContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.Render(_list, buf, area);
    }

    // ---- Reminders ----

    /// <summary>The file again if it changed, then a reminder for each task that fell due since the last check.</summary>
    private void Check()
    {
        if (_store.Changed != _read) Reload();
        else
        {
            _list.Refilter(); // the hints and the title move on with the clock
            UpdateTitle();
        }
        var now = Now;
        foreach (var task in _tasks.Where(t => t.Reminder > _checked && t.Reminder <= now).ToList()) Remind(task, Dates.Falling(task, now));
        _checked = now;
        _timer?.Change(NextCheck(), Timeout.InfiniteTimeSpan);
    }

    // The next reminder, or in a minute, whichever is sooner
    private TimeSpan NextCheck()
    {
        var now = Now;
        var next = _tasks.Select(t => t.Reminder).Where(r => r > now).Min();
        var wait = next is { } at ? at - now : CheckEvery;
        return wait < CheckEvery ? wait : CheckEvery;
    }

    private void Remind(TodoTask task, string body)
    {
        var id = "task-" + ++_nextId;
        _notified[id] = task.Line;
        Notify(id, task.Title, body, [new DeskAction(DoneAction, "Done"), new DeskAction(SnoozeAction, "Snooze 1 hour")]);
    }

    private void Notify(string id, string title, string body, IReadOnlyList<DeskAction>? actions)
    {
        if (_notify is { } notify) notify(new DeskNotification(id, title, body) { Actions = actions });
        else Toast(body.Length > 0 ? $"{title}: {body}" : title);
    }

    // ---- Tasks ----

    private void Reload()
    {
        try
        {
            _read = _store.Changed;
            _tasks = Sorted(_store.Read());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Toast($"Can't read {_store.FilePath}: {e.Message}", ToastLevel.Error);
        }
        _list.Refilter();
        UpdateTitle();
    }

    private static List<TodoTask> Sorted(List<TodoTask> tasks)
    {
        var order = tasks.Select((task, index) => (task, index)).ToList();
        return [.. order
            .OrderBy(t => t.task.Done ? 2 : t.task.Due is null ? 1 : 0)
            .ThenBy(t => t.task.Done ? DateTime.MaxValue.Ticks - (t.task.DoneOn?.DayNumber ?? 0) : t.task.Due?.Ticks ?? 0)
            .ThenBy(t => t.task.Priority ?? '[')   // after Z
            .ThenBy(t => t.index)
            .Select(t => t.task)];
    }

    private void UpdateTitle()
    {
        var now = Now;
        var overdue = _tasks.Count(t => !t.Done && t.Due < now);
        var today = _tasks.Count(t => !t.Done && t.Due >= now && t.DueDate == Today);
        var parts = new List<string>();
        if (overdue > 0) parts.Add($"{overdue} overdue");
        if (today > 0) parts.Add($"{today} today");
        var title = string.Join(", ", parts);
        if (title == _title) return;
        _title = title;
        _setTitle(title);
    }

    /// <summary>Write one task's change, or its removal. Says so if it changed elsewhere first, and shows the file.</summary>
    private void Change(TodoTask was, TodoTask? now)
    {
        try
        {
            if (!_store.Change(was, now)) Toast("That task changed elsewhere, so it's shown as it is now", ToastLevel.Warning);
            else if (now is not null) _list.Follow(now.Line);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Toast($"Can't save {_store.FilePath}: {e.Message}", ToastLevel.Error);
        }
        Reload();
    }

    private void Toast(string text, ToastLevel level = ToastLevel.Info) => _app?.Toasts.Push(text, level);

    private async void New()
    {
        if (await App.Overlays.PromptAsync("New task", _list.Filter, TodoTask.Problem, "What to do, with +project or due:2026-11-20") is not { } typed) return;
        var task = TodoTask.New(typed, Today);
        try
        {
            _store.Add(task);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Toast($"Can't save {_store.FilePath}: {e.Message}", ToastLevel.Error);
            return;
        }
        _list.ClearFilter();
        _list.Follow(task.Line);
        Reload();
    }

    private async void Edit()
    {
        if (Selected is not { } task) return;
        if (await App.Overlays.PromptAsync("Edit task", task.Editable, TodoTask.Problem) is not { } typed) return;
        var now = task.WithText(typed);
        if (now.Line != task.Line) Change(task, now);
    }

    private void ToggleDone()
    {
        if (Selected is { } task) Change(task, task.WithDone(!task.Done, Today));
    }

    private async void SetDue()
    {
        if (Selected is not { } task) return;
        var today = Today;
        if (await App.Overlays.PromptAsync("Due", Dates.Typed(task), typed => Dates.Parse(typed, today).Problem, "tomorrow 15:00") is not { } answer) return;
        var (date, time, _) = Dates.Parse(answer, today);
        var now = task.WithDue(date, time);
        if (now.Line != task.Line) Change(task, now);
    }

    private async void Delete()
    {
        if (Selected is not { } task) return;
        if (!await App.Overlays.ConfirmAsync($"Delete “{task.Title}”?", "Delete", "Cancel", yesFirst: false)) return;
        var index = _list.State.Matches.ToList().IndexOf(_list.State.Selected ?? -1);
        Change(task, null);
        var matches = _list.State.Matches;
        if (matches.Count > 0) _list.State.Select(matches[Math.Clamp(index, 0, matches.Count - 1)]);
    }

    /// <summary>MiniTui's filter list of the tasks, with each one's due time on the right.</summary>
    private sealed class TaskList : Component
    {
        private readonly TasksScreen _screen;
        private string? _follow; // the line to select once the tasks are read again
        private Theme? _theme;   // the theme the rows' styles came from

        public TaskList(TasksScreen screen)
        {
            _screen = screen;
            State.Activated += (_, _) => _screen.Edit();
        }

        // The list has the keys, j and k among them, until / asks for the filter
        public FilterListState State { get; } = new() { TypeToFilter = false };

        public override string KeysGroup => "List";
        public override bool CapturesText => State.Filtering;

        public string Filter => State.Filter.Text;

        protected override void OnMount()
        {
            Keys.Add("enter", "Edit", _screen.Edit)
                .Add("/", "Filter", () => State.Filtering = true)
                .Add("delete", "Delete", _screen.Delete);
        }

        public void ClearFilter() => State.Filter.Clear();

        /// <summary>Select the task with this line next time the rows are set, wherever it goes in the order.</summary>
        public void Follow(string line) => _follow = line;

        public void Refilter()
        {
            var now = _screen.Now;
            var theme = _theme = Theme.Current;
            // A key of the line and which of its copies it is, so identical lines are rows of their own
            var seen = new Dictionary<string, int>();
            var rows = _screen._tasks.Select(t =>
            {
                seen[t.Line] = seen.GetValueOrDefault(t.Line) + 1;
                // Done tasks muted and an overdue one's time in the error color, as well as the words in the hint
                return new FilterRow(t.Title, Hint: Dates.Hint(t, now), Key: (t.Line, seen[t.Line]))
                {
                    Style = t.Done ? theme.TextMuted : null,
                    HintStyle = !t.Done && t.Due < now ? theme.Error : null,
                };
            }).ToList();
            State.SetRows(rows);
            if (_follow is { } line && _screen._tasks.FindIndex(t => t.Line == line) is >= 0 and var index) State.Select(index);
            _follow = null;
        }

        public override bool HandleKey(ConsoleKeyInfo key)
        {
            // The list's keys: what it doesn't use (Enter, Delete, letters) is the bindings'
            if (!State.Filtering) return State.HandleKey(key);

            // With nothing typed, Home and End go to the first and last task
            var ctrlOrAlt = (key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) != 0;
            if (key.Key is ConsoleKey.Home or ConsoleKey.End && !ctrlOrAlt && Filter.Length == 0)
                return State.HandleKey(new ConsoleKeyInfo(key.KeyChar, key.Key, false, false, control: true));
            return State.HandleKey(key);
        }

        public override bool HandlePaste(string text) => State.HandlePaste(text);

        public override bool HandleMouse(MouseEvent mouse) => State.HandleMouse(mouse);

        public override void Render(CellBuffer buf, Rect area, RenderContext ctx)
        {
            ArgumentNullException.ThrowIfNull(ctx);
            if (Theme.Current != _theme) Refilter(); // the background was found to be light after the rows were made
            Widget.FilterList(State)
                .Block(Widget.Block().Title(" Tasks "))
                .Placeholder("/ to filter")
                .Empty(_screen._tasks.Count == 0 ? "No tasks yet. Ctrl+N adds one." : "No match")
                .Focused(ctx.Focused)
                .Draw(buf, area);
        }
    }
}

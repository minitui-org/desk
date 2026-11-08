using System.Globalization;

namespace MiniTui.Desk.Notes;

/// <summary>
/// The list of notes and the selected one in an editor (spec §13.8). Every change is saved 1 second after the last
/// one, and before anything else happens to the note.
/// </summary>
internal sealed class NotesScreen : Screen
{
    internal static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);
    private const int NarrowWidth = 50;

    private readonly NotesStore _store;
    private readonly TimeProvider _time;
    private readonly Action<string> _setTitle;
    private readonly NoteList _list;
    private readonly NoteEditor _editor;
    private ITimer? _saveTimer;
    private App? _app; // kept after unmounting, so a last save can still say what went wrong
    private List<NoteFile> _notes = [];
    private string? _title;
    private (string Name, string Text)? _append;

    /// <param name="append">A note and text for <c>--append</c> (spec §13.8): added at its end as Notes starts.</param>
    public NotesScreen(NotesStore store, TimeProvider? time = null, Action<string>? setTitle = null, (string Name, string Text)? append = null)
    {
        _store = store;
        _append = append;
        _time = time ?? TimeProvider.System;
        _setTitle = setTitle ?? (_ => { });
        _list = new NoteList(this);
        _editor = new NoteEditor(this);
        AddChild(_list);
        AddChild(_editor);
        Focus.Add(_list).Add(_editor);

        Keys.Add("ctrl+n", "New", New)
            .Add("f2", "Rename", Rename)
            .Add("ctrl+q", "Quit", () => Quit());
    }

    public override string Title => "Notes";

    /// <summary>The open note's name: the one selected in the list. Null with no notes.</summary>
    internal string? Current { get; private set; }

    internal TextAreaState Text { get; private set; } = new();

    /// <summary>What the note's file held when it was last read or written.</summary>
    private string? Saved { get; set; }

    internal bool HasUnsavedChanges => Current is not null && Text.Text != Saved;

    internal bool IsEditing => Focus.IsFocused(_editor);

    internal IReadOnlyList<NoteFile> Notes => _notes;

    internal IEnumerable<string> Showing => _list.Matches.Select(i => _notes[i].Name);

    /// <summary>The note selected in the list, for tests: the open one, unless it's filtered out.</summary>
    internal string? Highlighted => _list.State.Selected is { } row ? _notes[row].Name : null;

    // ---- What the app does with it ----

    /// <summary>Save, and quit if that worked. Otherwise it says why and stays, so the text isn't lost.</summary>
    public bool Quit()
    {
        if (!Save()) return false;
        App.Quit();
        return true;
    }

    /// <summary>Save the open note if it has changed. False, having said why, if it couldn't.</summary>
    public bool Save()
    {
        _saveTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (!HasUnsavedChanges) return true;
        var name = Current!;
        var text = Text.Text;
        try
        {
            if (_store.Read(name) != Saved)
            {
                // Changed on disk as well: theirs stays, and ours goes beside it
                var conflict = _store.FreeName(name, "conflict");
                _store.Write(conflict, text);
                Toast($"Changed elsewhere, so yours is “{conflict}”", ToastLevel.Warning);
                Current = conflict;
            }
            else
            {
                _store.Write(name, text);
            }
            Saved = text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Toast($"Can't save “{name}”: {e.Message}", ToastLevel.Error);
            return false;
        }
        Reload();
        return true;
    }

    /// <summary>Back in front: anything changed on disk since shows, unless there's unsaved text in the way.</summary>
    public void Refresh()
    {
        Reload();
        if (Current is null || HasUnsavedChanges) return;
        if (_store.Read(Current) is { } text)
        {
            if (text != Saved) Open(Current, text);
        }
        else
        {
            Current = null; // deleted elsewhere
            SelectFirst();
        }
    }

    // ---- Lifecycle ----

    protected override void OnMount()
    {
        _app = App;
        // Ctrl+C, a hangup and SIGTERM save first; a Ctrl+C that can't save stays, so the text isn't lost
        App.OnQuitting(_ => Save());
        _saveTimer = _time.CreateTimer(_ => App.Post(() => Save()), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Reload();
        if (_append is { } append)
        {
            _append = null;
            if (AppendTo(append.Name, append.Text)) return;
        }
        SelectFirst();
    }

    /// <summary>
    /// <c>--append</c>: add <paramref name="text"/> to the end of the note, making it if there's none, save, and edit
    /// it at the end. False, having said why, if the name can't be a note's or the note can't be written.
    /// </summary>
    private bool AppendTo(string name, string text)
    {
        if (NotesStore.Problem(name) is { } problem)
        {
            Toast($"Can't add to “{name}”: {problem}", ToastLevel.Error);
            return false;
        }
        name = _notes.Select(n => n.Name).FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) ?? name;
        try
        {
            var was = (_store.Read(name) ?? "").TrimEnd('\n', '\r');
            _store.Write(name, was.Length == 0 ? text : was + "\n\n" + text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Toast($"Can't add to “{name}”: {e.Message}", ToastLevel.Error);
            return false;
        }
        Reload();
        Select(name);
        if (Current != name) return false;
        Text.Cursor = new TextPosition(Text.Lines.Count - 1, Text.Lines[^1].Length);
        Edit();
        return true;
    }

    protected override void OnUnmount()
    {
        _saveTimer?.Dispose();
        _saveTimer = null;
    }

    public override void Render(CellBuffer buf, Rect area, RenderContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (area.Width < NarrowWidth)
        {
            ctx.Render(IsEditing ? _editor : _list, buf, area);
            return;
        }
        var listWidth = Math.Clamp(area.Width / 4, 20, 32);
        ctx.Render(_list, buf, area with { Width = listWidth });
        ctx.Render(_editor, buf, area with { X = area.X + listWidth, Width = area.Width - listWidth });
    }

    // ---- Notes ----

    private void Reload()
    {
        try
        {
            _notes = _store.List();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Toast($"Can't read {_store.Directory}: {e.Message}", ToastLevel.Error);
        }
        _list.Refilter();
    }

    private void SelectFirst()
    {
        if (_list.Matches.FirstOrDefault(-1) is var first and >= 0) Select(_notes[first].Name);
        else if (Current is null) ShowNone();
    }

    /// <summary>Open <paramref name="name"/>, saving the one that's open first. Stays put if that can't be saved.</summary>
    internal void Select(string name)
    {
        if (name == Current) return;
        if (!Save()) return;
        string text;
        try
        {
            text = _store.Read(name) ?? "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Toast($"Can't read “{name}”: {e.Message}", ToastLevel.Error);
            return;
        }
        Open(name, text);
    }

    private void Open(string name, string text)
    {
        var line = name == Current ? Text.Cursor.Line : 0;
        Current = name;
        Saved = text;
        Text = new TextAreaState(text) { TabIndents = true, AutoIndent = true };
        Text.Changed += (_, _) => Edited();
        if (line > 0) Text.Cursor = new TextPosition(Math.Min(line, Text.Lines.Count - 1), 0);
        SetTitle(name);
    }

    private void ShowNone()
    {
        Current = null;
        Saved = null;
        Text = new TextAreaState();
        if (IsEditing) Focus.Focus(_list);
        SetTitle("");
    }

    private void Toast(string text, ToastLevel level = ToastLevel.Info) => _app?.Toasts.Push(text, level);

    private void SetTitle(string title)
    {
        if (title == _title) return;
        _title = title;
        _setTitle(title);
    }

    /// <summary>The editor changed the text: save once it's been quiet a while.</summary>
    private void Edited()
    {
        if (HasUnsavedChanges) _saveTimer?.Change(SaveDelay, Timeout.InfiniteTimeSpan);
    }

    private void Edit()
    {
        if (Current is not null) Focus.Focus(_editor);
    }

    // A name's problem, ignoring spaces at the ends, which are trimmed
    private Func<string, string?> NameProblem(string? except) => text => _store.Problem(text.Trim(), except);

    private async void New()
    {
        if (await App.Overlays.PromptAsync("New note", _list.Filter, NameProblem(null), "Name") is not { } typed) return;
        var name = typed.Trim();
        try
        {
            _store.Create(name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Toast($"Can't make “{name}”: {e.Message}", ToastLevel.Error);
            return;
        }
        _list.ClearFilter();
        Reload();
        Select(name); // saving the open one first
        if (Current == name) Edit();
    }

    private async void Rename()
    {
        if (Current is not { } from) return;
        if (await App.Overlays.PromptAsync("Rename note", from, NameProblem(from), "Name") is not { } typed) return;
        var to = typed.Trim();
        if (to == Current || !Save()) return;
        from = Current!; // a conflict on saving gives it another name
        try
        {
            _store.Rename(from, to);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Toast($"Can't rename “{from}”: {e.Message}", ToastLevel.Error);
            return;
        }
        Current = to;
        SetTitle(to);
        Reload();
    }

    private async void Delete()
    {
        if (Current is not { } name) return;
        if (!await App.Overlays.ConfirmAsync($"Delete “{name}”?", "Delete", "Cancel", yesFirst: false)) return;
        if (Current != name) return;
        try
        {
            _store.Delete(name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Toast($"Can't delete “{name}”: {e.Message}", ToastLevel.Error);
            return;
        }
        var index = _list.Matches.ToList().FindIndex(i => _notes[i].Name == name);
        ShowNone();
        Reload();
        var matches = _list.Matches.ToList();
        if (matches.Count > 0) Select(_notes[matches[Math.Clamp(index, 0, matches.Count - 1)]].Name);
    }

    private string When(DateTimeOffset changed)
    {
        var local = TimeZoneInfo.ConvertTime(changed, _time.LocalTimeZone);
        var now = _time.GetLocalNow();
        if (local.Date == now.Date) return local.ToString("HH:mm", CultureInfo.CurrentCulture);
        return local.Year == now.Year
            ? local.ToString("MMM d", CultureInfo.CurrentCulture)
            : local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>The notes, filtered by typing (spec §13.8): MiniTui's filter list, whose selection is the open note.</summary>
    private sealed class NoteList : Component
    {
        private readonly NotesScreen _screen;
        private bool _syncing; // the screen is moving the selection, not the user

        public NoteList(NotesScreen screen)
        {
            _screen = screen;
            State.SelectionChanged += (_, _) =>
            {
                if (!_syncing) Follow();
            };
            State.Activated += (_, _) => _screen.Edit();
        }

        public FilterListState State { get; } = new();

        public override string KeysGroup => "List";
        public override bool CapturesText => true;

        public string Filter => State.Filter.Text;

        public IReadOnlyList<int> Matches => State.Matches;

        protected override void OnMount()
        {
            Keys.Add("enter,tab", "Edit", _screen.Edit)
                .Add("delete", "Delete", _screen.Delete);
        }

        public void ClearFilter() => Quietly(State.Filter.Clear);

        /// <summary>The notes changed: show them, with the open one selected.</summary>
        public void Refilter() => Quietly(() =>
            State.SetRows(_screen._notes.Select(n => new FilterRow(n.Name, Hint: _screen.When(n.Changed), Key: n.Name))));

        public override bool HandleKey(ConsoleKeyInfo key)
        {
            var ctrlOrAlt = (key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) != 0;
            switch (key.Key)
            {
                // Enter, Tab and Delete are the bindings'
                case ConsoleKey.Enter or ConsoleKey.Delete when !ctrlOrAlt:
                case ConsoleKey.Tab when !ctrlOrAlt:
                    return false;
                // With nothing typed, Home and End go to the first and last note
                case ConsoleKey.Home or ConsoleKey.End when !ctrlOrAlt && Filter.Length == 0:
                    return State.HandleKey(new ConsoleKeyInfo(key.KeyChar, key.Key, false, false, control: true));
            }
            return State.HandleKey(key);
        }

        public override bool HandlePaste(string text) => State.HandlePaste(text);

        public override bool HandleMouse(MouseEvent mouse) => State.HandleMouse(mouse);

        // The user moved the selection, or typed: open that note (the best match, as it's typed)
        private void Follow()
        {
            if (State.Selected is { } row) _screen.Select(_screen._notes[row].Name);
            Quietly(() => { }); // back to the open one, if it couldn't be left
        }

        private void Quietly(Action change)
        {
            _syncing = true;
            try
            {
                change();
                if (_screen.Current is { } current && _screen._notes.FindIndex(n => n.Name == current) is >= 0 and var index)
                    State.Select(index);
            }
            finally
            {
                _syncing = false;
            }
        }

        public override void Render(CellBuffer buf, Rect area, RenderContext ctx)
        {
            ArgumentNullException.ThrowIfNull(ctx);
            Widget.FilterList(State)
                .Block(Widget.Block().Title(" Notes "))
                .Placeholder("Type to filter")
                .Empty(_screen._notes.Count == 0 ? "No notes yet" : "No match")
                .Focused(ctx.Focused)
                .Draw(buf, area);
        }
    }

    /// <summary>The open note, in MiniTui's editor.</summary>
    private sealed class NoteEditor(NotesScreen screen) : Component
    {
        public override string KeysGroup => "Editor";
        public override bool CapturesText => true;

        protected override void OnMount()
        {
            Keys.Add("esc", "Notes", () => screen.Focus.Focus(screen._list))
                // The text area takes Tab and Shift+Tab, so these only list them in help
                .Add("tab", "Indent", () => { }, showInHints: false)
                .Add("shift+tab", "Outdent", () => { }, showInHints: false);
        }

        public override bool HandleKey(ConsoleKeyInfo key) => screen.Current is not null && screen.Text.HandleKey(key);

        public override bool HandlePaste(string text) => screen.Current is not null && screen.Text.Paste(text);

        public override bool HandleMouse(MouseEvent mouse) => screen.Current is not null && screen.Text.HandleMouse(mouse);

        public override void Render(CellBuffer buf, Rect area, RenderContext ctx)
        {
            ArgumentNullException.ThrowIfNull(ctx);
            if (screen.Current is not { } name)
            {
                var inner = Widget.Block().Draw(buf, area);
                buf.SetString(inner.X + 1, inner.Y, "No notes yet. Ctrl+N makes one.", ctx.Theme.TextMuted, inner.Width - 1);
                return;
            }
            Widget.TextArea()
                .State(screen.Text)
                .Focused(ctx.Focused)
                .Wrap(WrapMode.Word)
                .Block(Widget.Block().Title($" {name} ").Focused(ctx.Focused))
                .Draw(buf, area);
        }
    }
}

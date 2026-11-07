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

    public NotesScreen(NotesStore store, TimeProvider? time = null, Action<string>? setTitle = null)
    {
        _store = store;
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
        _saveTimer = _time.CreateTimer(_ => App.Post(() => Save()), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Reload();
        SelectFirst();
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
        Text = new TextAreaState(text) { AutoIndent = true };
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

    // MiniTui quits on Ctrl+C without the key maps unless the focused component takes it (README, gaps)
    private static bool IsCtrlC(ConsoleKeyInfo key) =>
        key.KeyChar == '\x03' || (key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0);

    private bool QuitByCtrlC()
    {
        Quit();
        return true;
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

    private void New()
    {
        App.Overlays.Show(new NamePrompt("New note", _list.Filter, name => _store.Problem(name, null), name =>
        {
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
        }));
    }

    private void Rename()
    {
        if (Current is not { } from) return;
        App.Overlays.Show(new NamePrompt("Rename note", from, name => _store.Problem(name, except: from), to =>
        {
            if (to == from || !Save()) return;
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
        }));
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

    /// <summary>The notes, filtered by typing (spec §13.8).</summary>
    private sealed class NoteList(NotesScreen screen) : Component
    {
        private readonly TextFieldState _filter = new();
        private List<int> _matches = [];
        private int _offset;
        private int _rows = 1;

        public override string KeysGroup => "List";
        public override bool CapturesText => true;

        public string Filter => _filter.Text;

        public IReadOnlyList<int> Matches => _matches;

        protected override void OnMount()
        {
            Keys.Add("enter,tab", "Edit", screen.Edit)
                .Add("delete", "Delete", screen.Delete);
        }

        public void ClearFilter()
        {
            _filter.Clear();
            Refilter();
        }

        public void Refilter()
        {
            var pattern = _filter.Text.Trim();
            _matches = pattern.Length == 0
                ? [.. Enumerable.Range(0, screen._notes.Count)]
                : [.. screen._notes
                    .Select((note, i) => (i, match: Fuzzy.Match(pattern, note.Name)))
                    .Where(m => m.match is not null)
                    .OrderByDescending(m => m.match!.Value.Score)
                    .Select(m => m.i)];
        }

        private int Selected => _matches.FindIndex(i => screen._notes[i].Name == screen.Current);

        public override bool HandleKey(ConsoleKeyInfo key)
        {
            if (IsCtrlC(key)) return screen.QuitByCtrlC();
            var ctrlOrAlt = (key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) != 0;
            switch (key.Key)
            {
                case ConsoleKey.UpArrow: return Move(Selected - 1);
                case ConsoleKey.DownArrow: return Move(Selected + 1);
                case ConsoleKey.PageUp: return Move(Selected - _rows);
                case ConsoleKey.PageDown: return Move(Selected + _rows);
                case ConsoleKey.Home when ctrlOrAlt || _filter.Text.Length == 0: return Move(0);
                case ConsoleKey.End when ctrlOrAlt || _filter.Text.Length == 0: return Move(_matches.Count - 1);
                case ConsoleKey.Escape when _filter.Text.Length > 0:
                    ClearFilter();
                    return true;
                case ConsoleKey.Enter or ConsoleKey.Delete or ConsoleKey.Escape:
                case ConsoleKey.Tab when !ctrlOrAlt:
                    return false;
            }
            if (ctrlOrAlt && key.Key is not (ConsoleKey.Backspace or ConsoleKey.LeftArrow or ConsoleKey.RightArrow)) return false;

            var before = _filter.Text;
            if (!_filter.HandleKey(key)) return false;
            if (_filter.Text != before) Filtered();
            return true;
        }

        public override bool HandlePaste(string text)
        {
            if (!_filter.Paste(text)) return false;
            Filtered();
            return true;
        }

        // The best match shows as it's typed
        private void Filtered()
        {
            Refilter();
            if (_matches.Count > 0 && Selected < 0) screen.Select(screen._notes[_matches[0]].Name);
        }

        private bool Move(int index)
        {
            if (_matches.Count == 0) return true;
            screen.Select(screen._notes[_matches[Math.Clamp(index, 0, _matches.Count - 1)]].Name);
            return true;
        }

        public override void Render(CellBuffer buf, Rect area, RenderContext ctx)
        {
            ArgumentNullException.ThrowIfNull(ctx);
            var focused = ctx.Focused;
            var inner = Widget.Block().Title(" Notes ").Focused(focused).Draw(buf, area);
            if (inner.Height <= 0 || inner.Width <= 0) return;

            Widget.TextField().State(_filter).Placeholder("Type to filter").Focused(focused)
                .Draw(buf, inner with { Height = 1 });
            var rows = inner with { Y = inner.Y + 1, Height = inner.Height - 1 };
            _rows = Math.Max(1, rows.Height);
            if (rows.Height <= 0) return;

            if (_matches.Count == 0)
            {
                var empty = screen._notes.Count == 0 ? "No notes yet" : "No match";
                buf.SetString(rows.X + 1, rows.Y, empty, ctx.Theme.TextMuted, rows.Width - 1);
                return;
            }

            var selected = Selected;
            if (selected >= 0)
            {
                if (selected < _offset) _offset = selected;
                if (selected >= _offset + rows.Height) _offset = selected - rows.Height + 1;
            }
            _offset = Math.Clamp(_offset, 0, Math.Max(0, _matches.Count - rows.Height));

            for (var row = 0; row < rows.Height && _offset + row < _matches.Count; row++)
            {
                var index = _offset + row;
                var note = screen._notes[_matches[index]];
                var y = rows.Y + row;
                var style = index == selected ? (focused ? ctx.Theme.Selection : ctx.Theme.SelectionUnfocused) : ctx.Theme.Text;
                buf.SetString(rows.X, y, new string(' ', rows.Width), style, rows.Width);
                var when = screen.When(note.Changed);
                var whenWidth = UnicodeWidth.Of(when);
                var nameWidth = Math.Max(0, rows.Width - whenWidth - 3);
                buf.SetString(rows.X + 1, y, UnicodeWidth.Truncate(note.Name, nameWidth), style, nameWidth);
                if (rows.Width > whenWidth + 4)
                    buf.SetString(rows.Right - whenWidth - 1, y, when, index == selected ? style : ctx.Theme.TextMuted, whenWidth);
            }
        }
    }

    /// <summary>The open note, in MiniTui's editor.</summary>
    private sealed class NoteEditor(NotesScreen screen) : Component
    {
        public override string KeysGroup => "Editor";
        public override bool CapturesText => true;

        protected override void OnMount()
        {
            Keys.Add("esc", "Notes", () => screen.Focus.Focus(screen._list));
        }

        public override bool HandleKey(ConsoleKeyInfo key)
        {
            if (screen.Current is null) return IsCtrlC(key) && screen.QuitByCtrlC();
            if (!screen.Text.HandleKey(key)) return IsCtrlC(key) && screen.QuitByCtrlC(); // it copies a selection
            screen.Edited();
            return true;
        }

        public override bool HandlePaste(string text)
        {
            if (screen.Current is null || !screen.Text.Paste(text)) return false;
            screen.Edited();
            return true;
        }

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

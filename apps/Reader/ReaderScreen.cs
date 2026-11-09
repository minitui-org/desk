namespace MiniTui.Desk.Reader;

/// <summary>
/// The files of a folder and the selected one formatted (spec §13.10). One block of the page is highlighted, and
/// that's the passage <c>Ctrl+N</c> sends to Notes.
/// </summary>
internal sealed class ReaderScreen : Screen
{
    /// <summary>A passage over this isn't sent.</summary>
    public const int MaxPassageBytes = 16 * 1024;

    private const int NarrowWidth = 50;

    private readonly Places _places;
    private readonly Action<string> _setTitle;
    private readonly Func<string, string, string?> _sendToNotes;
    private readonly FileList _list;
    private readonly PageView _page;
    private App? _app;
    private string? _title;
    private string? _startFile;
    private string? _startProblem;

    /// <param name="start">A folder to list, or a file to open in its folder.</param>
    /// <param name="sendToNotes">
    /// Sends a note's name and the text to add to it, and says how that went, or returns null to say nothing.
    /// </param>
    public ReaderScreen(Places places, string start, Action<string>? setTitle = null, Func<string, string, string?>? sendToNotes = null)
    {
        _places = places;
        _setTitle = setTitle ?? (_ => { });
        _sendToNotes = sendToNotes ?? ((_, _) => null);
        _list = new FileList(this);
        _page = new PageView(this);
        AddChild(_list);
        AddChild(_page);
        Focus.Add(_list).Add(_page);
        Keys.Add("ctrl+q", "Quit", Quit);

        var full = Path.GetFullPath(start);
        if (Directory.Exists(full))
        {
            Folder = full;
        }
        else if (File.Exists(full))
        {
            Folder = Path.GetDirectoryName(full) ?? "/";
            _startFile = full;
        }
        else
        {
            Folder = Directory.GetCurrentDirectory();
            _startProblem = $"There's no “{start}”";
        }
    }

    public override string Title => "Reader";

    internal string Folder { get; private set; }

    internal IReadOnlyList<Entry> Entries { get; private set; } = [];

    /// <summary>The open file, or null.</summary>
    internal string? Open { get; private set; }

    /// <summary>Why the open file isn't shown, or null.</summary>
    internal string? Problem { get; private set; }

    internal IReadOnlyList<MarkdownBlock> Blocks => _page.Document.Blocks;

    internal bool IsReading => Focus.IsFocused(_page);

    /// <summary>The highlighted blocks, first to last, or null with nothing to highlight.</summary>
    internal (int First, int Last)? Highlight => _page.Highlight;

    internal ScrollState Scroll => _page.Scroll;

    internal IEnumerable<string> Showing => _list.State.Matches.Select(i => Entries[i].Name);

    internal MarkdownState Document => _page.Document;

    // ---- What the app does with it ----

    public void Quit()
    {
        Remember();
        App.Quit();
    }

    /// <summary>Back in front: the folder and the open file again, in case they changed.</summary>
    public void Refresh()
    {
        List(Folder, keep: Open);
        if (Open is { } open && File.Exists(open)) Read(open, _page.Highlight?.First ?? 0);
    }

    // ---- Lifecycle ----

    protected override void OnMount()
    {
        _app = App;
        App.OnQuitting(_ =>
        {
            Remember();
            return true;
        });
        List(Folder, keep: _startFile);
        if (_startFile is { } file)
        {
            _startFile = null;
            ReadAt(file);
            Focus.Focus(_page);
        }
        else
        {
            SetTitle(Files.Pretty(Folder));
        }
        if (_startProblem is { } problem)
        {
            _startProblem = null;
            Toast(problem, ToastLevel.Warning);
        }
    }

    public override void Render(CellBuffer buf, Rect area, RenderContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (area.Width < NarrowWidth)
        {
            ctx.Render(IsReading ? _page : _list, buf, area);
            return;
        }
        var listWidth = Math.Clamp(area.Width / 4, 20, 32);
        ctx.Render(_list, buf, area with { Width = listWidth });
        ctx.Render(_page, buf, area with { X = area.X + listWidth, Width = area.Width - listWidth });
    }

    // ---- Folders ----

    /// <summary>List <paramref name="folder"/>, selecting <paramref name="keep"/> if it's there, or the first row.</summary>
    private void List(string folder, string? keep)
    {
        try
        {
            Entries = Files.List(folder);
            Folder = folder;
            _list.Problem = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (folder != Folder) // stay where we are
            {
                Toast($"Can't open {Files.Pretty(folder)}: {e.Message}", ToastLevel.Error);
                return;
            }
            Entries = [];
            _list.Problem = e.Message;
        }
        _list.Show(keep);
    }

    /// <summary>Enter or → on a row: into a folder, or read the file.</summary>
    internal void Enter()
    {
        if (_list.Current is not { } entry) return;
        if (entry.IsFolder)
        {
            var from = entry.Name == ".." ? Folder : null;
            _list.ClearFilter();
            List(entry.Path, keep: from);
            if (Open is null) SetTitle(Files.Pretty(Folder));
        }
        else
        {
            if (Open != entry.Path) ReadAt(entry.Path);
            Focus.Focus(_page);
        }
    }

    /// <summary>← or Backspace with nothing typed: up to the folder above, with this one selected.</summary>
    internal void Up()
    {
        if (Path.GetDirectoryName(Folder) is not { } parent) return;
        var from = Folder;
        _list.ClearFilter();
        List(parent, keep: from);
        if (Open is null) SetTitle(Files.Pretty(Folder));
    }

    /// <summary>The selection moved to a file: show it.</summary>
    internal void Selected(Entry entry)
    {
        if (!entry.IsFolder && entry.Path != Open) ReadAt(entry.Path);
    }

    // ---- Files ----

    private void ReadAt(string path)
    {
        Remember();
        Read(path, _places.Of(path));
    }

    private void Read(string path, int block)
    {
        List<string>? lines;
        string? problem;
        try
        {
            (lines, problem) = Files.Read(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            (lines, problem) = (null, $"Can't read this file: {e.Message}");
        }
        Open = path;
        Problem = problem;
        _page.Show(lines is null ? "" : string.Join('\n', lines), plainText: !Files.IsMarkdown(path), block);
        SetTitle(Path.GetFileName(path));
    }

    /// <summary>Where the open file was left, for next time.</summary>
    private void Remember()
    {
        if (Open is { } open && Problem is null && _page.Highlight is { } highlight) _places.Set(open, highlight.First);
    }

    // ---- Passages ----

    /// <summary>The passage: a mouse selection as shown, or the highlighted blocks as written in the file.</summary>
    internal string? Passage()
    {
        if (_page.Scroll.HasSelection) return _page.Scroll.SelectedText;
        return _page.Highlight is null ? null : _page.Document.HighlightedSource;
    }

    internal void SendToNotes()
    {
        if (Open is not { } open || Passage() is not { } passage) return;
        var text = Passages.Quote(passage, Files.Pretty(open));
        if (System.Text.Encoding.UTF8.GetByteCount(text) > MaxPassageBytes)
        {
            Toast("That passage is over 16 KiB, too long to send", ToastLevel.Warning);
            return;
        }
        if (_sendToNotes(Passages.NoteName(open), text) is { } said) Toast(said);
    }

    private void Toast(string text, ToastLevel level = ToastLevel.Info) => _app?.Toasts.Push(text, level);

    private void SetTitle(string title)
    {
        if (title == _title) return;
        _title = title;
        _setTitle(title);
    }

    /// <summary>The folder's files, filtered by typing: MiniTui's filter list.</summary>
    private sealed class FileList : Component
    {
        private readonly ReaderScreen _screen;
        private bool _syncing;

        public FileList(ReaderScreen screen)
        {
            _screen = screen;
            State.SelectionChanged += (_, _) =>
            {
                if (!_syncing && Current is { } entry) _screen.Selected(entry);
            };
            State.Activated += (_, _) => _screen.Enter();
        }

        // The list has the keys, j and k among them, until / asks for the filter
        public FilterListState State { get; } = new() { TypeToFilter = false };

        public string? Problem { get; set; }

        public override string KeysGroup => "List";
        public override bool CapturesText => State.Filtering;

        public Entry? Current => State.Selected is { } row ? _screen.Entries[row] : null;

        protected override void OnMount()
        {
            Keys.Add("enter,right,l", "Open", _screen.Enter)
                .Add("/", "Filter", () => State.Filtering = true)
                .Add("left,h,backspace", "Up", _screen.Up, showInHints: false);
        }

        public void ClearFilter() => Quietly(State.Filter.Clear);

        /// <summary>The entries changed: show them, with <paramref name="keep"/> selected, or the first.</summary>
        public void Show(string? keep) => Quietly(() =>
        {
            State.SetRows(_screen.Entries.Select(e => new FilterRow(e.IsFolder && e.Name != ".." ? e.Name + "/" : e.Name, Key: e.Path)));
            var index = _screen.Entries.ToList().FindIndex(e => e.Path == keep);
            if (index < 0) index = _screen.Entries.ToList().FindIndex(e => e.Path == _screen.Open);
            if (index >= 0) State.Select(index);
        });

        public override bool HandleKey(ConsoleKeyInfo key)
        {
            // The list's keys: what it doesn't use (Enter, Tab, ← →, Backspace, letters) is the bindings'
            if (!State.Filtering) return State.HandleKey(key);

            var ctrlOrAlt = (key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) != 0;
            var empty = State.Filter.Text.Length == 0;
            switch (key.Key)
            {
                // Tab leaves the filter for the page, keeping what was typed
                case ConsoleKey.Tab:
                    State.Filtering = false;
                    return false;
                // With nothing typed the arrows and Backspace go in and out; with a filter they edit it
                case ConsoleKey.RightArrow or ConsoleKey.LeftArrow when !ctrlOrAlt && key.Modifiers == 0 && empty:
                case ConsoleKey.Backspace when !ctrlOrAlt && empty:
                    return false;
                case ConsoleKey.Home or ConsoleKey.End when !ctrlOrAlt && empty:
                    return State.HandleKey(new ConsoleKeyInfo(key.KeyChar, key.Key, false, false, control: true));
            }
            return State.HandleKey(key);
        }

        public override bool HandlePaste(string text) => State.HandlePaste(text);

        public override bool HandleMouse(MouseEvent mouse) => State.HandleMouse(mouse);

        private void Quietly(Action change)
        {
            _syncing = true;
            try
            {
                change();
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
                .Block(Widget.Block().Title($" {Files.Pretty(_screen.Folder)} "))
                .Placeholder("/ to filter")
                .Empty(Problem is { } problem ? $"Can't read this folder: {problem}" : _screen.Entries.Count == 0 ? "Nothing to read here" : "No match")
                .Focused(ctx.Focused)
                .Draw(buf, area);
        }
    }

    /// <summary>The open file, formatted, with the highlighted blocks marked by a bar: MiniTui's Markdown view.</summary>
    private sealed class PageView : Component
    {
        private readonly ReaderScreen _screen;

        public PageView(ReaderScreen screen)
        {
            _screen = screen;
            Document.Scroll.Selectable = true;
        }

        public MarkdownState Document { get; } = new() { BlockCursor = true };

        public ScrollState Scroll => Document.Scroll;

        public (int First, int Last)? Highlight => Document.Highlight;

        public override string KeysGroup => "Page";

        // A block is always highlighted, so Ctrl+C copies rather than quits
        public override bool HasSelection => Document.HasSelection;

        protected override void OnMount()
        {
            // The keys are the Markdown view's; they're bound here to be listed in help
            Keys.Add("up", "Previous block", () => Send(ConsoleKey.UpArrow), showInHints: false)
                .Add("down", "Next block", () => Send(ConsoleKey.DownArrow), showInHints: false)
                .Add("shift+up", "Stretch up", () => Send(ConsoleKey.UpArrow, shift: true), showInHints: false)
                .Add("shift+down", "Stretch down", () => Send(ConsoleKey.DownArrow, shift: true), showInHints: false)
                .Add("pgup", "Page up", () => Send(ConsoleKey.PageUp), showInHints: false)
                .Add("pgdn", "Page down", () => Send(ConsoleKey.PageDown), showInHints: false)
                .Add("home", "Top", () => Send(ConsoleKey.Home), showInHints: false)
                .Add("end", "Bottom", () => Send(ConsoleKey.End), showInHints: false)
                .Add("f7", "Caret", () => Send(ConsoleKey.F7), showInHints: false)
                .Add("ctrl+n", "To Notes", _screen.SendToNotes)
                .Add("ctrl+c", "Copy", () => Send(ConsoleKey.C, control: true), showInHints: false)
                .Add("esc", "List", Back)
                .Add("h", "List", () => Sideways(ConsoleKey.LeftArrow), showInHints: false)
                .Add("l", "Caret right", () => Sideways(ConsoleKey.RightArrow), showInHints: false);
        }

        /// <summary>A file was read: show it, highlighting <paramref name="block"/>.</summary>
        public void Show(string source, bool plainText, int block)
        {
            Document.PlainText = plainText;
            Document.Source = source;
            Document.SetHighlight(block);
        }

        public override bool HandleKey(ConsoleKeyInfo key) => Document.HandleKey(key);

        public override bool HandleMouse(MouseEvent mouse) => Showing && Document.HandleMouse(mouse);

        // Esc clears a selection, then hides the caret, then goes back to the list
        private void Back()
        {
            if (!Send(ConsoleKey.Escape)) _screen.Focus.Focus(_screen._list);
        }

        // h and l move the caret while it shows; without it h goes back to the list, as ← does from the list's side
        private void Sideways(ConsoleKey arrow)
        {
            if (Document.Scroll.CaretVisible) Send(arrow);
            else if (arrow == ConsoleKey.LeftArrow) _screen.Focus.Focus(_screen._list);
        }

        private bool Send(ConsoleKey key, bool shift = false, bool control = false) =>
            Document.HandleKey(new ConsoleKeyInfo(key == ConsoleKey.C && control ? '\x03' : key == ConsoleKey.Escape ? '\x1b' : '\0', key, shift, false, control));

        private bool Showing => _screen.Open is not null && _screen.Problem is null && Document.Blocks.Count > 0;

        public override void Render(CellBuffer buf, Rect area, RenderContext ctx)
        {
            ArgumentNullException.ThrowIfNull(ctx);
            var title = _screen.Open is { } open ? $" {Path.GetFileName(open)} " : " Reader ";
            var inner = Widget.Block().Title(title).Focused(ctx.Focused).Draw(buf, area);
            if (inner.IsEmpty) return;
            if (!Showing)
            {
                var message = _screen.Open is null
                    ? _screen.Entries.All(e => e.Name == "..") ? "There's nothing to read in this folder." : "Pick a .md or .txt file on the left. Enter reads it."
                    : _screen.Problem ?? "This file is empty.";
                Widget.Paragraph().Text(new Text([new Line(message, ctx.Theme.TextMuted)])).Wrap(WrapMode.Word)
                    .Draw(buf, inner with { X = inner.X + 1, Width = Math.Max(0, inner.Width - 1) });
                return;
            }
            Widget.Markdown(Document).Focused(ctx.Focused).Draw(buf, inner);
        }
    }
}

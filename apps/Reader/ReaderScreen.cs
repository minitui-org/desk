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

    internal IReadOnlyList<Block> Blocks { get; private set; } = [];

    internal bool IsReading => Focus.IsFocused(_page);

    /// <summary>The highlighted blocks, first to last, or null with nothing to highlight.</summary>
    internal (int First, int Last)? Highlight => _page.Highlight;

    internal ScrollState Scroll => _page.Scroll;

    internal IEnumerable<string> Showing => _list.State.Matches.Select(i => Entries[i].Name);

    internal Page? LaidOut => _page.Laid;

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
        Blocks = lines is null ? [] : Files.IsMarkdown(path) ? Markdown.Parse(lines) : Markdown.PlainText(lines);
        _page.Show(block);
        SetTitle(Path.GetFileName(path));
    }

    /// <summary>Where the open file was left, for next time.</summary>
    private void Remember()
    {
        if (Open is { } open && Problem is null && _page.Highlight is { } highlight) _places.Set(open, highlight.First);
    }

    // ---- Passages ----

    /// <summary>The passage: a mouse selection as shown, or the highlighted blocks as written in the file.</summary>
    internal string? Passage(bool asWritten)
    {
        if (_page.Scroll.HasSelection) return _page.Scroll.SelectedText;
        if (_page.Highlight is not { } h || Open is null) return null;
        if (!asWritten) return _page.Laid?.Shown(h.First, h.Last);
        string[] lines;
        try
        {
            lines = Files.Read(Open).Lines?.ToArray() ?? [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return _page.Laid?.Shown(h.First, h.Last);
        }
        var from = Blocks[h.First].FirstLine;
        var to = Math.Min(Blocks[h.Last].LastLine, lines.Length - 1);
        return from > to ? null : Passages.Dedent(lines[from..(to + 1)]);
    }

    internal void SendToNotes()
    {
        if (Open is not { } open || Passage(asWritten: true) is not { } passage) return;
        var text = Passages.Quote(passage, Files.Pretty(open));
        if (System.Text.Encoding.UTF8.GetByteCount(text) > MaxPassageBytes)
        {
            Toast("That passage is over 16 KiB, too long to send", ToastLevel.Warning);
            return;
        }
        if (_sendToNotes(Passages.NoteName(open), text) is { } said) Toast(said);
    }

    internal void Copy()
    {
        if (Passage(asWritten: false) is { } passage) Clipboard.SetText(passage);
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

        public FilterListState State { get; } = new();

        public string? Problem { get; set; }

        public override string KeysGroup => "List";
        public override bool CapturesText => true;

        public Entry? Current => State.Selected is { } row ? _screen.Entries[row] : null;

        protected override void OnMount()
        {
            Keys.Add("enter,right", "Open", _screen.Enter)
                .Add("left", "Up", _screen.Up, showInHints: false);
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
            var ctrlOrAlt = (key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) != 0;
            var empty = State.Filter.Text.Length == 0;
            switch (key.Key)
            {
                case ConsoleKey.Enter when !ctrlOrAlt:
                case ConsoleKey.Tab:
                    return false;
                // With nothing typed the arrows go in and out; with a filter they move in it
                case ConsoleKey.RightArrow or ConsoleKey.LeftArrow when !ctrlOrAlt && key.Modifiers == 0 && empty:
                    return false;
                case ConsoleKey.Backspace when !ctrlOrAlt && empty:
                    _screen.Up();
                    return true;
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
                .Placeholder("Type to filter")
                .Empty(Problem is { } problem ? $"Can't read this folder: {problem}" : _screen.Entries.Count == 0 ? "Nothing to read here" : "No match")
                .Focused(ctx.Focused)
                .Draw(buf, area);
        }
    }

    /// <summary>The open file, formatted, with the highlighted blocks marked by a bar.</summary>
    private sealed class PageView : Component
    {
        private const int Gutter = 2;

        private readonly ReaderScreen _screen;
        private int _anchor;
        private int _active;
        private int _laidWidth = -1;
        private Theme? _laidTheme;
        private Rect _text;
        private bool _follow; // scroll to the highlight when next drawn

        public PageView(ReaderScreen screen)
        {
            _screen = screen;
        }

        public ScrollState Scroll { get; } = new() { Selectable = true };

        public Page? Laid { get; private set; }

        public (int First, int Last)? Highlight =>
            _screen.Blocks.Count == 0 ? null : (Math.Min(_anchor, _active), Math.Max(_anchor, _active));

        public override string KeysGroup => "Page";

        // A block is always highlighted, so Ctrl+C copies rather than quits
        public override bool HasSelection => _screen.Blocks.Count > 0 || Scroll.HasSelection;

        protected override void OnMount()
        {
            Keys.Add("up", "Previous block", () => Move(-1, stretch: false), showInHints: false)
                .Add("down", "Next block", () => Move(1, stretch: false), showInHints: false)
                .Add("shift+up", "Stretch up", () => Move(-1, stretch: true), showInHints: false)
                .Add("shift+down", "Stretch down", () => Move(1, stretch: true), showInHints: false)
                .Add("pgup", "Page up", () => Page(-1), showInHints: false)
                .Add("pgdn", "Page down", () => Page(1), showInHints: false)
                .Add("home", "Top", () => End(top: true), showInHints: false)
                .Add("end", "Bottom", () => End(top: false), showInHints: false)
                .Add("ctrl+n", "To Notes", _screen.SendToNotes)
                .Add("ctrl+c", "Copy", _screen.Copy, showInHints: false)
                .Add("esc", "List", Back);
        }

        /// <summary>A file was read: lay it out again, highlighting <paramref name="block"/>.</summary>
        public void Show(int block)
        {
            Scroll.ClearSelection();
            _laidWidth = -1;
            _anchor = _active = Math.Clamp(block, 0, Math.Max(0, _screen.Blocks.Count - 1));
            Scroll.Offset = 0;
            _follow = true;
        }

        public override bool HandleKey(ConsoleKeyInfo key)
        {
            if (key.Key == ConsoleKey.C && key.Modifiers == ConsoleModifiers.Control)
            {
                _screen.Copy();
                return true;
            }
            return false;
        }

        private void Back()
        {
            if (Scroll.HasSelection) Scroll.ClearSelection();
            else _screen.Focus.Focus(_screen._list);
        }

        private void Move(int by, bool stretch)
        {
            if (_screen.Blocks.Count == 0) return;
            Scroll.ClearSelection();
            _active = Math.Clamp(_active + by, 0, _screen.Blocks.Count - 1);
            if (!stretch) _anchor = _active;
            _follow = true;
        }

        private void Page(int by)
        {
            if (Laid is null || _screen.Blocks.Count == 0) return;
            Scroll.ClearSelection();
            Scroll.ScrollBy(by * Math.Max(1, Scroll.ViewportHeight - 1));
            _anchor = _active = FirstInView();
        }

        private void End(bool top)
        {
            if (_screen.Blocks.Count == 0) return;
            Scroll.ClearSelection();
            _anchor = _active = top ? 0 : _screen.Blocks.Count - 1;
            Scroll.Offset = top ? 0 : int.MaxValue;
            _follow = true;
        }

        public override bool HandleMouse(MouseEvent mouse)
        {
            ArgumentNullException.ThrowIfNull(mouse);
            if (_screen.Blocks.Count == 0 || Laid is null) return false;
            // A click on a block highlights it; the press also starts a text selection
            if (mouse.IsClick && _text.Contains(mouse.X, mouse.Y) && Scroll.Offset + mouse.Y - _text.Y is var row
                && row < Laid.Rows.Count && Laid.RowBlock[row] is >= 0 and var block)
            {
                _anchor = _active = block;
            }
            var handled = Scroll.HandleMouse(mouse);
            if (handled) KeepInView();
            return handled;
        }

        // The wheel or the scrollbar moved the page: a highlight out of view moves to the nearest block in view
        private void KeepInView()
        {
            if (Laid is null || Highlight is not { } h) return;
            var (first, last) = (Laid.BlockRows[h.First].First, Laid.BlockRows[h.Last].Last);
            var top = Scroll.Offset;
            var bottom = top + Scroll.ViewportHeight - 1;
            if (last < top) _anchor = _active = FirstInView();
            else if (first > bottom) _anchor = _active = LastInView();
        }

        private int FirstInView()
        {
            var top = Scroll.Offset;
            for (var b = 0; b < Laid!.BlockCount; b++)
                if (Laid.BlockRows[b].First >= top) return b;
            return Laid.BlockCount - 1;
        }

        private int LastInView()
        {
            var bottom = Scroll.Offset + Scroll.ViewportHeight - 1;
            for (var b = Laid!.BlockCount - 1; b >= 0; b--)
                if (Laid.BlockRows[b].Last <= bottom) return b;
            return 0;
        }

        public override void Render(CellBuffer buf, Rect area, RenderContext ctx)
        {
            ArgumentNullException.ThrowIfNull(ctx);
            var title = _screen.Open is { } open ? $" {Path.GetFileName(open)} " : " Reader ";
            var inner = Widget.Block().Title(title).Focused(ctx.Focused).Draw(buf, area);
            if (inner.IsEmpty) return;
            if (_screen.Open is null || _screen.Problem is not null || _screen.Blocks.Count == 0)
            {
                var message = _screen.Open is null
                    ? _screen.Entries.All(e => e.Name == "..") ? "There's nothing to read in this folder." : "Pick a .md or .txt file on the left. Enter reads it."
                    : _screen.Problem ?? "This file is empty.";
                Widget.Paragraph().Text(new Text([new Line(message, ctx.Theme.TextMuted)])).Wrap(WrapMode.Word)
                    .Draw(buf, inner with { X = inner.X + 1, Width = Math.Max(0, inner.Width - 1) });
                Laid = null;
                return;
            }

            // The bar's 2 columns, then the text to the pane's edge, less a column for the scrollbar
            var width = inner.Width - Gutter - 1;
            if (width != _laidWidth || ctx.Theme != _laidTheme || Laid is null)
            {
                Laid = MiniTui.Desk.Reader.Page.Layout(_screen.Blocks, width, PageStyles.From(ctx.Theme));
                _laidWidth = width;
                _laidTheme = ctx.Theme;
                _follow = true;
            }
            _text = new Rect(inner.X + Gutter, inner.Y, Math.Min(inner.Width - Gutter, width + 1), inner.Height);
            if (_follow)
            {
                _follow = false;
                Follow(_text.Height);
            }
            Widget.Paragraph().Text(new Text(Laid.Rows.ToList())).State(Scroll).Draw(buf, _text);

            if (Highlight is { } h)
            {
                var bar = PageStyles.From(ctx.Theme).Bar;
                for (var row = Laid.BlockRows[h.First].First; row <= Laid.BlockRows[h.Last].Last; row++)
                {
                    var y = inner.Y + row - Scroll.Offset;
                    if (y >= inner.Y && y < inner.Bottom) buf.SetString(inner.X, y, "▌", bar, 1);
                }
            }
        }

        // Scroll so the active block shows, its top first if it's taller than the page
        private void Follow(int height)
        {
            if (Laid is null) return;
            var (first, last) = Laid.BlockRows[_active];
            if (first < Scroll.Offset || last >= Scroll.Offset + height)
                Scroll.Offset = last >= Scroll.Offset + height && last - first < height ? last - height + 1 : first;
            if (_active == 0) Scroll.Offset = 0;
        }
    }
}

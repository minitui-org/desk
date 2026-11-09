namespace MiniTui.Desk.Reader.Tests;

/// <summary>Reader with the keyboard alone, and the mouse (spec §13.10), on a TestBackend.</summary>
public sealed class ScreenTests : IDisposable
{
    private const string Garden = "# Garden\n\nTomatoes need sun.\n\n- milk\n  - oat\n- eggs\n\nLast words.";

    private readonly TempFolder _folder = new();
    private readonly List<string> _titles = [];
    private readonly List<(string Name, string Text)> _sent = [];
    private readonly Places _places;
    private ReaderScreen? _screen;
    private ComponentHarness? _harness;

    public ScreenTests() => _places = new Places(_folder.Path("data/tuireader"));

    public void Dispose()
    {
        _harness?.Dispose();
        _folder.Dispose();
    }

    private ReaderScreen Reader => _screen!;
    private ComponentHarness Harness => _harness!;
    private string Screen => string.Join('\n', Harness.Lines);
    private string Status => Harness.Lines[^1];
    private string Docs => _folder.Path("docs");

    private string Make(string name, string text = "")
    {
        var path = Path.Combine(Docs, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private void Start(string? start = null, int width = 100, int height = 24, string? said = "Sent to Notes")
    {
        Directory.CreateDirectory(Docs);
        _screen = new ReaderScreen(_places, start ?? Docs, _titles.Add, (name, text) =>
        {
            _sent.Add((name, text));
            return said;
        });
        _harness = new ComponentHarness(_screen, width, height);
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

    // The highlighted rows' text, without the bar
    private string Highlighted => string.Join('\n', Harness.Lines.Where(l => l.Contains("│ ▌", StringComparison.Ordinal))
        .Select(l => l[(l.IndexOf("│ ▌", StringComparison.Ordinal) + 3)..].TrimEnd(' ', '│', '█', '░').Trim()));

    // ---- Starting ----

    [Fact]
    public void It_starts_in_the_folder_with_nothing_open_and_says_how_to_open_a_file()
    {
        Make("garden.md", Garden);
        Make("recipes/soup.md");
        Start();
        Assert.Equal(["..", "recipes", "garden.md"], Reader.Showing);
        Assert.Contains("Pick a .md or .txt file on the left. Enter reads it.", Screen, StringComparison.Ordinal);
        Assert.Contains("┌ /tmp/tuireader-test-", Harness.Lines[0], StringComparison.Ordinal); // the folder, cut to fit
        Assert.Equal([Files.Pretty(Docs)], _titles);
        Assert.False(Reader.IsReading);
    }

    [Fact]
    public void A_file_to_start_opens_it_to_read_with_the_list_in_its_folder()
    {
        var garden = Make("garden.md", Garden);
        Start(garden);
        Assert.True(Reader.IsReading);
        Assert.Equal(garden, Reader.Open);
        Assert.Equal("garden.md", _titles[^1]);
        Assert.Contains("Tomatoes need sun.", Screen, StringComparison.Ordinal);
        Assert.Equal("Garden\n══════", Highlighted);
    }

    [Fact]
    public void A_path_that_is_not_there_starts_in_the_current_folder_and_says_so()
    {
        Start(_folder.Path("nowhere"));
        Assert.Equal(Directory.GetCurrentDirectory(), Reader.Folder);
        Assert.Contains($"There's no “{_folder.Path("nowhere")}”", Screen, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_folder_says_there_is_nothing_to_read()
    {
        Start();
        Assert.Contains("There's nothing to read in this folder.", Screen, StringComparison.Ordinal);
        Assert.Equal([".."], Reader.Showing); // and up is still there
    }

    // ---- The list ----

    [Fact]
    public void Selecting_a_file_shows_it_and_enter_reads_it_and_esc_comes_back()
    {
        Make("a.md", "Alpha text");
        Make("b.txt", "Beta text");
        Start();
        Press("down");
        Assert.Contains("Alpha text", Screen, StringComparison.Ordinal);
        Assert.False(Reader.IsReading);
        Press("down");
        Assert.Contains("Beta text", Screen, StringComparison.Ordinal);
        Press("enter");
        Assert.True(Reader.IsReading);
        Assert.Contains("ctrl+n To Notes", Status, StringComparison.Ordinal);
        Press("esc");
        Assert.False(Reader.IsReading);
        Press("tab");
        Assert.True(Reader.IsReading);
        Press("tab");
        Assert.False(Reader.IsReading);
    }

    [Fact]
    public void J_and_k_move_and_l_and_h_go_into_a_folder_and_a_file_and_back()
    {
        Make("recipes/soup.md", "Soup");
        Start();
        Type("j");
        Type("l");
        Assert.Equal(Path.Combine(Docs, "recipes"), Reader.Folder);
        Type("j");
        Assert.Contains("Soup", Screen, StringComparison.Ordinal);
        Type("l");
        Assert.True(Reader.IsReading);
        Type("h"); // from the page, back to the list
        Assert.False(Reader.IsReading);
        Assert.Equal(Path.Combine(Docs, "recipes"), Reader.Folder);
        Type("k");
        Type("h"); // and from the list, up a folder
        Assert.Equal(Docs, Reader.Folder);
    }

    [Fact]
    public void With_the_caret_showing_h_and_l_move_it()
    {
        Start(Make("garden.md", Garden));
        Press("f7");
        Type("llh"); // two along "Garden" and one back
        Assert.True(Reader.IsReading);
        Press("shift+right");
        Assert.Equal("a", Reader.Passage());
    }

    [Fact]
    public void Enter_and_right_go_into_a_folder_and_left_and_backspace_come_back_to_it()
    {
        Make("recipes/soup.md", "Soup");
        Make("recipes/bread/rye.md");
        Start();
        Press("down");
        Press("enter");
        Assert.Equal(Path.Combine(Docs, "recipes"), Reader.Folder);
        Assert.Equal(["..", "bread", "soup.md"], Reader.Showing);
        Press("down");
        Press("right");
        Assert.Equal(["..", "rye.md"], Reader.Showing);
        Press("left");
        Assert.Equal(Path.Combine(Docs, "recipes"), Reader.Folder);
        Press("backspace");
        Assert.Equal(Docs, Reader.Folder);
        Assert.Equal("recipes", Harness.Lines.Single(l => l.Contains('▶') || l.Contains("recipes/", StringComparison.Ordinal)).Trim('│', ' ', '▶').TrimEnd('/'));
        Press("enter"); // the folder we came from is selected
        Assert.Equal(Path.Combine(Docs, "recipes"), Reader.Folder);
    }

    [Fact]
    public void Slash_then_typing_filters_and_backspace_then_edits_the_filter()
    {
        Make("garden.md");
        Make("grocery.md");
        Make("work.txt");
        Start();
        Type("gr"); // the list's keys, so nothing is filtered
        Assert.Equal(4, Reader.Showing.Count());
        Type("/gr");
        Assert.Equal(["garden.md", "grocery.md"], Reader.Showing.Order());
        Press("backspace");
        Assert.Equal(Docs, Reader.Folder);
        Press("esc");
        Assert.Equal(4, Reader.Showing.Count());
    }

    [Fact]
    public void Clicking_selects_and_double_clicking_goes_in_or_reads()
    {
        Make("a.md", "Alpha text");
        Start();
        var row = Array.FindIndex(Harness.Lines, l => l.Contains("a.md", StringComparison.Ordinal));
        Harness.Click(3, row);
        Harness.RunUntilIdle();
        Assert.Contains("Alpha text", Screen, StringComparison.Ordinal);
        Harness.DoubleClick(3, row);
        Harness.RunUntilIdle();
        Assert.True(Reader.IsReading);
    }

    [Fact]
    public void A_folder_that_can_not_be_read_says_why_and_stays_where_it_is()
    {
        Make("locked/x.md");
        File.SetUnixFileMode(Path.Combine(Docs, "locked"), UnixFileMode.None);
        Start();
        Press("down");
        Press("enter");
        Assert.Equal(Docs, Reader.Folder);
        Assert.Contains("Can't open", Screen, StringComparison.Ordinal);
    }

    // ---- The page ----

    [Fact]
    public void Up_and_down_move_the_highlight_a_block_and_shift_stretches_it()
    {
        Start(Make("garden.md", Garden));
        Press("down");
        Assert.Equal("Tomatoes need sun.", Highlighted);
        Press("down");
        Press("shift+down");
        Assert.Equal("• milk\n• oat", Highlighted); // the nested item is its own block
        Assert.Equal((2, 3), Reader.Highlight);
        Press("shift+up");
        Press("shift+up");
        Assert.Equal((1, 2), Reader.Highlight);
        Press("up");
        Assert.Equal((0, 0), Reader.Highlight);
        Press("up");
        Assert.Equal((0, 0), Reader.Highlight);
        Press("end");
        Assert.Equal("Last words.", Highlighted);
        Press("home");
        Assert.Equal((0, 0), Reader.Highlight);
    }

    [Fact]
    public void The_page_scrolls_to_keep_the_highlight_in_view_and_paging_takes_it_along()
    {
        var text = string.Join("\n\n", Enumerable.Range(1, 40).Select(i => $"Paragraph {i}."));
        Start(Make("long.md", text), height: 12);
        for (var i = 0; i < 12; i++) Press("down");
        Assert.Equal("Paragraph 13.", Highlighted);
        Assert.DoesNotContain("Paragraph 1.", Screen, StringComparison.Ordinal);
        Press("pgdn");
        Assert.Equal(Reader.Scroll.Offset, Reader.Document.RowsOf(Reader.Highlight!.Value.First)!.Value.First);
        Press("pgup");
        Press("pgup");
        Press("pgup");
        Assert.Equal((0, 0), Reader.Highlight);
        Assert.Equal(0, Reader.Scroll.Offset);
    }

    [Fact]
    public void The_wheel_scrolls_and_a_highlight_left_behind_moves_into_view()
    {
        var text = string.Join("\n\n", Enumerable.Range(1, 40).Select(i => $"Paragraph {i}."));
        Start(Make("long.md", text), height: 12);
        Harness.Scroll(60, 5, 10);
        Harness.RunUntilIdle();
        Assert.True(Reader.Scroll.Offset > 0);
        var firstInView = Enumerable.Range(0, Reader.Blocks.Count).First(b => Reader.Document.RowsOf(b)!.Value.First >= Reader.Scroll.Offset);
        Assert.Equal(firstInView, Reader.Highlight!.Value.First);
        Assert.NotEqual("", Highlighted);
    }

    [Fact]
    public void Clicking_a_block_highlights_it()
    {
        Start(Make("garden.md", Garden));
        var row = Array.FindIndex(Harness.Lines, l => l.Contains("Last words.", StringComparison.Ordinal));
        Harness.Click(40, row);
        Harness.RunUntilIdle();
        Assert.Equal("Last words.", Highlighted);
    }

    [Fact]
    public void A_narrow_screen_shows_one_side()
    {
        Start(Make("garden.md", Garden), width: 40);
        Assert.DoesNotContain("/ to filter", Screen, StringComparison.Ordinal);
        Assert.Contains("Tomatoes", Screen, StringComparison.Ordinal);
        Press("esc");
        Assert.Contains("/ to filter", Screen, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wide_screen_wraps_the_text_to_the_whole_pane()
    {
        Start(Make("wide.md", string.Join("\n\n", Enumerable.Repeat(string.Join(' ', Enumerable.Repeat("word", 60)), 20))), width: 160);
        // The text runs past where an 80-column page beside the 32-column list would end
        var lastWord = Harness.Lines.Max(l => l.LastIndexOf("word", StringComparison.Ordinal));
        Assert.InRange(lastWord, 140, 157);
        // The scrollbar is on the pane's right edge, not beside the text in the middle of the pane
        Assert.NotEqual(' ', Harness.Lines[1][157]);
    }

    [Fact]
    public void Files_that_are_not_shown_say_why()
    {
        File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(Docs).FullName, "bin.md"), [1, 0, 2]);
        Make("empty.md");
        Start(Path.Combine(Docs, "bin.md"));
        Assert.Contains("This isn't a text file.", Screen, StringComparison.Ordinal);
        Assert.Null(Reader.Highlight);
        Press("ctrl+n");
        Assert.Empty(_sent);
        Press("esc");
        Press("down");
        Assert.Contains("This file is empty.", Screen, StringComparison.Ordinal);
    }

    // ---- Passages ----

    [Fact]
    public void Ctrl_n_sends_the_highlight_as_written_to_the_note_named_after_the_file()
    {
        var garden = Make("garden.md", Garden);
        Start(garden);
        Press("down");
        Press("down");
        Press("shift+down");
        Press("shift+down");
        Press("ctrl+n");
        var from = Files.Pretty(garden);
        Assert.Equal([("garden", $"> - milk\n>   - oat\n> - eggs\n\n— {from}\n\n")], _sent);
        Assert.Contains("Sent to Notes", Screen, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mouse_selection_is_the_passage_while_there_is_one_and_esc_clears_it()
    {
        Start(Make("garden.md", Garden));
        var row = Array.FindIndex(Harness.Lines, l => l.Contains("Tomatoes need sun.", StringComparison.Ordinal));
        var x = Harness.Lines[row].IndexOf("need", StringComparison.Ordinal);
        Harness.Mouse(new MouseEvent(MouseAction.Press, MouseButton.Left, x, row));
        Harness.Mouse(new MouseEvent(MouseAction.Drag, MouseButton.Left, x + 4, row));
        Harness.Mouse(new MouseEvent(MouseAction.Release, MouseButton.Left, x + 4, row));
        Harness.RunUntilIdle();
        Press("ctrl+n");
        Assert.StartsWith("> need\n", _sent.Single().Text, StringComparison.Ordinal);
        Press("esc");
        Assert.True(Reader.IsReading);
        Press("ctrl+n");
        Assert.StartsWith("> Tomatoes need sun.\n", _sent[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void F7_shows_a_caret_to_select_with_the_keyboard_and_esc_steps_back_out()
    {
        Start(Make("garden.md", Garden));
        Press("down");
        Press("f7");
        for (var i = 0; i < 8; i++) Press("shift+right");
        Press("ctrl+n");
        Assert.StartsWith("> Tomatoes\n", _sent.Single().Text, StringComparison.Ordinal);

        Press("esc");                       // the selection
        Assert.True(Reader.Scroll.CaretVisible);
        Press("esc");                       // the caret
        Assert.False(Reader.Scroll.CaretVisible);
        Assert.True(Reader.IsReading);
        Press("down");                      // the arrows move the highlight again
        Assert.Equal("• milk", Highlighted.Split('\n')[0]);
        Press("esc");                       // back to the list
        Assert.False(Reader.IsReading);
    }

    [Fact]
    public void Ctrl_c_copies_the_passage_as_shown_and_does_not_quit()
    {
        Start(Make("garden.md", Garden));
        Press("down");
        Press("down");
        Press("shift+down");
        Press("ctrl+c");
        Assert.Equal("• milk\n  • oat", Clipboard.GetText());
        Assert.False(Harness.App.Stopping.IsCancellationRequested);
    }

    [Fact]
    public void A_passage_over_16_KiB_is_not_sent()
    {
        Start(Make("big.md", new string('x', ReaderScreen.MaxPassageBytes)));
        Press("ctrl+n");
        Assert.Empty(_sent);
        Assert.Contains("over 16 KiB", Screen, StringComparison.Ordinal);
    }

    [Fact]
    public void A_text_file_sends_its_paragraph()
    {
        Start(Make("notes.txt", "first\nsecond\n\n  third"));
        Press("down");
        Press("ctrl+n");
        Assert.Equal("notes", _sent.Single().Name);
        Assert.StartsWith("> third\n", _sent.Single().Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_is_said_when_the_sender_says_nothing()
    {
        Start(Make("garden.md", Garden), said: null);
        Press("ctrl+n");
        Assert.Single(_sent);
        Assert.DoesNotContain("Sent to Notes", Screen, StringComparison.Ordinal);
    }

    // ---- Places, changes and help ----

    [Fact]
    public void Each_file_opens_where_it_was_left()
    {
        var garden = Make("garden.md", Garden);
        Make("other.md", "Other");
        Start(garden);
        Press("end");
        Press("esc");
        Press("down"); // other.md: leaving garden remembers it
        Assert.Equal(5, _places.Of(garden));
        Press("up");
        Assert.Equal("Last words.", Highlighted);

        Press("enter");
        Press("up");
        Reader.Quit();
        Assert.Equal(4, new Places(_places.Directory).Of(garden));
    }

    [Fact]
    public void Refresh_shows_what_changed_and_keeps_the_highlight_by_number()
    {
        var garden = Make("garden.md", Garden);
        Start(garden);
        Press("down");
        File.WriteAllText(garden, "# Changed\n\nNew second block.\n\nThird.");
        Make("added.md");
        Reader.Refresh();
        Harness.RunUntilIdle();
        Assert.Equal("New second block.", Highlighted);
        Assert.Contains("added.md", Reader.Showing);
    }

    [Fact]
    public void Help_lists_the_page_keys()
    {
        Start(Make("garden.md", Garden));
        Press("?");
        foreach (var key in new[] { "shift+down", "Stretch down", "pgdn", "ctrl+n", "To Notes", "ctrl+c", "Copy" })
            Assert.Contains(key, Screen, StringComparison.Ordinal);
    }

    [Fact]
    public void Ctrl_q_quits_and_remembers_the_place()
    {
        var garden = Make("garden.md", Garden);
        Start(garden);
        Press("down");
        Press("ctrl+q");
        Assert.True(Harness.App.Stopping.IsCancellationRequested);
        Assert.Equal(1, new Places(_places.Directory).Of(garden));
    }
}

using Microsoft.Extensions.Time.Testing;

namespace MiniTui.Desk.Notes.Tests;

/// <summary>Notes with the keyboard alone (spec §13.8), on a TestBackend with a clock the test moves.</summary>
public sealed class ScreenTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
    private readonly NotesStore _store;
    private readonly List<string> _titles = [];
    private NotesScreen? _screen;
    private ComponentHarness? _harness;

    public ScreenTests() => _store = new NotesStore(_folder.Path("tuinotes"));

    public void Dispose()
    {
        _harness?.Dispose();
        _folder.Dispose();
    }

    private NotesScreen Notes => _screen!;
    private ComponentHarness Harness => _harness!;
    private string Screen => string.Join('\n', Harness.Lines);
    private string Status => Harness.Lines[^1];

    private void Start(int width = 100, int height = 20)
    {
        _screen = new NotesScreen(_store, _time, _titles.Add);
        _harness = new ComponentHarness(_screen, width, height, _time);
        Harness.RunUntilIdle();
    }

    private void Seed(string name, string text, int minutesAgo = 0)
    {
        _store.Write(name, text);
        File.SetLastWriteTimeUtc(_store.PathOf(name), DateTime.UtcNow.AddMinutes(-minutesAgo));
    }

    private void Wait(TimeSpan time)
    {
        _time.Advance(time);
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

    private void New(string name)
    {
        Press("ctrl+n");
        Press("ctrl+a"); // a filter becomes the name; replace it
        Type(name);
        Press("enter");
    }

    // ---- With no notes ----

    [Fact]
    public void With_no_notes_it_says_how_to_make_one_and_makes_no_folder()
    {
        Start();
        Assert.Contains("No notes yet. Ctrl+N makes one.", Screen, StringComparison.Ordinal);
        Assert.Contains("ctrl+n New", Status, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_store.Directory));
        Assert.Null(Notes.Current);
    }

    [Fact]
    public void Ctrl_n_makes_a_note_and_edits_it()
    {
        Start();
        New("Shopping");
        Assert.True(Notes.IsEditing);
        Assert.Equal("Shopping", Notes.Current);
        Assert.Equal("", _store.Read("Shopping"));
        Assert.Equal("Shopping", _titles[^1]);

        Type("milk");
        Assert.Contains("┌ Shopping ", Screen, StringComparison.Ordinal);
        Assert.Contains("milk", Screen, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_with_a_problem_is_refused_in_the_prompt()
    {
        Seed("Shopping", "");
        Start();
        New("shopping");
        Assert.Contains("There's already a note with that name", Screen, StringComparison.Ordinal);
        Press("ctrl+a");
        Type(".x");
        Press("enter");
        Assert.Contains("A name can't start with a dot", Screen, StringComparison.Ordinal);
        Press("esc");
        Assert.DoesNotContain("New note", Screen, StringComparison.Ordinal);
        Assert.Equal(["Shopping"], _store.List().Select(n => n.Name));
    }

    // ---- Autosave ----

    [Fact]
    public void A_change_is_saved_a_second_after_the_last_one()
    {
        Seed("Plan", "");
        Start();
        Press("enter");
        Type("one");
        Wait(TimeSpan.FromMilliseconds(600));
        Type(" two");
        Wait(TimeSpan.FromMilliseconds(600));
        Assert.Equal("", _store.Read("Plan")); // still typing
        Wait(TimeSpan.FromMilliseconds(400));
        Assert.Equal("one two", _store.Read("Plan"));
        Assert.False(Notes.HasUnsavedChanges);
    }

    [Fact]
    public void Moving_the_cursor_saves_nothing()
    {
        Seed("Plan", "text");
        Start();
        var written = File.GetLastWriteTimeUtc(_store.PathOf("Plan"));
        Press("enter");
        Press("end");
        Wait(TimeSpan.FromSeconds(2));
        Assert.Equal(written, File.GetLastWriteTimeUtc(_store.PathOf("Plan")));
    }

    [Fact]
    public void Selecting_another_note_saves_first()
    {
        Seed("A", "", minutesAgo: 1);
        Seed("B", "", minutesAgo: 2);
        Start();
        Assert.Equal("A", Notes.Current);
        Press("enter");
        Type("for a");
        Press("esc");
        Press("down");
        Assert.Equal("B", Notes.Current);
        Assert.Equal("for a", _store.Read("A"));
        Assert.Equal("B", _titles[^1]);
    }

    [Fact]
    public void Save_writes_at_once_for_losing_the_focus()
    {
        Seed("Plan", "");
        Start();
        Press("enter");
        Type("x");
        Harness.App.Post(() => Notes.Save());
        Harness.RunUntilIdle();
        Assert.Equal("x", _store.Read("Plan"));
    }

    [Fact]
    public void Ctrl_q_saves_and_quits()
    {
        Seed("Plan", "");
        Start();
        Press("enter");
        Type("last words");
        Press("ctrl+q");
        Assert.Equal("last words", _store.Read("Plan"));
        Assert.True(Harness.App.Stopping.IsCancellationRequested);
    }

    [Fact]
    public void Ctrl_c_quits_the_same_way()
    {
        Seed("Plan", "");
        Start();
        Press("enter");
        Type("kept");
        Press("esc");
        Press("ctrl+c");
        Assert.Equal("kept", _store.Read("Plan"));
        Assert.True(Harness.App.Stopping.IsCancellationRequested);
    }

    [Fact]
    public void Ctrl_c_in_the_editor_copies_a_selection_and_otherwise_quits()
    {
        Seed("Plan", "");
        Start();
        Press("enter");
        Type("kept");
        Press("shift+home");
        Press("ctrl+c");
        Assert.False(Harness.App.Stopping.IsCancellationRequested);
        Press("end");
        Press("ctrl+c");
        Assert.Equal("kept", _store.Read("Plan"));
        Assert.True(Harness.App.Stopping.IsCancellationRequested);
    }

    [Fact]
    public void A_save_that_fails_says_why_keeps_the_text_and_stops_a_quit()
    {
        Seed("Plan", "");
        Start();
        Press("enter");
        Type("precious");
        File.SetUnixFileMode(_store.Directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        Press("ctrl+q");
        Press("ctrl+c");

        Assert.False(Harness.App.Stopping.IsCancellationRequested);
        Assert.Contains("Can't save “Plan”", Screen, StringComparison.Ordinal);
        Assert.True(Notes.HasUnsavedChanges);
        Assert.Contains("precious", Screen, StringComparison.Ordinal);

        File.SetUnixFileMode(_store.Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Press("ctrl+q");
        Assert.Equal("precious", _store.Read("Plan"));
        Assert.True(Harness.App.Stopping.IsCancellationRequested);
    }

    [Fact]
    public void A_note_that_cannot_be_saved_is_not_left_for_another()
    {
        Seed("A", "", minutesAgo: 1);
        Seed("B", "", minutesAgo: 2);
        Start();
        Press("enter");
        Type("unsaved");
        File.SetUnixFileMode(_store.Directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        Press("esc");
        Press("down");
        Assert.Equal("A", Notes.Current);
        Assert.Equal("A", Notes.Highlighted);
        Assert.Contains("unsaved", Screen, StringComparison.Ordinal);
    }

    [Fact]
    public void The_list_keeps_the_open_note_selected_when_the_notes_change()
    {
        Seed("A", "", minutesAgo: 1);
        Seed("B", "", minutesAgo: 2);
        Start();
        Press("down");
        Assert.Equal("B", Notes.Current);
        Seed("C", "");                         // newest, so first
        Notes.Refresh();
        Assert.Equal(["C", "A", "B"], Notes.Showing);
        Assert.Equal("B", Notes.Highlighted);
        Assert.Equal("B", Notes.Current);
    }

    [Fact]
    public void A_click_opens_a_note_a_double_click_edits_it_and_a_click_in_the_editor_moves_the_cursor()
    {
        Seed("A", "hello", minutesAgo: 1);
        Seed("B", "world", minutesAgo: 2);
        Start();                               // the list is 25 columns, its rows from the fourth line
        Harness.Click(3, 4);
        Harness.RunUntilIdle();
        Assert.Equal("B", Notes.Current);
        Assert.False(Notes.IsEditing);

        Harness.DoubleClick(3, 3);
        Harness.RunUntilIdle();
        Assert.Equal("A", Notes.Current);
        Assert.True(Notes.IsEditing);

        Harness.Click(29, 1);                  // the editor's text starts at column 27
        Harness.RunUntilIdle();
        Assert.Equal(new TextPosition(0, 2), Notes.Text.Cursor);
    }

    [Fact]
    public void Spaces_around_a_new_name_are_dropped()
    {
        Start();
        New("  Spaced  ");
        Assert.Equal("Spaced", Notes.Current);
        Assert.NotNull(_store.Read("Spaced"));
    }

    [Fact]
    public void Ctrl_keys_edit_the_filter_and_the_apps_keys_still_work_from_the_list()
    {
        Seed("Shopping", "");
        Seed("Work plan", "", minutesAgo: 1);
        Start();
        Type("work");
        Press("ctrl+w");                       // deletes the word typed
        Assert.Equal(["Shopping", "Work plan"], Notes.Showing);
        Press("ctrl+n");
        Assert.True(Harness.App.Overlays.Any);
    }

    // ---- Changed elsewhere ----

    [Fact]
    public void A_note_changed_on_disk_while_edited_keeps_both()
    {
        Seed("Plan", "start");
        Start();
        Press("enter");
        Press("ctrl+end");
        Type(" mine");
        File.WriteAllText(_store.PathOf("Plan"), "theirs");
        Wait(NotesScreen.SaveDelay);

        Assert.Equal("theirs", _store.Read("Plan"));
        Assert.Equal("start mine", _store.Read("Plan (conflict)"));
        Assert.Equal("Plan (conflict)", Notes.Current);
        Assert.True(Screen.Contains("Changed elsewhere, so yours is “Plan (conflict)”", StringComparison.Ordinal), Screen);

        // and carries on in the new one
        Type("!");
        Wait(NotesScreen.SaveDelay);
        Assert.Equal("start mine!", _store.Read("Plan (conflict)"));
        Assert.Equal("theirs", _store.Read("Plan"));
    }

    [Fact]
    public void Coming_back_shows_what_changed_on_disk()
    {
        Seed("Plan", "before");
        Seed("Old", "", minutesAgo: 5);
        Start();
        File.WriteAllText(_store.PathOf("Plan"), "after");
        Seed("Added", "", minutesAgo: 1);
        Harness.App.Post(Notes.Refresh);
        Harness.RunUntilIdle();
        Assert.Contains("after", Screen, StringComparison.Ordinal);
        Assert.Equal(["Plan", "Added", "Old"], Notes.Showing);
    }

    [Fact]
    public void Coming_back_after_the_note_was_deleted_opens_another()
    {
        Seed("Plan", "", minutesAgo: 1);
        Seed("Other", "", minutesAgo: 2);
        Start();
        File.Delete(_store.PathOf("Plan"));
        Harness.App.Post(Notes.Refresh);
        Harness.RunUntilIdle();
        Assert.Equal("Other", Notes.Current);
    }

    [Fact]
    public void Coming_back_leaves_unsaved_text_alone()
    {
        Seed("Plan", "before");
        Start();
        Press("enter");
        Press("ctrl+end");
        Type(" edited");
        File.WriteAllText(_store.PathOf("Plan"), "after");
        Harness.App.Post(Notes.Refresh);
        Harness.RunUntilIdle();
        Assert.Contains("before edited", Screen, StringComparison.Ordinal);
    }

    // ---- The list ----

    [Fact]
    public void The_list_is_newest_first_and_the_first_is_open()
    {
        Seed("Old", "old text", minutesAgo: 30);
        Seed("New", "new text", minutesAgo: 1);
        Start();
        Assert.Equal(["New", "Old"], Notes.Showing);
        Assert.Equal("New", Notes.Current);
        Assert.Contains("new text", Screen, StringComparison.Ordinal);
        Assert.Equal("New", _titles[^1]);
    }

    [Fact]
    public void Typing_in_the_list_filters_and_opens_the_best_match()
    {
        Seed("Shopping", "eggs", minutesAgo: 1);
        Seed("Work plan", "ship it", minutesAgo: 2);
        Seed("Holiday", "beach", minutesAgo: 3);
        Start();
        Type("plan");
        Assert.Equal(["Work plan"], Notes.Showing);
        Assert.Equal("Work plan", Notes.Current);
        Type("zzz");
        Assert.Empty(Notes.Showing);
        Assert.Contains("No match", Screen, StringComparison.Ordinal);
        Assert.Equal("Work plan", Notes.Current); // still open

        Press("esc");
        Assert.Equal(["Shopping", "Work plan", "Holiday"], Notes.Showing);
    }

    [Fact]
    public void A_new_note_is_named_from_the_filter()
    {
        Seed("Shopping", "");
        Start();
        Type("Recipes");
        Press("ctrl+n");
        Press("enter");
        Assert.Equal("Recipes", Notes.Current);
        Assert.Equal(["Recipes", "Shopping"], Notes.Showing.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_keys_move_through_the_list()
    {
        for (var i = 0; i < 30; i++) Seed($"Note {i:00}", "", minutesAgo: i);
        Start(height: 12);
        Press("end");
        Assert.Equal("Note 29", Notes.Current);
        Press("home");
        Assert.Equal("Note 00", Notes.Current);
        Press("pgdn");
        Assert.Equal("Note 07", Notes.Current); // a page is the 7 rows showing, under the filter and its divider
        Press("pgup");
        Assert.Equal("Note 00", Notes.Current);
        Press("down");
        Press("down");
        Assert.Equal("Note 02", Notes.Current);
        Press("up");
        Assert.Equal("Note 01", Notes.Current);
        Press("end");
        Assert.Contains(Harness.Lines, l => l.StartsWith("│ Note 29", StringComparison.Ordinal)); // scrolled to it
        Assert.DoesNotContain(Harness.Lines, l => l.StartsWith("│ Note 00", StringComparison.Ordinal));
    }

    [Fact]
    public void Enter_and_tab_go_to_the_editor_and_esc_and_tab_come_back()
    {
        Seed("Plan", "");
        Start();
        Press("enter");
        Assert.True(Notes.IsEditing);
        Assert.Contains("esc Notes", Status, StringComparison.Ordinal);
        Press("esc");
        Assert.False(Notes.IsEditing);
        Assert.Contains("enter Edit", Status, StringComparison.Ordinal);
        Press("tab");
        Assert.True(Notes.IsEditing);
        Press("tab");
        Assert.False(Notes.IsEditing);
    }

    [Fact]
    public void There_is_no_editor_to_go_to_without_a_note()
    {
        Start();
        Press("enter");
        Assert.False(Notes.IsEditing);
        Press("tab");
        Assert.False(Notes.IsEditing);
    }

    // ---- Rename and delete ----

    [Fact]
    public void F2_renames_the_open_note_and_saves_it_first()
    {
        Seed("Plan", "");
        Start();
        Press("enter");
        Type("text");
        Press("f2");
        Press("ctrl+a");
        Type("Big plan");
        Press("enter");
        Assert.Equal("Big plan", Notes.Current);
        Assert.Equal("text", _store.Read("Big plan"));
        Assert.Null(_store.Read("Plan"));
        Assert.Equal("Big plan", _titles[^1]);
        Assert.Contains("┌ Big plan ", Screen, StringComparison.Ordinal);
    }

    [Fact]
    public void Renaming_to_another_notes_name_is_refused_but_a_change_of_case_is_not()
    {
        Seed("Plan", "");
        Seed("Other", "");
        Start();
        Press("f2");
        Press("ctrl+a");
        Type(Notes.Current == "Plan" ? "OTHER" : "PLAN");
        Press("enter");
        Assert.Contains("There's already a note with that name", Screen, StringComparison.Ordinal);
        Press("esc");

        var name = Notes.Current!;
        Press("f2");
        Press("ctrl+a");
        Type(name.ToUpperInvariant());
        Press("enter");
        Assert.Equal(name.ToUpperInvariant(), Notes.Current);
        Assert.Contains(name.ToUpperInvariant(), _store.List().Select(n => n.Name));
    }

    [Fact]
    public void Delete_asks_first_and_then_opens_the_next()
    {
        Seed("A", "", minutesAgo: 1);
        Seed("B", "", minutesAgo: 2);
        Seed("C", "", minutesAgo: 3);
        Start();
        Press("down");
        Press("delete");
        Assert.Contains("Delete “B”?", Screen, StringComparison.Ordinal);
        Press("enter"); // Cancel is selected
        Assert.NotNull(_store.Read("B"));

        Press("delete");
        Press("left");
        Press("enter");
        Assert.Null(_store.Read("B"));
        Assert.Equal("C", Notes.Current);
        Assert.Equal(["A", "C"], Notes.Showing);
    }

    [Fact]
    public void Deleting_the_last_note_shows_none_and_has_nothing_left_to_save()
    {
        Seed("Only", "");
        Start();
        Press("enter");
        Type("doomed");
        Press("esc");
        Press("delete");
        Press("left");
        Press("enter");
        Wait(TimeSpan.FromSeconds(2));
        Assert.Empty(_store.List());
        Assert.Contains("No notes yet. Ctrl+N makes one.", Screen, StringComparison.Ordinal);
        Assert.Equal("", _titles[^1]);
    }

    // ---- Size ----

    [Fact]
    public void A_narrow_screen_shows_one_side_at_a_time()
    {
        Seed("Plan", "the text");
        Start(width: 40);
        Assert.Contains("┌ Notes ", Screen, StringComparison.Ordinal);
        Assert.DoesNotContain("the text", Screen, StringComparison.Ordinal);
        Press("enter");
        Assert.Contains("the text", Screen, StringComparison.Ordinal);
        Assert.DoesNotContain("┌ Notes ", Screen, StringComparison.Ordinal);
    }
}

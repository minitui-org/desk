namespace MiniTui.Desk.Notes.Tests;

/// <summary>The notes folder (spec §13.8).</summary>
public sealed class StoreTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly NotesStore _store;

    public StoreTests() => _store = new NotesStore(_folder.Path("tuinotes"));

    public void Dispose() => _folder.Dispose();

    private void Seed(string file, string text, int minutesAgo)
    {
        Directory.CreateDirectory(_store.Directory);
        var path = Path.Combine(_store.Directory, file);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutesAgo));
    }

    [Fact]
    public void A_folder_that_is_not_there_has_no_notes_and_is_made_with_the_first()
    {
        Assert.Empty(_store.List());
        Assert.False(Directory.Exists(_store.Directory));
        _store.Create("First");
        Assert.Equal(["First"], _store.List().Select(n => n.Name));
    }

    [Fact]
    public void Only_markdown_files_are_notes_newest_first()
    {
        Seed("old.md", "", 30);
        Seed("new.md", "", 1);
        Seed("middle.md", "", 10);
        Seed("todo.txt", "", 0);
        Seed(".hidden.md", "", 0);
        Seed("upper.MD", "", 0);
        Directory.CreateDirectory(Path.Combine(_store.Directory, "folder.md"));
        Assert.Equal(["new", "middle", "old"], _store.List().Select(n => n.Name));
    }

    [Fact]
    public void Writing_replaces_the_text_and_leaves_nothing_behind()
    {
        _store.Write("Note", "one");
        _store.Write("Note", "two\nlines\n");
        Assert.Equal("two\nlines\n", _store.Read("Note"));
        Assert.Equal(["Note.md"], Directory.GetFiles(_store.Directory).Select(Path.GetFileName));
    }

    [Fact]
    public void A_write_that_fails_leaves_nothing_behind()
    {
        Directory.CreateDirectory(_store.PathOf("Plan")); // in the way of the rename
        Assert.ThrowsAny<IOException>(() => _store.Write("Plan", "text"));
        Assert.Empty(Directory.GetFiles(_store.Directory));
    }

    [Fact]
    public void Text_is_utf8_without_a_byte_order_mark()
    {
        _store.Write("Note", "café ✓");
        Assert.Equal("café ✓"u8.ToArray(), File.ReadAllBytes(_store.PathOf("Note")));
    }

    [Fact]
    public void Writing_keeps_the_files_permissions()
    {
        _store.Write("Private", "one");
        File.SetUnixFileMode(_store.PathOf("Private"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _store.Write("Private", "two");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_store.PathOf("Private")));
    }

    [Fact]
    public void A_note_that_is_a_link_has_its_target_written()
    {
        var target = _folder.Path("elsewhere.md");
        File.WriteAllText(target, "one");
        Directory.CreateDirectory(_store.Directory);
        File.CreateSymbolicLink(_store.PathOf("Linked"), target);

        _store.Write("Linked", "two");

        Assert.Equal("two", File.ReadAllText(target));
        Assert.NotNull(new FileInfo(_store.PathOf("Linked")).LinkTarget);
    }

    [Fact]
    public void A_note_that_is_gone_reads_as_null()
    {
        Assert.Null(_store.Read("Missing"));
    }

    [Theory]
    [InlineData("", "A note needs a name")]
    [InlineData("  ", "A note needs a name")]
    [InlineData(" padded", "A name can't start or end with a space")]
    [InlineData(".hidden", "A name can't start with a dot")]
    [InlineData("a/b", "A name can't contain a slash")]
    [InlineData("a\\b", "A name can't contain a slash")]
    [InlineData("tab\there", "A name can't contain control characters")]
    public void Names_follow_the_rules(string name, string problem)
    {
        Assert.Equal(problem, NotesStore.Problem(name));
    }

    [Fact]
    public void A_long_name_is_refused_before_the_file_system_would()
    {
        Assert.Null(NotesStore.Problem(new string('a', 200)));
        Assert.Equal("That name is too long", NotesStore.Problem(new string('a', 201)));
        Assert.Equal("That name is too long", NotesStore.Problem(new string('é', 101)));
    }

    [Fact]
    public void A_name_is_taken_whatever_its_case_except_by_the_note_being_renamed()
    {
        _store.Create("Shopping");
        Assert.Equal("There's already a note with that name", _store.Problem("shopping", null));
        Assert.Null(_store.Problem("shopping", except: "Shopping"));
        Assert.Null(_store.Problem("Groceries", null));
    }

    [Fact]
    public void A_free_name_is_numbered_once_the_first_is_taken()
    {
        _store.Create("Plan");
        Assert.Equal("Plan (conflict)", _store.FreeName("Plan", "conflict"));
        _store.Create("Plan (conflict)");
        Assert.Equal("Plan (conflict 2)", _store.FreeName("Plan", "conflict"));
    }

    [Fact]
    public void Renaming_moves_the_file_and_refuses_to_overwrite()
    {
        _store.Write("A", "a");
        _store.Write("B", "b");
        _store.Rename("A", "C");
        Assert.Equal("a", _store.Read("C"));
        Assert.Null(_store.Read("A"));
        Assert.Throws<IOException>(() => _store.Rename("C", "B"));
        Assert.Equal("b", _store.Read("B"));
    }

    [Fact]
    public void The_default_folder_follows_xdg_data_home()
    {
        var before = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", "/data");
            Assert.Equal("/data/tuinotes", NotesStore.DefaultDirectory());
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", "relative");
            Assert.EndsWith("/.local/share/tuinotes", NotesStore.DefaultDirectory(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", before);
        }
    }
}

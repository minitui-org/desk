namespace MiniTui.Desk.Tasks.Tests;

/// <summary>The todo.txt file (spec §13.9): one line changed at a time, the rest byte for byte.</summary>
public sealed class StoreTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly TaskStore _store;

    public StoreTests() => _store = new TaskStore(_folder.Path("tuitasks"));

    public void Dispose() => _folder.Dispose();

    private void Seed(string text)
    {
        Directory.CreateDirectory(_store.Directory);
        File.WriteAllText(_store.FilePath, text);
    }

    private string File_ => File.ReadAllText(_store.FilePath);

    [Fact]
    public void No_file_has_no_tasks_and_the_first_one_makes_the_folder_and_the_file()
    {
        Assert.Empty(_store.Read());
        Assert.Null(_store.Changed);
        Assert.False(Directory.Exists(_store.Directory));

        _store.Add(TodoTask.Parse("Buy milk"));
        Assert.Equal("Buy milk\n", File_);
    }

    [Fact]
    public void Blank_lines_are_not_tasks_but_stay_in_the_file()
    {
        Seed("a\n\n  \nb");
        Assert.Equal(["a", "b"], _store.Read().Select(t => t.Line));
        _store.Change(TodoTask.Parse("b"), TodoTask.Parse("c"));
        Assert.Equal("a\n\n  \nc", File_);
    }

    [Fact]
    public void A_change_replaces_only_its_line_and_keeps_windows_line_ends()
    {
        Seed("one  with  spaces\r\ntwo\r\nthree\r\n");
        Assert.True(_store.Change(TodoTask.Parse("two"), TodoTask.Parse("2")));
        Assert.Equal("one  with  spaces\r\n2\r\nthree\r\n", File_);

        _store.Add(TodoTask.Parse("four"));
        Assert.Equal("one  with  spaces\r\n2\r\nthree\r\nfour\r\n", File_);
    }

    [Fact]
    public void Adding_to_a_file_without_a_last_line_break_adds_one()
    {
        Seed("one");
        _store.Add(TodoTask.Parse("two"));
        Assert.Equal("one\ntwo\n", File_);
    }

    [Fact]
    public void Removing_takes_out_the_line()
    {
        Seed("one\ntwo\nthree\n");
        Assert.True(_store.Change(TodoTask.Parse("two"), null));
        Assert.Equal("one\nthree\n", File_);
    }

    [Fact]
    public void A_line_changed_elsewhere_is_left_alone()
    {
        Seed("one\ntwo\n");
        var two = _store.Read()[1];
        File.WriteAllText(_store.FilePath, "one\ntwo, changed\n");

        Assert.False(_store.Change(two, TodoTask.Parse("2")));
        Assert.Equal("one\ntwo, changed\n", File_);
    }

    [Fact]
    public void Of_two_identical_lines_one_changes()
    {
        Seed("same\nsame\n");
        _store.Change(TodoTask.Parse("same"), TodoTask.Parse("other"));
        Assert.Equal("other\nsame\n", File_);
    }

    [Fact]
    public void A_linked_file_has_its_target_written_and_keeps_its_permissions()
    {
        var target = _folder.Path("elsewhere.txt");
        File.WriteAllText(target, "one\n");
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Directory.CreateDirectory(_store.Directory);
        File.CreateSymbolicLink(_store.FilePath, target);

        _store.Add(TodoTask.Parse("two"));

        Assert.NotNull(new FileInfo(_store.FilePath).LinkTarget);
        Assert.Equal("one\ntwo\n", File.ReadAllText(target));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(target));
    }

    [Fact]
    public void A_folder_that_can_not_be_written_throws_and_leaves_the_file()
    {
        Seed("one\n");
        File.SetUnixFileMode(_store.Directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        Assert.Throws<UnauthorizedAccessException>(() => _store.Add(TodoTask.Parse("two")));
        Assert.Equal("one\n", File_);
    }

    [Fact]
    public void The_default_folder_follows_XDG_DATA_HOME()
    {
        var was = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", "/data");
            Assert.Equal("/data/tuitasks", TaskStore.DefaultDirectory());
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", "relative");
            Assert.EndsWith("/.local/share/tuitasks", TaskStore.DefaultDirectory(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", was);
        }
    }
}

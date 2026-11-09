namespace MiniTui.Desk.Reader.Tests;

/// <summary>Folders, files as text, places and passages (spec §13.10).</summary>
public sealed class FilesTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string Make(string name, string text = "")
    {
        var path = _folder.Path(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void A_folder_lists_up_then_folders_then_readable_files_by_name_ignoring_case()
    {
        Make("b.md");
        Make("A.txt");
        Make("c.MARKDOWN");
        Make("image.png");
        Make(".hidden.md");
        Make("zeta/x.md");
        Make("Alpha/x.md");
        Make(".git/x.md");
        Assert.Equal(["..", "Alpha", "zeta", "A.txt", "b.md", "c.MARKDOWN"], Files.List(_folder.Directory).Select(e => e.Name));
        Assert.True(Files.List(_folder.Directory)[1].IsFolder);
        Assert.DoesNotContain(Files.List("/"), e => e.Name == "..");
    }

    [Fact]
    public void Links_are_followed()
    {
        Directory.CreateDirectory(_folder.Path("real"));
        Directory.CreateSymbolicLink(_folder.Path("linked"), _folder.Path("real"));
        File.CreateSymbolicLink(_folder.Path("note.md"), Make("real/target.md", "x"));
        File.CreateSymbolicLink(_folder.Path("broken.md"), _folder.Path("nowhere.md"));
        var entries = Files.List(_folder.Directory);
        Assert.True(entries.Single(e => e.Name == "linked").IsFolder);
        Assert.False(entries.Single(e => e.Name == "note.md").IsFolder);
        Assert.Contains(entries, e => e.Name == "broken.md");
    }

    [Fact]
    public void A_folder_that_can_not_be_read_throws()
    {
        Directory.CreateDirectory(_folder.Path("locked"));
        File.SetUnixFileMode(_folder.Path("locked"), UnixFileMode.None);
        Assert.Throws<UnauthorizedAccessException>(() => Files.List(_folder.Path("locked")));
    }

    [Fact]
    public void A_file_reads_as_lines_without_a_byte_order_mark_with_tabs_as_spaces()
    {
        var path = _folder.Path("a.md");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. "one\r\n\ttab\r\na\tb\n"u8, 0xFF, (byte)'\n']);
        var (lines, problem) = Files.Read(path);
        Assert.Null(problem);
        Assert.Equal(["one", "    tab", "a   b", "�"], lines);
    }

    [Fact]
    public void A_file_too_big_or_not_text_is_not_shown()
    {
        var big = _folder.Path("big.txt");
        using (var stream = File.Create(big)) stream.SetLength(Files.MaxBytes + 1);
        Assert.Equal((null, "This file is over 10 MiB, too big to show."), Files.Read(big));

        var binary = _folder.Path("bin.md");
        File.WriteAllBytes(binary, [(byte)'a', 0, (byte)'b']);
        Assert.Equal((null, "This isn't a text file."), Files.Read(binary));

        Assert.Equal((List<string>?)[""], Files.Read(Make("empty.md")).Lines);
    }

    [Fact]
    public void The_home_folder_shows_as_a_tilde()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal("~/docs/a.md", Files.Pretty(home + "/docs/a.md"));
        Assert.Equal("~", Files.Pretty(home));
        Assert.Equal(home + "x/a.md", Files.Pretty(home + "x/a.md"));
        Assert.Equal("/etc/a.md", Files.Pretty("/etc/a.md"));
    }

    // ---- Places ----

    [Fact]
    public void Places_remember_the_block_most_recent_first_and_keep_200()
    {
        var places = new Places(_folder.Path("tuireader"));
        Assert.Equal(0, places.Of("/a.md"));
        Assert.True(places.Set("/a.md", 3));
        places.Set("/b.md", 5);
        places.Set("/a.md", 7);
        Assert.Equal("7\t/a.md\n5\t/b.md\n", File.ReadAllText(places.FilePath));
        Assert.Equal(7, new Places(places.Directory).Of("/a.md"));

        for (var i = 0; i < Places.Kept + 5; i++) places.Set($"/{i}.md", i);
        Assert.Equal(Places.Kept, File.ReadAllLines(places.FilePath).Length);
        Assert.Equal(0, new Places(places.Directory).Of("/a.md"));
    }

    [Fact]
    public void Places_that_can_not_be_read_or_written_are_carried_on_without()
    {
        Make("tuireader/places.txt", "nonsense\n-1\t/x.md\n4\t/y.md\n");
        var places = new Places(_folder.Path("tuireader"));
        Assert.Equal(0, places.Of("/x.md"));
        Assert.Equal(4, places.Of("/y.md"));

        File.SetUnixFileMode(places.Directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        Assert.False(places.Set("/z.md", 1));
        Assert.Equal(1, places.Of("/z.md")); // still remembered while it runs
    }

    [Fact]
    public void The_default_folder_follows_XDG_DATA_HOME()
    {
        var was = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", "/data");
            Assert.Equal("/data/tuireader", Places.DefaultDirectory());
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", "relative");
            Assert.EndsWith("/.local/share/tuireader", Places.DefaultDirectory(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", was);
        }
    }

    // ---- Passages ----

    [Theory]
    [InlineData("/docs/garden-plan.md", "garden-plan")]
    [InlineData("/docs/notes.txt", "notes")]
    [InlineData("/docs/.profile.md", "profile")]
    [InlineData("/docs/.md", "Reader")]
    [InlineData("/docs/a.b.md", "a.b")]
    public void A_passage_goes_to_the_note_named_after_its_file(string path, string name) => Assert.Equal(name, Passages.NoteName(path));

    [Fact]
    public void A_passage_is_quoted_with_where_it_is_from_and_a_line_to_write_on()
    {
        Assert.Equal("> Tomatoes need sun\n>\n> and water.\n\n— ~/docs/garden.md\n\n", Passages.Quote("\nTomatoes need sun  \n\nand water.\n\n", "~/docs/garden.md"));
    }
}

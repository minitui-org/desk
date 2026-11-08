using System.Diagnostics;
using MiniTui.Desk.Protocol;

namespace MiniTui.Desk.Reader.Tests;

/// <summary>Reader inside Desk (spec §13.10), with a real client and a scripted shell.</summary>
public sealed class DeskTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly TempFolder _folder = new();
    private readonly FakeShell _shell = new();
    private ReaderScreen? _reader;
    private ComponentHarness? _harness;

    public void Dispose()
    {
        _harness?.Dispose();
        _shell.Dispose();
        _folder.Dispose();
    }

    private ComponentHarness Harness => _harness!;

    private string Make(string name, string text)
    {
        var path = _folder.Path(name);
        File.WriteAllText(path, text);
        return path;
    }

    private void Start(string file)
    {
        DeskClient? desk = null;
        var app = (App?)null;
        _reader = new ReaderScreen(new Places(_folder.Path("data")), file, title => desk?.SetTitle(title), (name, text) => NotesLink.For(app!, desk!)(name, text));
        _harness = new ComponentHarness(_reader, 80, 16);
        app = Harness.App;
        Harness.App.WithDesk("Reader", out desk, null, _shell.SocketPath, "reader-1");
        DeskLink.Join(_reader, desk);
        Assert.Contains("\"hello\"", _shell.Next(), StringComparison.Ordinal);
    }

    private void Until(Func<bool> condition, Func<string> describe)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            Harness.RunUntilIdle();
            if (condition()) return;
            Assert.True(clock.Elapsed < Timeout, "Timed out: " + describe());
            Thread.Sleep(5);
        }
    }

    private string Next()
    {
        Harness.RunUntilIdle();
        return _shell.Next();
    }

    [Fact]
    public void Ctrl_n_asks_Desk_to_open_Notes_with_the_passage()
    {
        var garden = Make("garden.md", "# Garden\n\nTomatoes \"need\" sun.");
        Start(garden);
        Harness.Press("down");
        Harness.Press("ctrl+n");
        using var launch = System.Text.Json.JsonDocument.Parse(Next());
        Assert.Equal("launch", launch.RootElement.GetProperty("type").GetString());
        Assert.Equal("notes", launch.RootElement.GetProperty("appId").GetString());
        Assert.Equal(["--append", "garden", $"> Tomatoes \"need\" sun.\n\n— {Files.Pretty(garden)}\n\n"],
            launch.RootElement.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
        Until(() => Harness.Lines.Any(l => l.Contains("Sent to Notes", StringComparison.Ordinal)), () => string.Join('\n', Harness.Lines));
    }

    [Fact]
    public void The_title_is_the_open_file()
    {
        var garden = Make("garden.md", "text");
        Make("other.md", "text");
        Start(garden);                            // the first title goes before the shell is connected here
        Harness.Press("esc");
        Harness.Press("up");                      // .. is a folder, so the open file and title stay
        Harness.Press("down");
        Harness.Press("down");
        Assert.Equal("""{"type":"title","text":"other.md"}""", Next());
    }

    [Fact]
    public void Getting_the_focus_back_shows_what_changed()
    {
        var garden = Make("garden.md", "Before");
        Start(garden);
        _shell.Send("""{"type":"focus","focused":false}""");
        File.WriteAllText(garden, "After");
        _shell.Send("""{"type":"focus","focused":true}""");
        Until(() => Harness.Lines.Any(l => l.Contains("After", StringComparison.Ordinal)), () => string.Join('\n', Harness.Lines));
    }
}

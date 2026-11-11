using MiniTui;
using MiniTui.Desk.Protocol;

namespace MyApp.Tests;

/// <summary>The screen in a <see cref="ComponentHarness"/>: keys in, the screen and what it told Desk out.</summary>
public sealed class MainScreenTests : IDisposable
{
    private readonly List<string> _titles = [];
    private readonly List<DeskNotification> _notifications = [];
    private readonly MainScreen _screen;
    private readonly ComponentHarness _harness;

    public MainScreenTests()
    {
        _screen = new MainScreen(_titles.Add, _notifications.Add);
        _harness = new ComponentHarness(_screen, width: 40, height: 10);
        _harness.RunUntilIdle();
    }

    public void Dispose() => _harness.Dispose();

    private string Screen => string.Join('\n', _harness.Lines);

    [Fact]
    public void It_lists_the_items_with_the_first_as_the_title()
    {
        Assert.Contains("Apples", Screen, StringComparison.Ordinal);
        Assert.Contains("Eggs", Screen, StringComparison.Ordinal);
        Assert.Equal("Apples", _titles[^1]);
    }

    [Fact]
    public void Typing_filters_and_the_title_follows()
    {
        _harness.Type("cof");
        Assert.DoesNotContain("Bread", Screen, StringComparison.Ordinal);
        Assert.Equal("Coffee", _screen.Selected);
        Assert.Equal("Coffee", _titles[^1]);
    }

    [Fact]
    public void Enter_sends_a_notification()
    {
        _harness.Press("down");
        _harness.Press("enter");
        Assert.Equal("You picked Bread", Assert.Single(_notifications).Body);
    }
}

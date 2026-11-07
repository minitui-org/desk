namespace MiniTui.Desk.Protocol.Tests;

public sealed class WithDeskTests : IDisposable
{
    private readonly FakeShell _shell = new();
    private readonly ComponentHarness _harness = new(new Blank());

    private sealed class Blank : Screen
    {
        public override string Title => "Blank";

        public override void Render(CellBuffer buf, Rect area, RenderContext ctx)
        {
        }
    }

    public void Dispose()
    {
        _harness.Dispose();
        _shell.Dispose();
    }

    private DeskClient Connect()
    {
        _harness.App.WithDesk("Notes", out var client, null, _shell.SocketPath, "notes-1");
        return client;
    }

    private void Until(Func<bool> condition)
    {
        var start = DateTime.UtcNow;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow - start < TimeSpan.FromSeconds(10), "Timed out");
            _harness.RunUntilIdle();
            Thread.Sleep(5);
        }
    }

    [Fact]
    public void Events_wait_for_the_apps_thread()
    {
        var client = Connect();
        var thread = -1;
        client.FocusChanged += _ => thread = Environment.CurrentManagedThreadId;
        _shell.Send("""{"type":"focus","focused":false}""");
        Thread.Sleep(100);
        Assert.False(client.IsFocused);

        // It has arrived, and nothing has run it yet
        Assert.Equal(-1, thread);
        Until(() => thread != -1);
        Assert.Equal(Environment.CurrentManagedThreadId, thread);
    }

    [Fact]
    public void A_request_to_quit_quits_with_no_handler()
    {
        Connect();
        _shell.Send("""{"type":"quitRequest"}""");
        Until(() => _harness.App.Stopping.IsCancellationRequested);
    }

    [Fact]
    public void A_handler_decides_about_quitting_instead()
    {
        var client = Connect();
        var asked = false;
        client.QuitRequested += () => asked = true;
        _shell.Send("""{"type":"quitRequest"}""");
        Until(() => asked);
        Assert.False(_harness.App.Stopping.IsCancellationRequested);
    }

    [Fact]
    public void A_handler_added_after_connecting_still_hears_what_came_first()
    {
        var client = Connect();
        _shell.Send("""{"type":"quitRequest"}""");
        Thread.Sleep(100);

        var asked = false;
        client.QuitRequested += () => asked = true;
        Until(() => asked);
        Assert.False(_harness.App.Stopping.IsCancellationRequested);
    }

    [Fact]
    public void A_handler_that_throws_goes_to_the_apps_error_handling()
    {
        var client = Connect();
        Exception? seen = null;
        _harness.App.OnError(e =>
        {
            seen = e;
            return ErrorAction.Continue;
        });
        client.QuitRequested += () => throw new InvalidOperationException("the app's bug");
        _shell.Send("""{"type":"quitRequest"}""");
        Until(() => seen is not null);
        Assert.IsType<InvalidOperationException>(seen);
    }

    [Fact]
    public async Task Nothing_is_delivered_once_the_client_is_disposed()
    {
        var client = Connect();
        var asked = false;
        client.QuitRequested += () => asked = true;
        _shell.Send("""{"type":"quitRequest"}""");
        Thread.Sleep(100);

        await client.DisposeAsync();
        _harness.RunUntilIdle();
        Assert.False(asked);
        Assert.False(_harness.App.Stopping.IsCancellationRequested);
    }

    [Fact]
    public void Stopping_the_app_says_bye()
    {
        Connect();
        Assert.Contains("hello", _shell.Next(), StringComparison.Ordinal);
        _harness.App.Quit();
        Assert.Equal("""{"type":"bye"}""", _shell.Next());
    }

    [Fact]
    public void Outside_Desk_it_does_nothing_and_returns_the_app()
    {
        var app = _harness.App;
        Assert.Same(app, app.WithDesk("Notes", out var client, null, null, null));
        Assert.False(client.IsActive);
        client.SetTitle("t");
    }

    [Fact]
    public void The_public_overload_reads_the_environment()
    {
        // The test host isn't running under Desk
        Assert.Same(_harness.App, _harness.App.WithDesk("Notes", out var client));
        Assert.False(client.IsActive);
    }
}

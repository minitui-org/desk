using System.Diagnostics;

namespace MiniTui.Desk.Protocol.Tests;

public sealed class DeskClientTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // Long enough that a busy machine never misses the welcome; the real wait is tested with a silent shell
    private static readonly TimeSpan StartupWait = TimeSpan.FromSeconds(10);
    private readonly FakeShell _shell = new();

    public void Dispose() => _shell.Dispose();

    private DeskClient Connected() => DeskClient.Connect("Notes", "0.1.0", _shell.SocketPath, "notes-1", startupWait: StartupWait);

    private static void Until(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < Timeout, "Timed out");
            Thread.Sleep(5);
        }
    }

    // ---- Starting ----

    [Fact]
    public async Task It_says_hello_first_and_takes_the_theme_from_welcome()
    {
        await using var client = Connected();
        Assert.True(client.IsActive);
        Assert.Equal("dark", client.Theme);
        Assert.Equal("""{"type":"hello","appId":"notes-1","name":"Notes","version":"0.1.0","protocol":0}""", _shell.Next());
    }

    [Fact]
    public async Task A_version_is_optional()
    {
        await using var client = DeskClient.Connect("Notes", null, _shell.SocketPath, "notes-1", startupWait: StartupWait);
        Assert.Equal("""{"type":"hello","appId":"notes-1","name":"Notes","protocol":0}""", _shell.Next());
    }

    [Fact]
    public async Task A_silent_shell_costs_half_a_second_and_a_late_welcome_still_counts()
    {
        using var shell = new FakeShell(welcome: null);
        var clock = Stopwatch.StartNew();
        await using var client = DeskClient.Connect("Notes", "0.1.0", shell.SocketPath, "notes-1");
        clock.Stop();

        Assert.InRange(clock.ElapsedMilliseconds, 400, 3000);
        Assert.True(client.IsActive);
        Assert.Null(client.Theme);

        shell.Send("""{"type":"welcome","protocol":0,"theme":"light"}""");
        Until(() => client.Theme == "light");
    }

    [Fact]
    public async Task Welcome_ends_the_wait_early()
    {
        var clock = Stopwatch.StartNew();
        await using var client = Connected();
        Assert.True(clock.Elapsed < StartupWait / 2, $"{clock.ElapsedMilliseconds} ms");
        Assert.Equal("dark", client.Theme);
    }

    // ---- Inactive ----

    public static TheoryData<string?, string?> NotInDesk => new()
    {
        { null, null },
        { null, "notes-1" },
        { "", "notes-1" },
        { "/nonexistent/socket", null },
        { "/nonexistent/socket", "" },
        { "/nonexistent/socket", "notes-1" },
        { new string('x', 500), "notes-1" },
    };

    [Theory]
    [MemberData(nameof(NotInDesk))]
    public async Task Without_a_shell_every_member_does_nothing(string? socket, string? appId)
    {
        var client = DeskClient.Connect("Notes", null, socket, appId);
        Assert.False(client.IsActive);
        Assert.True(client.IsFocused);
        Assert.Null(client.Theme);

        client.SetTitle("t");
        client.Notify(new DeskNotification("a", "t", "b"));
        client.Launch("notes", "x");
        client.DeclineQuit();
        client.FocusChanged += _ => { };

        await client.DisposeAsync();
        await client.DisposeAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_reachable_shell_is_not_used_without_an_app_id(string? appId)
    {
        Assert.False(DeskClient.Connect("Notes", null, _shell.SocketPath, appId).IsActive);
        _shell.AssertQuiet();
    }

    [Fact]
    public void A_socket_with_nobody_listening_leaves_it_inactive()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("desk-protocol-").FullName, "s");
        using (var dead = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified))
        {
            dead.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(path));
        }
        Assert.False(DeskClient.Connect("Notes", null, path, "notes-1").IsActive);
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public async Task Nulls_from_a_careless_app_do_not_throw()
    {
        await using var client = Connected();
        _shell.Next();
        client.SetTitle(null!);
        client.Notify(null!);
        client.Launch(null!);
        client.Launch("notes", null!);
        Assert.Equal("""{"type":"launch","appId":"notes","args":[]}""", _shell.Next());
    }

    [Fact]
    public void The_public_connect_reads_the_environment_and_is_inactive_without_it()
    {
        // The test host isn't running under Desk
        var client = DeskClient.Connect("Notes");
        Assert.False(client.IsActive);
    }

    // ---- Sending ----

    [Fact]
    public async Task Each_call_sends_its_message()
    {
        await using var client = Connected();
        _shell.Next();

        client.SetTitle("Today");
        Assert.Equal("""{"type":"title","text":"Today"}""", _shell.Next());

        client.Notify(new DeskNotification("sync", "Synced", "12 notes") { Urgency = DeskUrgency.Low });
        Assert.Equal("""{"type":"notify","id":"sync","title":"Synced","body":"12 notes","urgency":"low"}""", _shell.Next());

        client.Notify(new DeskNotification("a", "t", "b") { Actions = [new DeskAction("done", "Done")] });
        Assert.Equal("""{"type":"notify","id":"a","title":"t","body":"b","urgency":"normal","actions":[{"id":"done","label":"Done"}]}""", _shell.Next());

        client.Launch("notes", "a", "b");
        Assert.Equal("""{"type":"launch","appId":"notes","args":["a","b"]}""", _shell.Next());

        client.DeclineQuit();
        Assert.Equal("""{"type":"quitDeclined"}""", _shell.Next());
    }

    [Fact]
    public async Task Line_breaks_in_text_cannot_split_a_message()
    {
        await using var client = Connected();
        _shell.Next();
        client.SetTitle("a\nb");
        Assert.Equal("""{"type":"title","text":"a\nb"}""", _shell.Next());
    }

    [Fact]
    public async Task Disposing_says_bye_once_and_then_it_is_inactive()
    {
        var client = Connected();
        _shell.Next();
        await client.DisposeAsync();
        Assert.Equal("""{"type":"bye"}""", _shell.Next());
        Assert.False(client.IsActive);

        client.SetTitle("late");
        await client.DisposeAsync();
        _shell.AssertQuiet();
    }

    [Fact]
    public async Task A_message_over_the_limit_is_dropped_and_the_next_still_goes()
    {
        await using var client = Connected();
        _shell.Next();
        client.SetTitle(new string('x', DeskWire.MaxLine));
        client.SetTitle("next");
        Assert.Equal("""{"type":"title","text":"next"}""", _shell.Next());
        Assert.True(client.IsActive);
    }

    // ---- Receiving ----

    [Fact]
    public async Task Focus_changes_are_reported_only_when_they_change()
    {
        await using var client = Connected();
        var seen = new List<bool>();
        client.FocusChanged += f =>
        {
            lock (seen) seen.Add(f);
        };

        _shell.Send("""{"type":"focus","focused":true}""");
        _shell.Send("""{"type":"focus","focused":false}""");
        _shell.Send("""{"type":"focus","focused":false}""");
        _shell.Send("""{"type":"focus","focused":true}""");
        Until(() =>
        {
            lock (seen) return seen.Count == 2;
        });
        lock (seen) Assert.Equal([false, true], seen);
        Assert.True(client.IsFocused);
    }

    [Fact]
    public async Task Notification_actions_and_quit_requests_arrive()
    {
        await using var client = Connected();
        string? chosen = null;
        var quit = 0;
        client.NotificationAction += (id, action) => Volatile.Write(ref chosen, id + "/" + action);
        client.QuitRequested += () => Interlocked.Increment(ref quit);

        _shell.Send("""{"type":"notificationAction","id":"a","action":"done"}""");
        _shell.Send("""{"type":"quitRequest"}""");
        Until(() => Volatile.Read(ref chosen) == "a/done" && Volatile.Read(ref quit) == 1);
    }

    [Fact]
    public async Task What_it_does_not_understand_is_skipped()
    {
        await using var client = Connected();
        var quit = 0;
        client.QuitRequested += () => Interlocked.Increment(ref quit);

        _shell.Send("""{"type":"somethingLater","x":1}""");
        _shell.Send("not json");
        _shell.SendBytes([0xFF, 0xFE, (byte)'\n']);
        _shell.Send("""{"type":"notificationAction"}""");
        _shell.Send("""{"type":"focus"}""");
        _shell.Send("""{"type":"quitRequest"}""");
        Until(() => Volatile.Read(ref quit) == 1);
        Assert.True(client.IsActive);
        Assert.True(client.IsFocused);
    }

    [Fact]
    public async Task A_handler_that_throws_does_not_end_the_connection()
    {
        await using var client = Connected();
        var calls = 0;
        client.QuitRequested += () =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("the app's bug");
        };
        _shell.Send("""{"type":"quitRequest"}""");
        _shell.Send("""{"type":"quitRequest"}""");
        Until(() => Volatile.Read(ref calls) == 2);
        Assert.True(client.IsActive);
    }

    // ---- Failure ----

    [Fact]
    public async Task A_dropped_connection_goes_inactive_and_calls_do_nothing()
    {
        await using var client = Connected();
        _shell.Hangup();
        Until(() => !client.IsActive);
        client.SetTitle("t");
        client.Notify(new DeskNotification("a", "t", "b"));
        client.Launch("notes");
        client.DeclineQuit();
    }

    [Fact]
    public async Task An_app_in_the_background_when_the_shell_goes_is_told_it_has_the_focus()
    {
        await using var client = Connected();
        bool? last = null;
        var count = 0;
        client.FocusChanged += f =>
        {
            last = f;
            Interlocked.Increment(ref count);
        };
        _shell.Send("""{"type":"focus","focused":false}""");
        Until(() => !client.IsFocused);

        _shell.Hangup();
        Until(() => Volatile.Read(ref count) == 2);
        Assert.True(last);
        Assert.True(client.IsFocused);
    }

    [Fact]
    public async Task A_line_too_long_from_the_shell_ends_the_connection()
    {
        await using var client = Connected();
        _shell.SendBytes(new byte[DeskWire.MaxLine + 1]);
        Until(() => !client.IsActive);
    }

    [Fact]
    public async Task A_shell_that_stops_reading_never_blocks_the_app()
    {
        using var shell = new FakeShell(read: false);
        var client = DeskClient.Connect("Notes", "0.1.0", shell.SocketPath, "notes-1");
        Assert.True(client.IsActive);

        var big = new string('x', 60_000);
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 2000; i++) client.SetTitle(big);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"Sending took {clock.Elapsed}");
        Assert.True(client.IsActive);

        clock.Restart();
        await client.DisposeAsync();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"Disposing took {clock.Elapsed}");
    }
}

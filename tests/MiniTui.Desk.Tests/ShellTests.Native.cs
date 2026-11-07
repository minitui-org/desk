using System.Collections.Concurrent;
using System.Text;
using MiniTui.Desk.Protocol;

namespace MiniTui.Desk.Tests;

/// <summary>
/// Native apps (spec §13.4, §13.6 2b): the shell with a TestBackend, hosting real children, with clients connecting
/// as them from the test, and with a real client in a real child.
/// </summary>
public sealed partial class ShellTests
{
    private static readonly string TestApp = Path.Combine(AppContext.BaseDirectory, "desk-test-app");

    private readonly List<IDisposable> _clients = [];
    private readonly List<DeskClient> _deskClients = [];

    private HostedApp Front => Shell.Foreground!;

    private void DisposeClients()
    {
        foreach (var client in _clients) client.Dispose();
        foreach (var client in _deskClients) client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private RawClient Raw()
    {
        var client = new RawClient(Shell.SocketPath!);
        _clients.Add(client);
        return client;
    }

    // Said hello as the app, and been welcomed
    private RawClient Connect(HostedApp app, string name = "Raw")
    {
        var client = Raw();
        client.Hello(app.Id, name);
        Until(() => client.Got("\"welcome\""), "the welcome");
        return client;
    }

    // A real client, as an app would make one. It waits for the welcome, which needs the UI thread running.
    private DeskClient Client(HostedApp app, string name)
    {
        var connecting = Task.Run(() => DeskClient.Connect(name, "1.0", Shell.SocketPath, app.Id));
        Until(() => connecting.IsCompleted, "the client");
        var client = connecting.Result;
        _deskClients.Add(client);
        Assert.True(client.IsActive);
        Until(() => app.Connections.Count > 0 && app.Name == name, "the connection");
        return client;
    }

    private static void Send(RawClient client, string json) => client.Send(json + "\n");

    private string Log(string name) => _files.Path(name + ".log");

    private string Logged(string name) => File.Exists(Log(name)) ? File.ReadAllText(Log(name)) : "";

    private void UntilLogged(string name, string text) =>
        Until(() => Logged(name).Contains(text, StringComparison.Ordinal), $"\"{text}\" in {name}'s log:\n{Logged(name)}");

    private AppEntry Native(string id, string name) =>
        new() { Id = id, Name = name, Command = TestApp, Args = [name, Log(name)] };

    private string RowWith(string text) => Harness.Lines.FirstOrDefault(l => l.Contains(text, StringComparison.Ordinal))?.TrimEnd(' ', '│', '┃') ?? "";

    // A native app reading the keyboard: what's typed before then is flushed as it sets up the terminal
    private void UntilReady(string name)
    {
        UntilLogged(name, "started active=True");
        UntilShows(name + " ready");
    }

    private void Command(string command)
    {
        Harness.Type(command);
        Harness.Press("enter");
    }

    // ---- Socket and environment ----

    [Fact]
    public void Every_child_is_told_the_socket_and_its_own_id()
    {
        var output = _files.Path("env");
        var entry = new AppEntry { Id = "notes", Name = "notes", Command = _files.Script($"echo \"$MINITUI_DESK_APP_ID $MINITUI_DESK_SOCKET\" >> '{output}'; exec cat") };
        Start(config: WithApps(entry, entry with { Id = null, Name = "unnamed" }));
        Until(() => Shell.Foreground is not null);
        Launch("notes");
        Launch("notes");
        Launch("unnamed");

        Until(() => File.Exists(output) && File.ReadAllLines(output).Length == 3, "the children");
        var socket = Shell.SocketPath!;
        Assert.Equal([$"notes-1 {socket}", $"notes-2 {socket}", $"app-1 {socket}"], File.ReadAllLines(output));
        Assert.Equal(["shell-1", "notes-1", "notes-2", "app-1"], Shell.Apps.Select(a => a.Id));
    }

    [Fact]
    public void Instance_numbers_are_not_reused()
    {
        Start(config: WithApps(new AppEntry { Id = "notes", Name = "notes", Command = _files.Script("exec cat") }));
        Until(() => Shell.Foreground is not null);
        Launch("notes");
        Assert.Equal("notes-1", Front.Id);
        Harness.Press("ctrl+space ctrl+x");
        Until(() => Shell.Apps.Count == 1, "notes to close");
        Launch("notes");
        Assert.Equal("notes-2", Front.Id);
    }

    [Fact]
    public void A_restart_keeps_the_id()
    {
        var output = _files.Path("env");
        Start($"echo \"$MINITUI_DESK_APP_ID\" >> '{output}'; exit 3");
        Until(() => Front.Failure is not null, "the failure");
        Harness.Press("r");
        Until(() => File.ReadAllLines(output).Length == 2, "the restart");
        Assert.Equal(["shell-1", "shell-1"], File.ReadAllLines(output));
    }

    [Fact]
    public void Without_a_socket_the_variables_are_removed_and_apps_still_run()
    {
        var blocker = _files.Path("file");
        File.WriteAllText(blocker, "");
        var output = _files.Path("env");
        Environment.SetEnvironmentVariable("MINITUI_DESK_SOCKET", "/outer.sock");
        try
        {
            Start($"echo \"[$MINITUI_DESK_SOCKET][$MINITUI_DESK_APP_ID]\" > '{output}'; exec cat", new DeskConfig { RuntimeDirectory = Path.Combine(blocker, "run") });
            Until(() => File.Exists(output) && File.ReadAllText(output).Length > 0, "the child");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MINITUI_DESK_SOCKET", null);
        }
        Assert.Equal("[][]\n", File.ReadAllText(output));
        Assert.Null(Shell.SocketPath);
        Assert.Contains("Native apps are off", Assert.Single(Shell.Notifications).Text, StringComparison.Ordinal);
    }

    // ---- Hello, and clients that misbehave ----

    [Fact]
    public void A_hello_for_an_app_that_is_not_running_closes_the_connection()
    {
        Start();
        Until(() => Shell.Foreground is not null);
        var client = Raw();
        client.Hello("notes-9");
        Until(() => client.Closed, "the close");
        Assert.DoesNotContain(client.Received, l => l.Contains("welcome", StringComparison.Ordinal));
        Assert.Empty(Front.Connections);
    }

    [Fact]
    public void Anything_before_hello_is_ignored()
    {
        Start();
        Until(() => Shell.Foreground is not null);
        var client = Raw();
        Send(client, """{"type":"title","text":"early"}""");
        Send(client, """{"type":"notify","id":"a","title":"t","body":"early","urgency":"high"}""");
        client.Hello(Front.Id, "Late");
        Until(() => client.Got("\"welcome\""), "the welcome");

        Assert.Null(Front.Native!.Title);
        Assert.Empty(Shell.Notifications);
        Assert.Contains("""{"type":"welcome","protocol":0,"theme":"dark"}""", client.Received);
        Assert.Contains("""{"type":"focus","focused":true}""", client.Received);
    }

    [Fact]
    public void Welcome_offers_no_higher_version_than_the_shell_speaks()
    {
        Start();
        Until(() => Shell.Foreground is not null);
        var client = Raw();
        Send(client, $$"""{"type":"hello","appId":"{{Front.Id}}","name":"Future","protocol":7}""");
        Until(() => client.Got("\"welcome\""), "the welcome");
        Assert.Contains("""{"type":"welcome","protocol":0,"theme":"dark"}""", client.Received);
    }

    [Fact]
    public void Bye_ends_the_connection_even_if_the_app_keeps_the_socket_open()
    {
        Start();
        Until(() => Shell.Foreground is not null);
        var client = Connect(Front, "Leaving");
        Until(() => Front.Name == "Leaving", "the name");
        Send(client, """{"type":"bye"}""");
        Until(() => Front.Connections.Count == 0 && client.Closed, "the connection to close");
    }

    [Fact]
    public void A_line_over_64_KiB_closes_that_connection_only()
    {
        Start();
        Until(() => Shell.Foreground is not null);
        var other = Connect(Front, "Other");
        var client = Connect(Front, "Long");
        client.Send("{\"type\":\"title\",\"text\":\"" + new string('x', 64 * 1024) + "\"}\n");
        Until(() => client.Closed, "the close");

        Until(() => Front.Connections.Count == 1, "the connection to go");
        Assert.Equal("Other", Front.Name);
        Send(other, """{"type":"title","text":"still here"}""");
        Until(() => Status.Contains("Other: still here", StringComparison.Ordinal), "the other title");
    }

    [Fact]
    public void Nothing_a_client_sends_takes_down_the_shell()
    {
        Start(config: WithApps(new AppEntry { Id = "two", Command = _files.Script("exec cat") }));
        Until(() => Shell.Foreground is not null);
        var client = Connect(Front);
        string[] junk =
        [
            "not json",
            "{}",
            "[]",
            "null",
            """{"type":null}""",
            """{"type":7}""",
            """{"type":"title"}""",
            """{"type":"title","text":7}""",
            """{"type":"notify"}""",
            """{"type":"notify","id":null,"actions":[null,{},{"id":"x"},{"label":"y"}]}""",
            """{"type":"notify","id":"a","urgency":"extreme","actions":"none"}""",
            """{"type":"launch"}""",
            """{"type":"launch","appId":"two","args":[null,"a"]}""",
            """{"type":"launch","appId":"nope"}""",
            """{"type":"hello","appId":"shell-1","protocol":-5}""",
            """{"type":"quitDeclined","extra":{"deep":[1,2,3]}}""",
            """{"type":"protocol","protocol":1e400}""",
            new string('[', 1000),
        ];
        foreach (var line in junk) Send(client, line);
        client.SendBytes([.. "{\"type\":\"title\",\"text\":\""u8, 0xff, 0xfe, .. "\"}\n"u8]);
        Send(client, """{"type":"title","text":"after"}""");
        Until(() => Shell.Apps[0].Title == "after", "the shell to read it all");

        // Still answering, and the launch among the junk was a real one from the foreground app
        Assert.False(client.Closed);
        Assert.Equal(["shell-1", "two-1"], Shell.Apps.Select(a => a.Id));
        var fresh = Connect(Shell.Apps[0], "Fresh");
        Assert.False(fresh.Closed);
    }

    // ---- Name and title ----

    [Fact]
    public void The_protocol_title_wins_over_the_terminal_title_while_connected()
    {
        Start("printf '\\033]2;from osc\\007'; exec cat");
        Until(() => Status.Contains("from osc", StringComparison.Ordinal), "the OSC title");
        var client = Connect(Front, "Notes");
        Until(() => Status.StartsWith(" Notes: from osc", StringComparison.Ordinal) || Status.Contains("Notes: from osc", StringComparison.Ordinal), "the name");

        Send(client, """{"type":"title","text":"Today\u001b[31m\tlist"}""");
        Until(() => Status.Contains("Notes: Today [31m list", StringComparison.Ordinal), "the protocol title");

        client.Dispose();
        Until(() => Front.Connections.Count == 0, "the connection to go");
        Until(() => Status.Contains(": from osc", StringComparison.Ordinal) && !Status.Contains("Notes", StringComparison.Ordinal), "the OSC title again");
    }

    [Fact]
    public void Several_connections_on_one_id_follow_the_most_recent_hello()
    {
        Start();
        Until(() => Shell.Foreground is not null);
        var shellName = Front.Name;
        var first = Connect(Front, "First");
        Send(first, """{"type":"title","text":"one"}""");
        Until(() => Status.Contains("First: one", StringComparison.Ordinal), "the first");

        var second = Connect(Front, "Second");
        Until(() => Status.Contains("Second", StringComparison.Ordinal), "the second");
        Assert.DoesNotContain("one", Status, StringComparison.Ordinal);

        second.Dispose();
        Until(() => Status.Contains("First: one", StringComparison.Ordinal), "the first again");
        first.Dispose();
        Until(() => Front.Connections.Count == 0 && Front.Name == shellName, "the shell's own name");
    }

    // ---- Focus ----

    [Fact]
    public void Focus_is_sent_on_every_switch()
    {
        Start(config: WithApps(Entry("two", "exec cat")));
        Until(() => Shell.Foreground is not null);
        var first = Front;
        var client = Client(first, "First");
        var seen = new ConcurrentQueue<bool>();
        client.FocusChanged += seen.Enqueue;

        Launch("two");
        Until(() => !client.IsFocused, "the focus to go");
        Harness.Press("ctrl+space 1");
        Until(() => client.IsFocused, "the focus to come back");
        Assert.Equal([false, true], seen);
    }

    [Fact]
    public void A_connection_arriving_in_the_background_is_told_so()
    {
        Start(config: WithApps(Entry("two", "exec cat")));
        Until(() => Shell.Foreground is not null);
        var first = Front;
        Launch("two");
        var client = Connect(first);
        Until(() => client.Got("""{"type":"focus","focused":false}"""), "the focus");
    }

    // ---- Notifications ----

    [Theory]
    [InlineData("low", true, false)]
    [InlineData("normal", true, false)]
    [InlineData("high", true, false)]
    [InlineData("low", false, false)]
    [InlineData("normal", false, true)]
    [InlineData("high", false, true)]
    public void The_notification_policy_follows_the_table(string urgency, bool doNotDisturb, bool toast)
    {
        Start(config: new DeskConfig { DoNotDisturb = doNotDisturb });
        Until(() => Shell.Foreground is not null);
        var client = Connect(Front, "Notes");
        Send(client, $$"""{"type":"notify","id":"a","title":"Synced","body":"12 notes","urgency":"{{urgency}}"}""");
        Until(() => Shell.Unread == 1, "the count");
        Assert.Equal("Notes: Synced: 12 notes", Shell.Notifications[0].Text);
        Thread.Sleep(50);
        Harness.RunUntilIdle();
        Assert.Equal(toast ? 1 : 0, Harness.App.Toasts.Count);
    }

    [Fact]
    public void A_repeated_id_replaces_the_entry_in_place_and_counts_once_until_read()
    {
        Start(config: WithApps(Entry("two", "exec cat")));
        Until(() => Shell.Foreground is not null);
        var notes = Connect(Front, "Notes");
        var other = Connect(Front, "Other");
        Send(notes, """{"type":"notify","id":"sync","title":"Sync","body":"1","urgency":"normal"}""");
        Until(() => Shell.Unread == 1, "the first");
        other.Dispose();
        Send(notes, """{"type":"notify","id":"later","title":"Later","body":"x","urgency":"normal"}""");
        Until(() => Shell.Unread == 2, "the second");

        Send(notes, """{"type":"notify","id":"sync","title":"Sync","body":"2","urgency":"normal"}""");
        Until(() => Shell.Notifications[0].Text.EndsWith(": 2", StringComparison.Ordinal), "the replacement");
        Assert.Equal(2, Shell.Notifications.Count);
        Assert.Equal(2, Shell.Unread);

        Harness.Press("ctrl+space ctrl+n esc");
        Assert.Equal(0, Shell.Unread);
        Send(notes, """{"type":"notify","id":"sync","title":"Sync","body":"3","urgency":"normal"}""");
        Until(() => Shell.Unread == 1, "the read one to count again");
        Assert.EndsWith(": 3", Shell.Notifications[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_id_from_another_app_is_another_entry()
    {
        Start(config: WithApps(Entry("two", "exec cat")));
        Until(() => Shell.Foreground is not null);
        var first = Connect(Front, "One");
        Launch("two");
        var second = Connect(Front, "Two");
        Send(first, """{"type":"notify","id":"a","title":"t","body":"one","urgency":"normal"}""");
        Send(second, """{"type":"notify","id":"a","title":"t","body":"two","urgency":"normal"}""");
        Until(() => Shell.Notifications.Count == 2, "both");
    }

    [Fact]
    public async Task Choosing_an_action_tells_the_app_and_actions_go_with_the_app()
    {
        Start();
        Until(() => Shell.Foreground is not null);
        var client = Client(Front, "Tasks");
        var chosen = new ConcurrentQueue<string>();
        client.NotificationAction += (id, action) => chosen.Enqueue(id + "/" + action);
        client.Notify(new DeskNotification("due", "Due", "Pay rent") { Actions = [new("done", "Done"), new("snooze", "Snooze")] });
        Until(() => Shell.Unread == 1, "the notification");

        Harness.Press("ctrl+space ctrl+n");
        Until(() => RowWith("Pay rent").EndsWith("actions", StringComparison.Ordinal), "the hint");
        Harness.Press("enter");
        UntilShows("Snooze");
        Harness.Press("down enter");
        Until(() => !chosen.IsEmpty, "the action");
        Assert.Equal(["due/snooze"], chosen);

        await client.DisposeAsync();
        Until(() => Front.Connections.Count == 0, "the connection to go");
        Harness.Press("ctrl+space ctrl+n");
        UntilShows("Pay rent");
        Assert.DoesNotContain("actions", RowWith("Pay rent"), StringComparison.Ordinal);
        Harness.Press("enter");
        Assert.False(Harness.App.Overlays.Any);
        Assert.Single(Shell.Notifications);
    }

    // ---- Launch ----

    [Fact]
    public void Launch_from_the_foreground_starts_the_entry_with_the_args_after_its_own()
    {
        var output = _files.Path("args");
        var entry = new AppEntry { Id = "notes", Name = "notes", Command = _files.Script($"echo \"$*\" > '{output}'; exec cat"), Args = ["--from", "entry"] };
        Start(config: WithApps(entry));
        Until(() => Shell.Foreground is not null);
        var client = Connect(Front);
        Send(client, """{"type":"launch","appId":"nope"}""");
        Send(client, """{"type":"launch","appId":"notes","args":["a b","c"]}""");
        Until(() => File.Exists(output) && File.ReadAllText(output).Length > 0, "the launch");
        Assert.Equal("--from entry a b c\n", File.ReadAllText(output));
        Assert.Equal("notes-1", Front.Id);
        Assert.Equal(2, Shell.Apps.Count);
    }

    [Fact]
    public void Launch_from_the_background_is_ignored()
    {
        var output = _files.Path("launched");
        var notes = new AppEntry { Id = "notes", Name = "notes", Command = _files.Script($"touch '{output}'; exec cat") };
        Start(config: WithApps(notes, Entry("two", "exec cat")));
        Until(() => Shell.Foreground is not null);
        var client = Connect(Front);
        Launch("two");
        var two = Front;
        Send(client, """{"type":"launch","appId":"notes"}""");
        Send(client, """{"type":"title","text":"sent"}""");
        Until(() => Shell.Apps[0].Title == "sent", "the messages");
        Thread.Sleep(100);
        Harness.RunUntilIdle();
        Assert.False(File.Exists(output));
        Assert.Same(two, Front);
    }

    // ---- Closing ----

    [Fact]
    public void Ctrl_X_asks_a_native_app_first_and_again_forces_it()
    {
        Start(config: WithApps(Entry("two", "exec cat")));
        Until(() => Shell.Foreground is not null);
        Launch("two");
        var two = Front;
        var client = Connect(two, "Notes");

        Harness.Press("ctrl+space ctrl+x");
        Until(() => client.Got("\"quitRequest\""), "the request");
        Assert.Contains("Asked Notes to quit, Ctrl+X again to force", Status, StringComparison.Ordinal);
        Assert.Contains(two, Shell.Apps);

        Harness.Press("ctrl+space ctrl+x");
        Until(() => !Shell.Apps.Contains(two), "the hangup");
        Assert.DoesNotContain("Asked", Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Switching_away_clears_the_ask_so_Ctrl_X_asks_again()
    {
        Start(config: WithApps(Entry("two", "exec cat")));
        Until(() => Shell.Foreground is not null);
        var client = Connect(Front, "Notes");
        Harness.Press("ctrl+space ctrl+x");
        Until(() => Status.Contains("Asked Notes", StringComparison.Ordinal), "the ask");

        Launch("two");
        Harness.Press("ctrl+space 1");
        Assert.DoesNotContain("Asked", Status, StringComparison.Ordinal);
        Harness.Press("ctrl+space ctrl+x");
        Until(() => client.Received.Count(l => l.Contains("quitRequest", StringComparison.Ordinal)) == 2, "the second request");
        Assert.Equal(2, Shell.Apps.Count);
    }

    [Fact]
    public void The_shells_commands_do_not_work_in_the_notifications_list()
    {
        Start("printf '\\033]9;hello\\007'; exec cat");
        Until(() => Shell.Unread == 1, "the notification");
        Harness.Press("ctrl+space ctrl+n");
        UntilShows("hello");
        Harness.Press("ctrl+x ctrl+d");
        Assert.Single(Shell.Apps);
        Assert.True(Shell.DoNotDisturb);
        Assert.IsType<AppListOverlay>(Harness.App.Overlays.Top);

        // Typing filters, as in the other lists
        Harness.Type("zq");
        UntilShows("Nothing matches");
    }

    [Fact]
    public void Quit_declined_clears_the_ask_so_Ctrl_X_asks_again()
    {
        Start();
        Until(() => Shell.Foreground is not null);
        var client = Connect(Front, "Notes");
        Harness.Press("ctrl+space ctrl+x");
        Until(() => Status.Contains("Asked Notes", StringComparison.Ordinal), "the ask");

        Send(client, """{"type":"quitDeclined"}""");
        Until(() => !Status.Contains("Asked", StringComparison.Ordinal), "the status to clear");
        Harness.Press("ctrl+space ctrl+x");
        Until(() => client.Received.Count(l => l.Contains("quitRequest", StringComparison.Ordinal)) == 2, "the second request");
        Assert.Single(Shell.Apps);
    }

    [Fact]
    public void Ctrl_Q_waits_for_native_apps_and_then_closes_the_rest()
    {
        var hungUp = _files.Path("hungup");
        Start($"trap 'touch \"{hungUp}\"; exit 0' HUP; while :; do sleep 0.01; done", WithApps(Entry("two", "exec cat")));
        Until(() => Shell.Foreground is not null);
        var plain = Front;
        Launch("two");
        var client = Connect(Front, "Notes");
        Harness.Press("ctrl+space 1");
        Assert.Same(plain, Front);

        Harness.Press("ctrl+space ctrl+q");
        UntilShows("Close and quit?");
        Harness.Press("left enter");
        Until(() => client.Got("\"quitRequest\""), "the request");
        Assert.Equal("Notes", Front.Name);
        Assert.Contains("Waiting for Notes, Ctrl+Q again to force", Status, StringComparison.Ordinal);
        Thread.Sleep(100);
        Harness.RunUntilIdle();
        Assert.False(File.Exists(hungUp));

        // Notes goes, as it would on being asked, and only then is the other app closed
        Shell.Apps[1].Terminal.Kill();
        Until(() => File.Exists(hungUp), "the hangup of the rest");
    }

    [Fact]
    public void Quit_declined_cancels_the_wait_and_every_app_keeps_running()
    {
        Start(config: WithApps(Entry("two", "exec cat")));
        Until(() => Shell.Foreground is not null);
        var client = Connect(Front, "Notes");
        Launch("two");
        BeginQuit();
        Until(() => Status.Contains("Waiting for Notes", StringComparison.Ordinal), "the wait");

        Send(client, """{"type":"quitDeclined"}""");
        Until(() => !Status.Contains("Waiting", StringComparison.Ordinal), "the status to clear");
        Assert.Equal(2, Shell.Apps.Count);

        // Ctrl+Q starts over, rather than forcing
        Harness.Press("ctrl+space ctrl+q");
        UntilShows("Close and quit?");
    }

    [Fact]
    public void Switching_to_another_app_cancels_the_wait()
    {
        Start(config: WithApps(Entry("two", "exec cat")));
        Until(() => Shell.Foreground is not null);
        Connect(Front, "Notes");
        Launch("two");
        BeginQuit();
        Until(() => Status.Contains("Waiting for Notes", StringComparison.Ordinal), "the wait");

        Harness.Press("ctrl+space 2");
        Until(() => !Status.Contains("Waiting", StringComparison.Ordinal), "the status to clear");
        Assert.Equal(2, Shell.Apps.Count);
    }

    [Fact]
    public void The_wait_ends_when_the_connection_closes_even_if_the_app_stays()
    {
        Start(config: WithApps(Entry("two", "exec cat")));
        Until(() => Shell.Foreground is not null);
        var client = Connect(Front, "Inner");
        Launch("two");
        BeginQuit();
        Until(() => Status.Contains("Waiting for Inner", StringComparison.Ordinal), "the wait");

        // As a native app inside a hosted shell exits, leaving the shell
        client.Dispose();
        Until(() => Shell.Apps.Count == 0, "everything to close");
    }

    [Fact]
    public void Ctrl_Q_again_while_waiting_forces_everything()
    {
        Start(config: WithApps(Entry("two", "exec cat")));
        Until(() => Shell.Foreground is not null);
        Connect(Front, "Notes");
        Launch("two");
        BeginQuit();
        Until(() => Status.Contains("Waiting for Notes", StringComparison.Ordinal), "the wait");

        Harness.Press("ctrl+space ctrl+q");
        Until(() => Shell.Apps.Count == 0, "everything to close");
        Assert.DoesNotContain("Close and quit?", Screen, StringComparison.Ordinal);
    }

    // Ctrl+Q and the confirmation, which other tests cover
    private void BeginQuit()
    {
        Harness.Press("ctrl+space ctrl+q");
        UntilShows("Close and quit?");
        Harness.Press("left enter");
    }

    // ---- A real client in a real child ----

    [Fact]
    public void A_native_app_shows_its_name_title_and_notifications_and_hears_its_actions()
    {
        Start(config: WithApps(Native("notes", "Notes")));
        Until(() => Shell.Foreground is not null);
        Launch("notes");
        UntilLogged("Notes", "started active=True theme=dark");
        UntilShows("Notes ready");
        Until(() => Status.Contains("Notes", StringComparison.Ordinal), "the name");

        Command("title Today's_list");
        Until(() => Status.Contains("Notes: Today's list", StringComparison.Ordinal), "the title");
        Command("notify sync high Synced 12_notes open=Open");
        Until(() => Shell.Unread == 1, "the notification");
        Assert.Equal("Notes: Synced: 12 notes", Shell.Notifications[0].Text);

        Harness.Press("ctrl+space ctrl+n enter enter");
        UntilLogged("Notes", "action sync open");

        Harness.Press("ctrl+space 1");
        UntilLogged("Notes", "focus False");
    }

    [Fact]
    public void A_native_app_with_no_handler_quits_on_Ctrl_X()
    {
        Start(config: WithApps(Native("notes", "Notes")));
        Until(() => Shell.Foreground is not null);
        Launch("notes");
        UntilReady("Notes");
        Until(() => Front.Connections.Count == 1, "the connection");

        Harness.Press("ctrl+space ctrl+x");
        UntilLogged("Notes", "exiting");
        Until(() => Shell.Apps.Count == 1, "it to close");
    }

    [Fact]
    public void A_native_app_that_declines_stays_and_the_status_clears()
    {
        Start(config: WithApps(Native("notes", "Notes")));
        Until(() => Shell.Foreground is not null);
        Launch("notes");
        UntilReady("Notes");
        Command("decline");
        UntilLogged("Notes", "ran decline");

        Harness.Press("ctrl+space ctrl+x");
        UntilLogged("Notes", "quitRequest declined");
        Until(() => !Status.Contains("Asked", StringComparison.Ordinal), "the status to clear");
        Assert.Equal(2, Shell.Apps.Count);
    }

    [Fact]
    public void A_native_app_can_launch_another_only_from_the_front()
    {
        Start(config: WithApps(Native("notes", "Notes"), Native("reader", "Reader")));
        Until(() => Shell.Foreground is not null);
        Launch("reader");
        UntilReady("Reader");
        Command("launch notes");
        UntilReady("Notes");
        Assert.Equal("notes-1", Front.Id);

        // Reader, now behind, tries again
        Harness.Press("ctrl+space 2");
        Command("later 300 launch notes");
        UntilLogged("Reader", "ran later");
        Harness.Press("ctrl+space 1");
        UntilLogged("Reader", "ran launch notes");
        Thread.Sleep(200);
        Harness.RunUntilIdle();
        Assert.Equal(3, Shell.Apps.Count);
    }

    [Fact]
    public void Ctrl_Q_waits_for_a_native_app_that_stays_and_again_forces_it()
    {
        Start(config: WithApps(Native("notes", "Notes")));
        Until(() => Shell.Foreground is not null);
        Launch("notes");
        UntilReady("Notes");
        Command("stay");
        UntilLogged("Notes", "ran stay");
        Harness.Press("ctrl+space 1");

        BeginQuit();
        UntilLogged("Notes", "quitRequest ignored");
        Until(() => Front.Name == "Notes" && Status.Contains("Waiting for Notes", StringComparison.Ordinal), "Notes in front");
        Assert.Equal(2, Shell.Apps.Count);

        Harness.Press("ctrl+space ctrl+q");
        Until(() => Shell.Apps.Count == 0, "everything to close");
    }

    [Fact]
    public void A_native_app_started_from_a_hosted_shell_speaks_for_it_until_it_exits()
    {
        Start($"'{TestApp}' Inner '{Log("Inner")}'; echo back in the shell; exec cat");
        Until(() => Shell.Foreground is not null);
        var shellName = Front.Entry.DisplayName;
        UntilReady("Inner");
        Assert.Equal("shell-1", Front.Id);
        Until(() => Status.Contains("Inner", StringComparison.Ordinal), "the inner app's name");

        Command("title inside");
        Until(() => Status.Contains("Inner: inside", StringComparison.Ordinal), "the inner title");
        Command("exit 0");
        UntilShows("back in the shell");
        Until(() => Front.Connections.Count == 0 && Status.Contains(shellName, StringComparison.Ordinal), "the shell's own name");
        Assert.Single(Shell.Apps);
    }
}

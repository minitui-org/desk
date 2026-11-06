using MiniTui.Vt;

namespace MiniTui.Desk.Tests;

/// <summary>The shell's behaviour, with a TestBackend and scripted input, hosting real children.</summary>
public sealed class ShellTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly Scripts _files = new();
    private DeskShell? _shell;
    private ComponentHarness? _harness;

    private DeskShell Shell => _shell!;
    private ComponentHarness Harness => _harness!;
    private string Status => Harness.Lines[^1];
    private string Screen => string.Join('\n', Harness.Lines);

    public void Dispose()
    {
        _shell?.Shutdown();
        _harness?.Dispose();
        _files.Dispose();
    }

    private void Start(string? shellScript = null, DeskConfig? config = null, TimeProvider? time = null)
    {
        config ??= new DeskConfig();
        config = config with { ShellCommand = _files.Script(shellScript ?? "exec cat") };
        _shell = new DeskShell(config, time);
        _harness = new ComponentHarness(_shell, 80, 24, time);
    }

    private void Until(Func<bool> condition, string? what = null)
    {
        var start = DateTime.UtcNow;
        while (true)
        {
            Harness.RunUntilIdle();
            if (condition()) return;
            Assert.True(DateTime.UtcNow - start < Timeout, $"Timed out waiting for {what ?? "the condition"}:\n{Screen}");
            Thread.Sleep(5);
        }
    }

    private void UntilShows(string text) => Until(() => Screen.Contains(text, StringComparison.Ordinal), $"\"{text}\"");

    private void Launch(string filter)
    {
        Harness.Press("ctrl+space space");
        Harness.Type(filter);
        Harness.Press("enter");
    }

    private AppEntry Entry(string name, string body) => new() { Name = name, Command = _files.Script(body) };

    private static DeskConfig WithApps(params AppEntry[] apps) => new() { Apps = apps };

    // ---- Screen ----

    [Fact]
    public void An_app_gets_every_row_but_the_status_line()
    {
        Start("stty size; exec cat");
        UntilShows("23 80");
        Assert.StartsWith("23 80", Harness.Lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void The_status_line_has_the_name_title_count_and_clock()
    {
        var time = new FixedTime(new DateTimeOffset(2026, 11, 6, 14, 5, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 6))));
        Start("printf '\\033]2;my title\\007'; exec cat", time: time);
        Until(() => Status.Contains("my title", StringComparison.Ordinal), "the title");
        Assert.Contains(": my title", Status, StringComparison.Ordinal);
        Assert.Contains("DND", Status, StringComparison.Ordinal);
        Assert.Contains("14:05", Status, StringComparison.Ordinal);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    // ---- Keys ----

    [Fact]
    public void Keys_go_to_the_app_and_the_prefix_does_not()
    {
        var output = _files.Path("out");
        Start($"stty raw -echo; exec cat > '{output}'");
        Thread.Sleep(100);
        Harness.Press("a ctrl+c ctrl+z ctrl+q");
        Harness.Press("ctrl+space esc");
        Harness.Press("b");
        Until(() => File.Exists(output) && File.ReadAllText(output).Contains('b', StringComparison.Ordinal), "the keys");
        Assert.Equal("a\u0003\u001a\u0011b", File.ReadAllText(output));
        Assert.NotNull(Shell.Foreground);
    }

    [Fact]
    public void The_prefix_twice_sends_it()
    {
        var output = _files.Path("out");
        Start($"stty raw -echo; exec cat > '{output}'");
        Thread.Sleep(100);
        Harness.Press("ctrl+space ctrl+space b");
        Until(() => File.Exists(output) && File.ReadAllText(output).Contains('b', StringComparison.Ordinal), "the keys");
        Assert.Equal("\0b", File.ReadAllText(output));
    }

    [Fact]
    public void Waiting_for_the_key_shows_the_choices_and_anything_else_cancels()
    {
        var output = _files.Path("out");
        Start($"stty raw -echo; exec cat > '{output}'");
        Thread.Sleep(100);

        Harness.Press("ctrl+space");
        Assert.Contains("Tab switch", Status, StringComparison.Ordinal);

        // The cancelling key isn't sent on
        Harness.Press("z");
        Assert.DoesNotContain("Tab switch", Status, StringComparison.Ordinal);

        Harness.Press("b");
        Until(() => File.Exists(output) && File.ReadAllText(output).Contains('b', StringComparison.Ordinal), "the key");
        Assert.Equal("b", File.ReadAllText(output));
    }

    [Fact]
    public void Another_prefix_can_be_configured()
    {
        Start(config: new DeskConfig { Prefix = KeyBinding.Parse("ctrl+b") });
        Harness.Press("ctrl+b");
        Assert.Contains("Tab switch", Status, StringComparison.Ordinal);
    }

    [Fact]
    public void A_paste_goes_to_the_app_wrapped_if_it_asked()
    {
        var output = _files.Path("out");
        Start($"stty raw -echo; printf '\\033[?2004h'; exec cat > '{output}'");
        Thread.Sleep(100);
        Harness.Paste("hi");
        Until(() => File.Exists(output) && File.ReadAllText(output).EndsWith("\u001b[201~", StringComparison.Ordinal), "the paste");
        Assert.Equal("\u001b[200~hi\u001b[201~", File.ReadAllText(output));
    }

    [Fact]
    public void The_mouse_goes_to_the_app_and_a_click_on_the_status_line_opens_the_switcher()
    {
        var output = _files.Path("out");
        Start($"stty raw -echo; printf '\\033[?1000h\\033[?1006h'; exec cat > '{output}'");
        Until(() => { lock (Shell.Foreground!.Terminal.Lock) return Shell.Foreground.Terminal.Screen.Modes.Mouse != 0; }, "mouse mode");

        Harness.Click(4, 2);
        Until(() => File.Exists(output) && File.ReadAllText(output).EndsWith('m'), "the click");
        Assert.Equal("\u001b[<0;5;3M\u001b[<0;5;3m", File.ReadAllText(output));

        Harness.Click(4, 23);
        Assert.IsType<AppListOverlay>(Harness.App.Overlays.Top);
    }

    [Fact]
    public void A_drag_that_leaves_the_app_stays_on_its_last_row()
    {
        var output = _files.Path("out");
        Start($"stty raw -echo; printf '\\033[?1002h\\033[?1006h'; exec cat > '{output}'");
        Until(() => { lock (Shell.Foreground!.Terminal.Lock) return Shell.Foreground.Terminal.Screen.Modes.Mouse != 0; }, "mouse mode");

        Harness.Mouse(new MouseEvent(MouseAction.Press, MouseButton.Left, 4, 22));
        Harness.Mouse(new MouseEvent(MouseAction.Drag, MouseButton.Left, 4, 23));
        Harness.Mouse(new MouseEvent(MouseAction.Release, MouseButton.Left, 4, 23));
        Until(() => File.Exists(output) && File.ReadAllText(output).EndsWith('m'), "the release");
        Assert.Equal("\u001b[<0;5;23M\u001b[<32;5;23M\u001b[<0;5;23m", File.ReadAllText(output));
        Assert.False(Harness.App.Overlays.Any);
    }

    [Fact]
    public void Opening_a_list_with_the_mouse_stops_waiting_for_the_key()
    {
        var output = _files.Path("out");
        Start($"stty raw -echo; exec cat > '{output}'");
        Thread.Sleep(100);
        Harness.Press("ctrl+space");
        Harness.Click(4, 23);
        Harness.Press("esc b");
        Until(() => File.Exists(output) && File.ReadAllText(output).Contains('b', StringComparison.Ordinal), "the key");
        Assert.Equal("b", File.ReadAllText(output));
    }

    // ---- Switcher and launcher ----

    [Fact]
    public void The_switcher_lists_by_recent_use_with_the_previous_app_selected()
    {
        Start("printf first; exec cat", WithApps(Entry("two", "printf second; exec cat"), Entry("three", "printf third; exec cat")));
        UntilShows("first");
        Launch("two");
        UntilShows("second");
        Launch("three");
        UntilShows("third");

        Harness.Press("ctrl+space tab");
        var list = Assert.IsType<AppListOverlay>(Harness.App.Overlays.Top);
        Assert.Equal(["three", "two", Shell.Apps[0].Name], list.Showing);

        // Enter goes back to the previous one, and doing it again returns
        Harness.Press("enter");
        Assert.Equal("two", Shell.Foreground!.Name);
        UntilShows("second");
        Harness.Press("ctrl+space tab enter");
        Assert.Equal("three", Shell.Foreground!.Name);
    }

    [Fact]
    public void The_switcher_filters_and_cannot_start_anything()
    {
        Start(config: WithApps(Entry("two", "exec cat")));
        Until(() => Shell.Foreground is not null);
        Harness.Press("ctrl+space tab");
        var list = Assert.IsType<AppListOverlay>(Harness.App.Overlays.Top);
        Assert.DoesNotContain("two", list.Showing);
        Harness.Type("zzz");
        Assert.Empty(list.Showing);
        Harness.Press("enter");
        Assert.Single(Shell.Apps);
    }

    [Fact]
    public void The_launcher_lists_the_apps_and_a_shell_and_starts_a_new_instance_each_time()
    {
        Start(config: WithApps(Entry("two", "exec cat")));
        Until(() => Shell.Foreground is not null);

        Harness.Press("ctrl+space space");
        var list = Assert.IsType<AppListOverlay>(Harness.App.Overlays.Top);
        Assert.Equal(["two", "Shell"], list.Showing);
        Harness.Press("esc");

        Launch("two");
        Launch("two");
        Launch("Shell");
        Assert.Equal(4, Shell.Apps.Count);
        Assert.Equal("Shell", Shell.Foreground!.Name);
    }

    [Fact]
    public void A_number_goes_to_that_position_and_a_missing_one_does_nothing()
    {
        Start(config: WithApps(Entry("two", "exec cat")));
        Until(() => Shell.Foreground is not null);
        var first = Shell.Foreground;
        Launch("two");
        Assert.Equal("two", Shell.Foreground!.Name);

        Harness.Press("ctrl+space 1");
        Assert.Same(first, Shell.Foreground);
        Harness.Press("ctrl+space 2");
        Assert.Equal("two", Shell.Foreground!.Name);
        Harness.Press("ctrl+space 9");
        Assert.Equal("two", Shell.Foreground!.Name);
    }

    [Fact]
    public void An_app_that_cannot_start_says_so_even_with_do_not_disturb_on()
    {
        Start(config: WithApps(new AppEntry { Name = "nope", Command = _files.Path("missing") }));
        Until(() => Shell.Foreground is not null);
        var first = Shell.Foreground;
        Launch("nope");
        UntilShows("Can't start nope");
        Assert.Same(first, Shell.Foreground);
    }

    [Fact]
    public void Switching_tells_apps_that_asked_about_focus()
    {
        var output = _files.Path("out");
        Start($"stty raw -echo; printf '\\033[?1004h'; exec cat > '{output}'", WithApps(Entry("two", "exec cat")));
        Until(() => { lock (Shell.Foreground!.Terminal.Lock) return Shell.Foreground.Terminal.Screen.Modes.FocusReporting; }, "focus mode");

        Launch("two");
        Harness.Press("ctrl+space 1");

        // It can also be told it has the focus as it starts, if it has asked by then
        Until(() => File.Exists(output) && File.ReadAllText(output).EndsWith("\u001b[O\u001b[I", StringComparison.Ordinal), "the focus reports");
    }

    // ---- Lifecycle ----

    [Theory]
    [InlineData("exit 0")]
    [InlineData("exit 130")]
    [InlineData("kill -INT $$; sleep 5")]
    public void A_clean_end_brings_the_previous_app_back(string ending)
    {
        var go = _files.Path("go");
        Start(config: WithApps(Entry("two", $"while [ ! -e '{go}' ]; do sleep 0.01; done; trap - INT; {ending}")));
        Until(() => Shell.Foreground is not null);
        var first = Shell.Foreground;
        Launch("two");
        Assert.Equal("two", Shell.Foreground!.Name);

        File.WriteAllText(go, "");
        Until(() => Shell.Apps.Count == 1, "the app to close");
        Assert.Same(first, Shell.Foreground);
        Assert.Empty(Shell.Notifications);
    }

    [Theory]
    [InlineData("exit 1", "exited 1")]
    [InlineData("kill -KILL $$", "killed by SIGKILL")]
    public void A_failure_keeps_the_screen_and_says_how_it_ended(string ending, string says)
    {
        Start($"echo last words; {ending}");
        Until(() => Status.Contains(says, StringComparison.Ordinal), says);
        Assert.Contains("r restart", Status, StringComparison.Ordinal);
        Assert.Contains("c close", Status, StringComparison.Ordinal);
        Assert.StartsWith("last words", Harness.Lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_app_restarts_with_r()
    {
        var count = _files.Path("count");
        Start($"echo run >> '{count}'; [ $(wc -l < '{count}') -gt 1 ] && {{ echo again; exec cat; }}; exit 3");
        Until(() => Status.Contains("exited 3", StringComparison.Ordinal), "the failure");

        // Other keys do nothing
        Harness.Press("z");
        Assert.Contains("exited 3", Status, StringComparison.Ordinal);

        Harness.Press("r");
        UntilShows("again");
        Assert.DoesNotContain("exited 3", Status, StringComparison.Ordinal);
        Assert.Single(Shell.Apps);
        Assert.Null(Shell.Foreground!.Failure);
    }

    [Fact]
    public void A_failed_app_closes_with_c_and_the_launcher_opens_if_it_was_the_last()
    {
        Start("exit 3");
        Until(() => Status.Contains("exited 3", StringComparison.Ordinal), "the failure");
        Harness.Press("c");
        Assert.Empty(Shell.Apps);
        Assert.IsType<AppListOverlay>(Harness.App.Overlays.Top);
    }

    [Fact]
    public void The_last_app_closing_opens_the_launcher_and_Desk_keeps_running()
    {
        var go = _files.Path("go");
        Start($"while [ ! -e '{go}' ]; do sleep 0.01; done; exit 0", WithApps(Entry("two", "exec cat")));
        Until(() => Shell.Foreground is not null);
        File.WriteAllText(go, "");
        Until(() => Harness.App.Overlays.Top is AppListOverlay, "the launcher");
        Assert.Empty(Shell.Apps);

        Harness.Press("esc");
        UntilShows("No apps");

        Launch("two");
        Assert.Equal("two", Assert.Single(Shell.Apps).Name);
    }

    [Fact]
    public void Close_hangs_up_and_shows_the_previous_app()
    {
        var hung = _files.Path("hung");
        Start(config: WithApps(Entry("two", $"trap 'echo > \"{hung}\"; exit 0' HUP; while :; do sleep 0.01; done")));
        Until(() => Shell.Foreground is not null);
        var first = Shell.Foreground;
        Launch("two");
        Thread.Sleep(100);

        Harness.Press("ctrl+space x");
        Assert.Same(first, Shell.Foreground);
        Assert.Single(Shell.Apps);
        Until(() => File.Exists(hung), "the hangup");
    }

    [Fact]
    public void An_app_that_ignores_the_hangup_is_killed()
    {
        var entry = Entry("two", "trap '' HUP; while :; do sleep 0.01; done");
        Start(config: WithApps(entry) with { CloseTimeout = TimeSpan.FromMilliseconds(100) });
        Until(() => Shell.Foreground is not null);
        Launch("two");
        Thread.Sleep(100);
        var pid = Shell.Foreground!.Terminal.Pid;

        Harness.Press("ctrl+space x");
        Until(() => !File.Exists($"/proc/{pid}/cmdline") || File.ReadAllText($"/proc/{pid}/cmdline").Length == 0, "the kill");
    }

    [Fact]
    public void Quit_asks_while_apps_are_running_and_defaults_to_staying()
    {
        Start();
        Until(() => Shell.Foreground is not null);
        Harness.Press("ctrl+space q");
        Assert.IsType<ConfirmDialog>(Harness.App.Overlays.Top);
        Harness.Press("enter");
        Assert.False(Harness.App.Overlays.Any);
        Assert.Single(Shell.Apps);
    }

    [Fact]
    public void Ctrl_C_in_a_list_closes_the_list_and_not_Desk()
    {
        Start();
        Until(() => Shell.Foreground is not null);
        Harness.Press("ctrl+space tab");
        Harness.Press("ctrl+c");
        Assert.False(Harness.App.Overlays.Any);
        Assert.False(Harness.App.Stopping.IsCancellationRequested);
        Assert.Single(Shell.Apps);
    }

    [Fact]
    public void Help_shows_the_keys()
    {
        Start();
        Until(() => Shell.Foreground is not null);
        Harness.Press("ctrl+space ?");
        UntilShows("Switch to a running app");
    }

    // ---- Notifications ----

    [Fact]
    public void A_background_failure_is_a_notification_and_stays_in_the_switcher()
    {
        var go = _files.Path("go");
        Start($"while [ ! -e '{go}' ]; do sleep 0.01; done; exit 4", WithApps(Entry("two", "exec cat")));
        Until(() => Shell.Foreground is not null);
        var first = Shell.Foreground;
        Launch("two");

        File.WriteAllText(go, "");
        Until(() => Shell.Notifications.Count == 1, "the notification");
        Assert.Contains("exited 4", Shell.Notifications[0].Text, StringComparison.Ordinal);
        Assert.Contains("1 new", Status, StringComparison.Ordinal);
        Assert.Equal(2, Shell.Apps.Count);
        Assert.NotNull(first!.Failure);
    }

    [Fact]
    public void A_foreground_failure_is_not_a_notification()
    {
        Start("exit 4");
        Until(() => Status.Contains("exited 4", StringComparison.Ordinal), "the failure");
        Assert.Empty(Shell.Notifications);
    }

    [Fact]
    public void A_bell_only_counts_from_the_background_and_once_until_the_user_returns()
    {
        var ring = _files.Path("ring");
        Start($"while :; do [ -e '{ring}' ] && {{ printf '\\007\\007'; rm '{ring}'; }}; sleep 0.01; done", WithApps(Entry("two", "exec cat")));
        Until(() => Shell.Foreground is not null);

        File.WriteAllText(ring, "");
        Until(() => !File.Exists(ring), "the bell");
        Thread.Sleep(100);
        Harness.RunUntilIdle();
        Assert.Empty(Shell.Notifications);

        Launch("two");
        File.WriteAllText(ring, "");
        Until(() => Shell.Notifications.Count > 0, "the notification");
        File.WriteAllText(ring, "");
        Until(() => !File.Exists(ring), "the second bell");
        Thread.Sleep(100);
        Harness.RunUntilIdle();
        var only = Assert.Single(Shell.Notifications);
        Assert.Contains("wants attention", only.Text, StringComparison.Ordinal);

        // Going there and leaving again means the next one counts
        Harness.Press("ctrl+space 1");
        Harness.Press("ctrl+space 2");
        File.WriteAllText(ring, "");
        Until(() => Shell.Notifications.Count == 2, "another notification");
    }

    [Theory]
    [InlineData("\\033]9;hello\\007", "hello")]
    [InlineData("\\033]777;notify;build;done\\007", "build: done")]
    public void Osc_notifications_count_quietly_and_are_listed(string sequence, string expected)
    {
        Start($"printf '{sequence}'; exec cat");
        Until(() => Shell.Unread == 1, "the notification");
        Assert.Contains(expected, Shell.Notifications[0].Text, StringComparison.Ordinal);
        Assert.Equal(0, Harness.App.Toasts.Count);

        Harness.Press("ctrl+space n");
        UntilShows(expected);
        Assert.Equal(0, Shell.Unread);
        Assert.Contains("Notifications", Screen, StringComparison.Ordinal);
    }

    [Fact]
    public void With_do_not_disturb_off_a_notification_is_also_a_toast()
    {
        var go = _files.Path("go");
        Start($"while [ ! -e '{go}' ]; do sleep 0.01; done; printf '\\033]9;hello\\007'; exec cat");
        Until(() => Shell.Foreground is not null);
        Assert.True(Shell.DoNotDisturb);
        Harness.Press("ctrl+space d");
        Assert.False(Shell.DoNotDisturb);
        Assert.DoesNotContain("DND", Status, StringComparison.Ordinal);

        File.WriteAllText(go, "");
        Until(() => Harness.App.Toasts.Count == 1, "the toast");
    }

    [Fact]
    public void The_queue_keeps_the_newest_hundred()
    {
        Start("i=0; while [ $i -lt 105 ]; do printf '\\033]9;n%d\\007' $i; i=$((i+1)); done; exec cat");
        Until(() => Shell.Notifications.Count == 100 && Shell.Notifications[^1].Text.EndsWith("n104", StringComparison.Ordinal), "the notifications");
        Assert.EndsWith("n5", Shell.Notifications[0].Text, StringComparison.Ordinal);
        Assert.Equal(100, Shell.Unread);
    }

    [Fact]
    public void A_notification_cannot_put_control_characters_on_the_screen()
    {
        Start("printf '\\033]9;a\\tb\\007'; exec cat");
        Until(() => Shell.Unread == 1, "the notification");
        Assert.EndsWith("ab", Shell.Notifications[0].Text, StringComparison.Ordinal);
    }

    // ---- Config ----

    [Fact]
    public void Broken_files_are_ignored_and_reported()
    {
        File.WriteAllText(_files.Path("config.json"), "{ nope");
        File.WriteAllText(_files.Path("apps.json"), """[{ "name": "no command" }]""");
        var config = DeskConfig.Load(_files.Directory);
        Start(config: config);
        Until(() => Shell.Foreground is not null);

        Assert.Equal(2, Shell.Notifications.Count);
        Assert.Contains("config.json ignored", Shell.Notifications[0].Text, StringComparison.Ordinal);
        Assert.Contains("apps.json ignored", Shell.Notifications[1].Text, StringComparison.Ordinal);
        Assert.True(Shell.DoNotDisturb);
    }

    [Fact]
    public void A_prefix_that_is_not_a_key_is_reported()
    {
        File.WriteAllText(_files.Path("config.json"), """{ "prefix": "ctrl+nope", "doNotDisturb": false }""");
        var config = DeskConfig.Load(_files.Directory);
        Assert.Contains("isn't a key", Assert.Single(config.Errors), StringComparison.Ordinal);
        Assert.True(config.DoNotDisturb);
    }

    [Fact]
    public void Config_files_are_optional_and_read_when_present()
    {
        var none = DeskConfig.Load(_files.Path("missing"));
        Assert.Empty(none.Errors);
        Assert.Empty(none.Apps);
        Assert.True(none.DoNotDisturb);

        File.WriteAllText(_files.Path("config.json"), """{ "prefix": "ctrl+b", "doNotDisturb": false }""");
        File.WriteAllText(_files.Path("apps.json"), """[{ "id": "k", "name": "Kube", "command": "k9s", "args": ["-c", "pods"], "cwd": "~/x", "env": { "A": "1" } }]""");
        var config = DeskConfig.Load(_files.Directory);
        Assert.Empty(config.Errors);
        Assert.Equal("ctrl+b", config.Prefix.ToString());
        Assert.False(config.DoNotDisturb);

        var entry = Assert.Single(config.Apps);
        var info = DeskConfig.StartInfo(entry, 10, 5);
        Assert.Equal("k9s", info.FileName);
        Assert.Equal(["-c", "pods"], info.Args);
        Assert.Equal(Path.Combine(Environment.GetEnvironmentVariable("HOME")!, "x"), info.WorkingDirectory);
        Assert.Equal("1", info.Environment["A"]);
        Assert.Equal("tuidesk", info.Environment["TERM_PROGRAM"]);
        Assert.Equal((10, 5), (info.Width, info.Height));
    }

    [Fact]
    public void Hosted_apps_get_the_documented_environment()
    {
        Start("echo \"$TERM $COLORTERM $TERM_PROGRAM\"; exec cat");
        UntilShows("xterm-256color truecolor tuidesk");
    }

    [Fact]
    public void A_shell_that_cannot_start_falls_back()
    {
        var config = new DeskConfig { ShellCommand = _files.Path("missing") };
        _shell = new DeskShell(config);
        _harness = new ComponentHarness(_shell, 80, 24);
        Until(() => Shell.Foreground is not null);
        Assert.Equal("sh", Shell.Foreground!.Name);
    }
}

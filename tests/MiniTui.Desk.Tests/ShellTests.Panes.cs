namespace MiniTui.Desk.Tests;

/// <summary>Two panes (spec §14): a second app beside or below the first.</summary>
public sealed partial class ShellTests
{
    private static (int Width, int Height) SizeOf(HostedApp app)
    {
        lock (app.Terminal.Lock) return (app.Terminal.Screen.Width, app.Terminal.Screen.Height);
    }

    // The first app, and "two" and "three" to start; each prints its name and then echoes what it's sent
    private void StartThree(int width = 80, int height = 24) => Start(
        "printf first; exec cat",
        WithApps(Entry("two", "stty size; printf second; exec cat"), Entry("three", "printf third; exec cat")),
        width: width,
        height: height);

    private void Beside(string filter, string key)
    {
        Harness.Press("ctrl+space");
        Harness.Type(filter);
        Harness.Press(key);
    }

    [Fact]
    public void Ctrl_r_in_the_launcher_starts_the_app_to_the_right_at_its_panes_size()
    {
        StartThree();
        UntilShows("first");
        Beside("two", "ctrl+r");
        UntilShows("second");

        Assert.Equal("two", Shell.Foreground!.Name);
        Assert.Same(Shell.Apps[0], Shell.Other);
        Assert.False(Shell.Stacked);
        // 80 columns less the divider: the first pane gets the odd one. The app started at that size.
        Assert.Equal((40, 23), SizeOf(Shell.Apps[0]));
        Assert.Equal((39, 23), SizeOf(Shell.Apps[1]));
        Assert.StartsWith("23 39", Harness.Lines[0][41..], StringComparison.Ordinal);
        Assert.StartsWith("first", Harness.Lines[0], StringComparison.Ordinal);
        Assert.All(Enumerable.Range(0, 23), y => Assert.Equal('│', Harness.Lines[y][40]));
    }

    [Fact]
    public void Ctrl_b_puts_it_below()
    {
        StartThree();
        UntilShows("first");
        Beside("two", "ctrl+b");
        UntilShows("second");

        Assert.True(Shell.Stacked);
        // 23 rows less the divider: 11 each
        Assert.Equal((80, 11), SizeOf(Shell.Apps[0]));
        Assert.Equal((80, 11), SizeOf(Shell.Apps[1]));
        Assert.StartsWith("first", Harness.Lines[0], StringComparison.Ordinal);
        Assert.Equal(new string('─', 80), Harness.Lines[11]);
        Assert.StartsWith("11 80", Harness.Lines[12], StringComparison.Ordinal);
    }

    [Fact]
    public void Keys_go_to_the_focused_pane_and_alt_j_goes_to_the_other()
    {
        StartThree();
        UntilShows("first");
        Beside("two", "ctrl+r");
        UntilShows("second");

        Harness.Type("R");
        Until(() => Harness.Lines[1][41..].StartsWith("secondR", StringComparison.Ordinal) || Harness.Lines[0][41..].Contains("secondR", StringComparison.Ordinal), "the key on the right");
        Harness.Press("alt+j");
        Assert.Same(Shell.Apps[0], Shell.Foreground);
        Assert.Equal("two", Shell.Other!.Name);
        Harness.Type("L");
        Until(() => Harness.Lines[0].StartsWith("firstL", StringComparison.Ordinal), "the key on the left");
        Assert.DoesNotContain("L", Harness.Lines[0][41..], StringComparison.Ordinal);

        Harness.Press("alt+j");
        Assert.Equal("two", Shell.Foreground!.Name);
    }

    [Fact]
    public void With_one_pane_alt_j_does_nothing_and_the_app_does_not_get_it()
    {
        var output = _files.Path("out");
        Start($"stty raw -echo; exec cat > '{output}'");
        Thread.Sleep(100);
        Harness.Press("alt+j b");
        Until(() => File.Exists(output) && File.ReadAllText(output).Contains('b', StringComparison.Ordinal), "the keys");
        Assert.Equal("b", File.ReadAllText(output));
    }

    [Fact]
    public void Alt_l_and_alt_h_change_the_focused_pane_and_skip_the_other_panes_app()
    {
        StartThree();
        UntilShows("first");
        Launch("three");
        UntilShows("third");
        Beside("two", "ctrl+r"); // three | two, with two focused
        UntilShows("second");

        Harness.Press("alt+l");
        Assert.Same(Shell.Apps[0], Shell.Foreground); // from two, past three which is showing, around to the first
        Assert.Equal("three", Shell.Other!.Name);
        Assert.Equal((39, 23), SizeOf(Shell.Apps[0]));
        Assert.Equal((80, 23), SizeOf(Shell.Apps[2]));  // two is out of sight, at the whole screen's size
        Harness.Press("alt+h");
        Assert.Equal("two", Shell.Foreground!.Name);

        // With only the two, there's nowhere to go
        Harness.Press("alt+j ctrl+space ctrl+x");
        Until(() => Shell.Apps.Count == 2, "three to close");
        Beside("two", "ctrl+r");
        Harness.Press("ctrl+space tab");
        var before = Shell.Foreground;
        Harness.Press("esc alt+l alt+h");
        Assert.Same(before, Shell.Foreground);
    }

    [Fact]
    public void Choosing_the_other_panes_app_moves_the_focus_to_it()
    {
        StartThree();
        UntilShows("first");
        Beside("two", "ctrl+r");
        UntilShows("second");

        Harness.Press("ctrl+space 1");
        Assert.Same(Shell.Apps[0], Shell.Foreground);
        Assert.Equal("two", Shell.Other!.Name);
        Assert.Equal((40, 23), SizeOf(Shell.Apps[0])); // and neither moved

        Harness.Press("ctrl+space tab enter"); // the switcher's previous app
        Assert.Equal("two", Shell.Foreground!.Name);
        Assert.Same(Shell.Apps[0], Shell.Other);
    }

    [Fact]
    public void Enter_on_another_app_replaces_the_focused_pane_and_keeps_the_other()
    {
        StartThree();
        UntilShows("first");
        Beside("two", "ctrl+r");
        UntilShows("second");
        Launch("three");
        UntilShows("third");

        Assert.Equal("three", Shell.Foreground!.Name);
        Assert.Same(Shell.Apps[0], Shell.Other);
        Assert.Equal((39, 23), SizeOf(Shell.Foreground));
        Assert.StartsWith("third", Harness.Lines[0][41..], StringComparison.Ordinal);
    }

    [Fact]
    public void Ctrl_r_in_the_switcher_brings_a_running_app_beside_but_not_beside_itself()
    {
        StartThree();
        UntilShows("first");
        Launch("two");
        UntilShows("second");

        Harness.Press("ctrl+space tab"); // the switcher, on the previous app: the first
        Harness.Press("ctrl+r");
        Assert.Same(Shell.Apps[0], Shell.Foreground);
        Assert.Equal("two", Shell.Other!.Name);
        Assert.Equal((40, 23), SizeOf(Shell.Apps[1])); // two kept the left, and the first is on the right
        Assert.Equal((39, 23), SizeOf(Shell.Apps[0]));

        Harness.Press("ctrl+space ctrl+o");
        Assert.Null(Shell.Other);
        Harness.Press("ctrl+space tab up ctrl+r"); // the focused app's own row
        Assert.Null(Shell.Other);
    }

    [Fact]
    public void Ctrl_b_on_a_split_turns_it_the_other_way()
    {
        StartThree();
        UntilShows("first");
        Beside("two", "ctrl+r");
        UntilShows("second");
        Harness.Press("ctrl+space tab ctrl+b"); // the first app, below two
        Assert.True(Shell.Stacked);
        Assert.Same(Shell.Apps[0], Shell.Foreground);
        Assert.Equal((80, 11), SizeOf(Shell.Apps[0]));
        Assert.Equal((80, 11), SizeOf(Shell.Apps[1]));
    }

    [Fact]
    public void Ctrl_o_goes_back_to_one_pane_and_the_other_app_keeps_running()
    {
        StartThree();
        UntilShows("first");
        Beside("two", "ctrl+r");
        UntilShows("second");
        Harness.Press("ctrl+space ctrl+o");

        Assert.Null(Shell.Other);
        Assert.Equal("two", Shell.Foreground!.Name);
        Assert.Equal(2, Shell.Apps.Count);
        Assert.Equal((80, 23), SizeOf(Shell.Apps[0]));
        Assert.Equal((80, 23), SizeOf(Shell.Apps[1]));
        Assert.DoesNotContain('│', Harness.Lines[5]);
    }

    [Fact]
    public void Closing_the_focused_app_leaves_the_other_full_screen()
    {
        StartThree();
        UntilShows("first");
        Launch("three");
        Beside("two", "ctrl+r"); // three | two
        UntilShows("second");
        Harness.Press("ctrl+space ctrl+x");
        Until(() => Shell.Apps.Count == 2, "two to close");

        Assert.Equal("three", Shell.Foreground!.Name); // the one that was beside it, not the most recent
        Assert.Null(Shell.Other);
        Assert.Equal((80, 23), SizeOf(Shell.Foreground));
    }

    [Fact]
    public void The_other_panes_app_ending_leaves_the_focused_one_full_screen()
    {
        Start("printf first; exec cat", WithApps(Entry("brief", "printf brief; read line")));
        UntilShows("first");
        Beside("brief", "ctrl+r");
        UntilShows("brief");
        Harness.Press("enter"); // read returns, and it exits 0
        Until(() => Shell.Apps.Count == 1, "it to end");
        Assert.Null(Shell.Other);
        Assert.Same(Shell.Apps[0], Shell.Foreground);
        Assert.Equal((80, 23), SizeOf(Shell.Apps[0]));
    }

    [Fact]
    public void A_screen_too_small_for_two_panes_shows_the_focused_one()
    {
        StartThree(width: 40); // 39 columns for two panes of 20
        UntilShows("first");
        Beside("two", "ctrl+r");
        UntilShows("second");

        Assert.NotNull(Shell.Other); // remembered, for when there's room
        Assert.Equal((40, 23), SizeOf(Shell.Foreground!));
        Assert.DoesNotContain("first", Screen, StringComparison.Ordinal);
        Harness.Press("alt+j");
        UntilShows("first");
        Assert.DoesNotContain("second", Screen, StringComparison.Ordinal);

        // Stacked needs 5 rows each
        _harness!.Dispose();
        _shell!.Shutdown();
        StartThree(height: 11);
        UntilShows("first");
        Beside("two", "ctrl+b");
        UntilShows("second");
        Assert.Equal((80, 10), SizeOf(Shell.Foreground!));
    }

    [Fact]
    public void The_status_line_marks_the_other_panes_app()
    {
        StartThree();
        UntilShows("first");
        Launch("three");
        Beside("two", "ctrl+r"); // three | two
        UntilShows("second");

        var theme = Theme.Current;
        var two = Status.IndexOf("3 two", StringComparison.Ordinal);
        var three = Status.IndexOf("2 three", StringComparison.Ordinal);
        var first = Status.IndexOf("1 ", StringComparison.Ordinal);
        Assert.Equal(theme.Selection.Bg, Harness.CellAt(two, 23).Bg);
        Assert.True(Harness.CellAt(three, 23).Modifiers.HasFlag(Modifiers.Bold | Modifiers.Underlined));
        Assert.False(Harness.CellAt(three - 1, 23).Modifiers.HasFlag(Modifiers.Underlined)); // not its padding
        Assert.False(Harness.CellAt(first, 23).Modifiers.HasFlag(Modifiers.Underlined));
        Assert.NotEqual(theme.Selection.Bg, Harness.CellAt(first, 23).Bg);
    }

    [Fact]
    public void The_footer_and_help_list_the_pane_keys()
    {
        Start();
        Until(() => Shell.Foreground is not null);
        Harness.Press("ctrl+space");
        foreach (var key in new[] { "Ctrl+R right", "Ctrl+B below", "Ctrl+O one pane" }) Assert.Contains(key, Screen, StringComparison.Ordinal);
        Harness.Press("f1");
        Assert.Contains("Alt+J", Screen, StringComparison.Ordinal);
        Assert.Contains("pane to the right", Screen, StringComparison.Ordinal);
    }

    [Fact]
    public void The_key_for_the_other_pane_can_be_set_or_turned_off()
    {
        Assert.Equal(KeyBinding.Parse("alt+j"), new DeskConfig().OtherPane);
        File.WriteAllText(_files.Path("config.json"), """{ "otherPane": "alt+o" }""");
        Assert.Equal(KeyBinding.Parse("alt+o"), DeskConfig.Load(_files.Directory).OtherPane);
        File.WriteAllText(_files.Path("config.json"), """{ "otherPane": "" }""");
        Assert.Null(DeskConfig.Load(_files.Directory).OtherPane);
    }

    // ---- Mouse (spec §14.6) ----

    // The first app on the left and "two" focused on the right, each in button-event mouse mode, writing what it gets
    private (string Left, string Right) SplitWithMouse()
    {
        var left = _files.Path("left");
        var right = _files.Path("right");
        const string Mouse = "stty raw -echo; printf '\\033[?1002h\\033[?1006h'; exec cat > ";
        Start($"{Mouse}'{left}'", WithApps(Entry("two", $"{Mouse}'{right}'")));
        Until(() => Shell.Foreground is not null);
        Beside("two", "ctrl+r");
        Until(() => Shell.Apps.Count == 2);
        Until(() => Shell.Apps.All(a => { lock (a.Terminal.Lock) return a.Terminal.Screen.Modes.Mouse != 0; }), "mouse mode");
        return (left, right);
    }

    private static string Got(string path) => File.Exists(path) ? File.ReadAllText(path) : "";

    [Fact]
    public void A_press_in_the_other_pane_focuses_it_and_reaches_its_app_in_its_own_coordinates()
    {
        var (left, right) = SplitWithMouse();

        Harness.Click(4, 2);
        Assert.Same(Shell.Apps[0], Shell.Foreground);
        Until(() => Got(left).EndsWith('m'), "the click");
        Assert.Equal("\u001b[<0;5;3M\u001b[<0;5;3m", Got(left));

        // The right pane starts after the left's 40 columns and the divider
        Harness.Click(45, 2);
        Assert.Equal("two", Shell.Foreground!.Name);
        Until(() => Got(right).EndsWith('m'), "the click");
        Assert.Equal("\u001b[<0;5;3M\u001b[<0;5;3m", Got(right));
    }

    [Fact]
    public void The_wheel_goes_to_the_pane_under_it_without_moving_the_focus()
    {
        var (left, _) = SplitWithMouse();

        Harness.Mouse(new MouseEvent(MouseAction.ScrollUp, MouseButton.None, 4, 2));
        Until(() => Got(left).EndsWith('M'), "the wheel");
        Assert.Equal("\u001b[<64;5;3M", Got(left));
        Assert.Equal("two", Shell.Foreground!.Name);
    }

    [Fact]
    public void A_drag_stays_with_the_pane_it_started_in()
    {
        var (left, right) = SplitWithMouse();

        Harness.Mouse(new MouseEvent(MouseAction.Press, MouseButton.Left, 45, 2));
        Harness.Mouse(new MouseEvent(MouseAction.Drag, MouseButton.Left, 10, 2));
        Harness.Mouse(new MouseEvent(MouseAction.Release, MouseButton.Left, 10, 2));
        Until(() => Got(right).EndsWith('m'), "the release");
        // Held to the right pane's first column
        Assert.Equal("\u001b[<0;5;3M\u001b[<32;1;3M\u001b[<0;1;3m", Got(right));
        Assert.Equal("", Got(left));
        Assert.Equal("two", Shell.Foreground!.Name);
    }

    [Fact]
    public void A_press_on_the_divider_does_nothing()
    {
        var (left, right) = SplitWithMouse();

        Harness.Click(40, 2);
        Harness.Mouse(new MouseEvent(MouseAction.ScrollUp, MouseButton.None, 40, 2));
        Thread.Sleep(100);
        Harness.RunUntilIdle();
        Assert.Equal("two", Shell.Foreground!.Name);
        Assert.Equal("", Got(left));
        Assert.Equal("", Got(right));
    }

    // ---- The protocol (spec §14.7) ----

    [Fact]
    public void Focus_follows_the_focused_pane_with_a_client_in_each()
    {
        Start(config: WithApps(Entry("two", "exec cat")));
        Until(() => Shell.Foreground is not null);
        var first = Client(Front, "First");
        Beside("two", "ctrl+r");
        Until(() => Shell.Apps.Count == 2);
        var two = Client(Front, "Two");
        Until(() => !first.IsFocused, "the first to lose the focus");
        Assert.True(two.IsFocused);

        Harness.Press("alt+j");
        Until(() => first.IsFocused && !two.IsFocused, "the focus to move left");

        Harness.Click(45, 2);
        Until(() => two.IsFocused && !first.IsFocused, "the focus to move right");
    }

    [Fact]
    public void A_launch_while_split_takes_the_other_pane_and_the_asker_stays_in_sight()
    {
        var notes = new AppEntry { Id = "notes", Name = "notes", Command = _files.Script("stty size; exec cat") };
        Start("printf first; exec cat", WithApps(notes, Entry("two", "exec cat")));
        UntilShows("first");
        Beside("two", "ctrl+r");
        Until(() => Shell.Apps.Count == 2);
        var two = Front;
        var client = Connect(two);

        Send(client, """{"type":"launch","appId":"notes"}""");
        Until(() => Shell.Apps.Count == 3, "the launch");
        Harness.RunUntilIdle();
        Assert.Equal("notes-1", Front.Id);
        Assert.Same(two, Shell.Other);
        // In the left pane, where the first app was, at that pane's size
        UntilShows("23 40");
        Assert.StartsWith("23 40", Harness.Lines[0], StringComparison.Ordinal);
        Assert.Equal((80, 23), SizeOf(Shell.Apps[0]));
    }
}

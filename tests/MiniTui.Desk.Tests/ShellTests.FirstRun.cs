namespace MiniTui.Desk.Tests;

/// <summary>Finding your way in (spec §15): the hint, the welcome and the state that remembers them.</summary>
public sealed partial class ShellTests
{
    private string StatePath => _files.Path("state/state.json");

    private void StartFresh(DeskConfig? config = null, int width = 80) =>
        Start(config: (config ?? new DeskConfig()) with { StatePath = StatePath }, width: width);

    // A second run of Desk, after the first has ended
    private void Restart(int width = 80)
    {
        DisposeClients();
        _shell?.Shutdown();
        _harness?.Dispose();
        StartFresh(width: width);
    }

    private void UntilWelcomed() => Until(() => Harness.App.Overlays.Any, "the welcome");

    [Fact]
    public void The_first_start_welcomes_with_the_keys_as_configured_and_only_once()
    {
        var config = new DeskConfig { Prefix = KeyBinding.Parse("ctrl+a"), NextApp = KeyBinding.Parse("alt+n"), ConfigDirectory = _files.Path("conf") };
        StartFresh(config);
        UntilWelcomed();
        Assert.Contains("Welcome to Desk", Screen, StringComparison.Ordinal);
        Assert.Contains("Ctrl+A", Screen, StringComparison.Ordinal);
        Assert.Contains("Alt+H Alt+N", Screen, StringComparison.Ordinal);
        Assert.Contains("Alt+J", Screen, StringComparison.Ordinal);
        Assert.Contains($"{_files.Path("conf")}/config.json and apps.json", Screen, StringComparison.Ordinal);

        Harness.Press("esc");
        Assert.False(Harness.App.Overlays.Any);
        Assert.Contains("\"welcomed\":true", File.ReadAllText(StatePath), StringComparison.Ordinal);

        Restart();
        Until(() => Shell.Foreground is not null);
        Thread.Sleep(50);
        Harness.RunUntilIdle();
        Assert.False(Harness.App.Overlays.Any);
    }

    [Fact]
    public void Enter_closes_the_welcome_too()
    {
        StartFresh();
        UntilWelcomed();
        Harness.Press("enter");
        Assert.False(Harness.App.Overlays.Any);
    }

    [Fact]
    public void The_hint_shows_until_the_prefix_is_pressed_and_not_after_a_restart()
    {
        StartFresh();
        UntilWelcomed();
        Harness.Press("esc");
        Harness.RunUntilIdle();
        Assert.Contains("Ctrl+Space menu", Status, StringComparison.Ordinal);

        // A click on the status line opens the switcher, which isn't finding the prefix
        Harness.Click(70, 23);
        Assert.IsType<AppListOverlay>(Harness.App.Overlays.Top);
        Harness.Press("esc");
        Harness.RunUntilIdle();
        Assert.Contains("Ctrl+Space menu", Status, StringComparison.Ordinal);

        Harness.Press("ctrl+space");
        Harness.Press("esc");
        Harness.RunUntilIdle();
        Assert.DoesNotContain("menu", Status, StringComparison.Ordinal);

        Restart();
        Until(() => Shell.Foreground is not null);
        Harness.RunUntilIdle();
        Assert.DoesNotContain("menu", Status, StringComparison.Ordinal);
    }

    [Fact]
    public void On_a_narrow_screen_the_hint_goes_before_the_apps_names()
    {
        Start(config: WithApps(Entry("second-app", "exec cat")) with { StatePath = StatePath }, width: 50);
        UntilWelcomed();
        Harness.Press("esc");
        Harness.RunUntilIdle();
        Assert.Contains("Ctrl+Space menu", Status, StringComparison.Ordinal);

        Launch("second");
        Until(() => Shell.Apps.Count == 2);
        Harness.RunUntilIdle();
        Assert.DoesNotContain("menu", Status, StringComparison.Ordinal);
        Assert.Contains("2 second-app", Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_lists_the_config_paths()
    {
        Start(config: new DeskConfig { ConfigDirectory = _files.Path("conf") });
        Until(() => Shell.Foreground is not null);
        Harness.Press("ctrl+space f1");
        Until(() => Harness.App.Overlays.Any, "help");
        Harness.Press("end");
        Until(() => Screen.Contains("Settings:", StringComparison.Ordinal), "the end of help");
        Assert.Contains($"{_files.Path("conf")}/config.json and apps.json", Screen, StringComparison.Ordinal);
    }

    [Fact]
    public void A_broken_state_file_is_nothing_seen()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, "{ not json");
        StartFresh();
        UntilWelcomed();
        Harness.Press("esc");
        Assert.Contains("Ctrl+Space menu", Status, StringComparison.Ordinal);
        Assert.Contains("\"welcomed\":true", File.ReadAllText(StatePath), StringComparison.Ordinal);
    }

    [Fact]
    public void A_state_file_that_cant_be_written_doesnt_stop_desk()
    {
        // Its folder is a file, so neither the folder nor the file can be made
        File.WriteAllText(_files.Path("state"), "");
        StartFresh();
        UntilWelcomed();
        Harness.Press("esc");
        Harness.Press("ctrl+space");
        Harness.Press("esc");
        Harness.RunUntilIdle();
        Assert.NotNull(Shell.Foreground);
        Assert.DoesNotContain("menu", Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_state_path_nothing_is_shown()
    {
        Start();
        Until(() => Shell.Foreground is not null);
        Thread.Sleep(50);
        Harness.RunUntilIdle();
        Assert.False(Harness.App.Overlays.Any);
        Assert.DoesNotContain("menu", Status, StringComparison.Ordinal);
    }
}

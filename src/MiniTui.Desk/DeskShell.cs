using System.ComponentModel;
using System.Globalization;
using MiniTui.Vt;

namespace MiniTui.Desk;

/// <summary>
/// The workspace (spec §12): one app on the screen, one row of status, and one key taken from the app, the prefix.
/// Everything here runs on the UI thread, apart from the handlers that <see cref="Start"/> adds to a terminal.
/// </summary>
internal sealed class DeskShell(DeskConfig config, TimeProvider? time = null) : Screen
{
    private const int MaxNotifications = 100;
    private static readonly TimeSpan GraceAfterKill = TimeSpan.FromSeconds(1);
    private static readonly KeyBinding CloseKey = KeyBinding.Parse("ctrl+x");
    private static readonly KeyBinding QuitKey = KeyBinding.Parse("ctrl+q");
    private static readonly KeyBinding NotificationsKey = KeyBinding.Parse("ctrl+n");
    private static readonly KeyBinding DoNotDisturbKey = KeyBinding.Parse("ctrl+d");
    private static readonly KeyBinding SendPrefixKey = KeyBinding.Parse("ctrl+s");
    private static readonly KeyBinding HelpKey = KeyBinding.Parse("f1");

    private readonly List<HostedApp> _apps = [];     // positions, for 1-9
    private readonly List<HostedApp> _recent = [];   // most recently used first
    private readonly List<VtTerminal> _closing = [];
    private readonly List<Notification> _notifications = []; // oldest first
    private volatile HostedApp? _foreground;
    private Rect _pane;
    private bool _started;
    private bool _quitting;
    private bool _doNotDisturb = config.DoNotDisturb;
    private int _unread;
    private Color? _defaultForeground;
    private Color? _defaultBackground;

    internal readonly record struct Notification(DateTimeOffset Time, string Text);

    public override string Title => "Desk";
    public override bool PopOnEscape => false;

    internal IReadOnlyList<HostedApp> Apps => _apps;
    internal HostedApp? Foreground => _foreground;
    internal IReadOnlyList<Notification> Notifications => _notifications;
    internal int Unread => _unread;
    internal bool DoNotDisturb => _doNotDisturb;

    /// <summary>End whatever is left. For when the app has stopped, however that happened.</summary>
    public void Shutdown()
    {
        foreach (var terminal in _apps.Select(a => a.Terminal).Concat(_closing))
        {
            terminal.Kill();
            terminal.Dispose();
        }
        _apps.Clear();
        _recent.Clear();
        _closing.Clear();
        _foreground = null;
    }

    protected override void OnMount()
    {
        App.WithChrome(statusBar: false, breadcrumb: false);
        App.OnKey(OnKey);
        App.OnPaste(OnPaste);
        App.OnMouse(OnMouse);
        App.OnEvent(OnEvent);
        foreach (var error in config.Errors) Notify(error);
    }

    public override void Render(CellBuffer buf, Rect area, RenderContext ctx)
    {
        ArgumentNullException.ThrowIfNull(buf);
        var pane = area with { Height = Math.Max(0, area.Height - 1) };
        if (pane != _pane)
        {
            _pane = pane;
            ResizeAll();
        }
        if (!_started)
        {
            _started = true;
            StartFirstApp();
        }

        if (_foreground is { } app && !pane.IsEmpty)
        {
            new TerminalViewBuilder()
                .Terminal(app.Terminal)
                .Focused(app.Failure is null && !App.Overlays.Any)
                .Draw(buf, pane);
        }
        else if (_foreground is null && !_quitting && !pane.IsEmpty)
        {
            buf.SetString(pane.X, pane.Y, $"No apps. {config.Prefix} starts one, or Ctrl+Q there quits.", Theme.Current.TextMuted, pane.Width);
        }

        if (area.Height > 0) DrawStatus(buf, new Rect(area.X, area.Bottom - 1, area.Width, 1));
    }

    // ---- Status ----

    private void DrawStatus(CellBuffer buf, Rect row)
    {
        var right = new List<Span>();
        if (_unread > 0) right.Add(new Span(_unread.ToString(CultureInfo.InvariantCulture) + " new  ", default));
        if (_doNotDisturb) right.Add(new Span("DND  ", Theme.Current.TextMuted));
        right.Add(new Span(Now().ToString("HH:mm", CultureInfo.InvariantCulture), default));

        new StatusBarBuilder().Left(StatusText()).Right(new Line(right)).Draw(buf, row);
    }

    private string StatusText()
    {
        if (_quitting) return "Closing apps...";
        if (_foreground is not { } app) return "No apps";
        if (app.Failure is { } failure) return $"{app.Name}: {failure}  r restart  c close";
        return app.Title is { Length: > 0 } title ? $"{app.Name}: {OneLine(title)}" : app.Name;
    }

    // ---- Input ----

    private bool OnKey(ConsoleKeyInfo key)
    {
        if (App.Overlays.Top is { } top)
        {
            // Ctrl+C, left alone, the app would take as a request to quit
            if (key.KeyChar == '\x03' || config.Prefix.Matches(key))
            {
                App.Overlays.Close(top);
                return true;
            }
            if (top is not AppListOverlay list || ListCommand(list, key) is not { } command) return false;
            App.Overlays.Close(list);
            command();
            return true;
        }
        if (_quitting) return true;

        if (config.Prefix.Matches(key))
        {
            ShowLauncher();
            return true;
        }

        if (_foreground is not { } app) return true;
        if (app.Failure is not null)
        {
            if (key.Modifiers == 0 && key.Key == ConsoleKey.R) Restart(app);
            else if (key.Modifiers == 0 && key.Key == ConsoleKey.C) Close(app);
            return true;
        }
        app.Terminal.HandleKey(key);
        return true;
    }

    // What a key does from either list, or null if it's the list's: typing filters, so these can't be plain letters
    private Action? ListCommand(AppListOverlay list, ConsoleKeyInfo key)
    {
        if (key.Modifiers == 0 && key.KeyChar is >= '1' and <= '9' && list.FilterIsEmpty)
        {
            var position = key.KeyChar - '1';
            return () =>
            {
                if (position < _apps.Count) SwitchTo(_apps[position]);
            };
        }
        if (CloseKey.Matches(key))
        {
            return () =>
            {
                if (_foreground is { } app) Close(app);
            };
        }
        if (QuitKey.Matches(key)) return AskToQuit;
        if (NotificationsKey.Matches(key)) return ShowNotifications;
        if (DoNotDisturbKey.Matches(key)) return () => _doNotDisturb = !_doNotDisturb;
        if (HelpKey.Matches(key)) return ShowHelp;
        if (SendPrefixKey.Matches(key)) return () => _foreground?.Terminal.HandleKey(config.PrefixKey);
        return null;
    }

    private bool OnPaste(string text)
    {
        if (App.Overlays.Any) return false;
        if (_foreground is { Failure: null } app && !_quitting) app.Terminal.HandlePaste(text);
        return true;
    }

    private bool OnMouse(MouseEvent mouse)
    {
        if (App.Overlays.Any) return false;
        if (_quitting) return true;

        var onStatus = mouse.Y >= _pane.Bottom;
        if (onStatus && mouse.Action == MouseAction.Press)
        {
            ShowSwitcher();
            return true;
        }

        // A drag can leave the pane, and the app still needs to see where it goes and that it ended
        if (onStatus && mouse.Action is not (MouseAction.Drag or MouseAction.Release)) return true;
        if (_foreground is { Failure: null } app && !_pane.IsEmpty)
            app.Terminal.HandleMouse(mouse with { Y = Math.Min(mouse.Y, _pane.Bottom - 1) });
        return true;
    }

    private void OnEvent(Event e)
    {
        if (e is not TerminalInfoEvent { Info: var info }) return;
        _defaultForeground = info.Foreground ?? _defaultForeground;
        _defaultBackground = info.Background ?? _defaultBackground;
        foreach (var app in _apps) ApplyDefaultColors(app.Terminal);
    }

    // ---- Lists and dialogs ----

    private void ShowSwitcher()
    {
        var apps = _recent.ToArray();
        var rows = apps.Select(a => new AppListRow(
            a.Name,
            a.Failure?.ToString() ?? (a.Title is { Length: > 0 } title ? OneLine(title) : null),
            (_apps.IndexOf(a) + 1).ToString(CultureInfo.InvariantCulture)));
        ShowList(" Switch ", rows, selected: 1, "Tab launcher", ShowLauncher, i =>
        {
            // The list doesn't follow apps that end while it's open
            if (_apps.Contains(apps[i])) SwitchTo(apps[i]);
        });
    }

    private void ShowLauncher()
    {
        AppEntry[] entries = [.. config.Apps, new AppEntry { Name = "Shell", Command = config.ShellCommand }];
        ShowList(" Launch ", entries.Select(e => new AppListRow(e.DisplayName, e.Command)), selected: 0, "Tab switcher", ShowSwitcher, i => Launch(entries[i]));
    }

    private void ShowList(string title, IEnumerable<AppListRow> rows, int selected, string swapHint, Action swap, Action<int> choose)
    {
        if (App.Overlays.Top is AppListOverlay open) App.Overlays.Close(open);
        string[] footer =
        [
            "F1 help", swapHint, "Ctrl+X close", "Ctrl+Q quit", "Ctrl+N notifications", "Ctrl+D DND",
            $"Ctrl+S send {config.Prefix}", "1-9 go to",
        ];
        App.Overlays.Show(new AppListOverlay(title, rows, selected, choose, swap, footer));
    }

    private void ShowNotifications()
    {
        _unread = 0;
        var lines = Enumerable.Reverse(_notifications)
            .Select(n => new Line(n.Time.ToString("HH:mm", CultureInfo.InvariantCulture) + "  " + n.Text))
            .ToArray();
        _ = App.Overlays.ShowMessageAsync("Notifications", lines.Length > 0 ? new Text(lines) : Text.From("No notifications"));
    }

    private void ShowHelp()
    {
        var prefix = config.Prefix.ToString();
        _ = App.Overlays.ShowMessageAsync("Keys", Text.From($"""
            Every key goes to the app, except {prefix}, which opens the launcher.
            In the launcher and the switcher:

            Tab         Swap between them
            Enter       Start or switch to the selected app
            1-9         Go to the app at that position, before typing a filter
            Ctrl+X      Close the current app
            Ctrl+Q      Quit Desk
            Ctrl+N      Notifications
            Ctrl+D      Do Not Disturb: {(_doNotDisturb ? "on" : "off")}
            Ctrl+S      Send {prefix} to the app
            F1          This help
            Esc         Close, as {prefix} does
            """));
    }

    private void AskToQuit()
    {
        var running = _apps.Count(a => a.Failure is null);
        if (running == 0)
        {
            Quit();
            return;
        }

        var dialog = new ConfirmDialog($"{running} running. Close and quit?", "Quit", "Stay", yesFirst: false);
        App.Overlays.Show(dialog);
        _ = dialog.Result.ContinueWith(
            answer =>
            {
                if (answer.Result) App.Post(Quit);
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    // ---- Lifecycle ----

    private void StartFirstApp()
    {
        var shell = new AppEntry { Name = Path.GetFileName(config.ShellCommand), Command = config.ShellCommand };
        var app = Start(shell, reportFailure: false) ?? Start(shell with { Name = "sh", Command = DeskConfig.FallbackShell });
        if (app is not null) SwitchTo(app);
        else ShowLauncher();
    }

    private void Launch(AppEntry entry)
    {
        if (Start(entry) is { } app) SwitchTo(app);
        else if (_foreground is null) ShowLauncher();
    }

    private HostedApp? Start(AppEntry entry, bool reportFailure = true)
    {
        var app = new HostedApp(entry);
        if (!StartTerminal(app, reportFailure)) return null;
        _apps.Add(app);
        _recent.Add(app);
        return app;
    }

    // Set up before the program's output is read, as it can ring or ask about its colors straight away
    private bool StartTerminal(HostedApp app, bool reportFailure)
    {
        try
        {
            var info = DeskConfig.StartInfo(app.Entry, Math.Max(1, _pane.Width), Math.Max(1, _pane.Height));
            VtTerminal.Start(info, terminal =>
            {
                app.Terminal = terminal;
                ApplyDefaultColors(terminal);
                Watch(app);
            });
            return true;
        }
        catch (Win32Exception e)
        {
            // The user just asked for this, so it's said out loud even with Do Not Disturb on
            if (reportFailure) App.Toasts.Push($"Can't start {app.Name}: {e.Message}", ToastLevel.Error);
            return false;
        }
    }

    // These run on the terminal's threads. Whether an app is in front is only settled on the UI thread: a program can
    // ring or copy before the code that started it has brought it to the front.
    private void Watch(HostedApp app)
    {
        var terminal = app.Terminal;
        terminal.Changed += () =>
        {
            if (ReferenceEquals(_foreground, app)) App.RequestRedraw();
        };
        terminal.Exited += () => App.Post(() => OnExited(app, terminal));
        terminal.Bell += () =>
        {
            // Not worth a trip to the UI thread, for a program ringing in a loop
            if (ReferenceEquals(_foreground, app) || app.WantsAttention) return;
            App.Post(() =>
            {
                if (ReferenceEquals(_foreground, app) || app.WantsAttention) return;
                app.WantsAttention = true;
                Notify($"{app.Name} wants attention");
            });
        };
        terminal.ClipboardSet += text =>
        {
            // A program out of sight doesn't get to replace what the user copied
            App.Post(() =>
            {
                if (ReferenceEquals(_foreground, app)) Clipboard.SetText(text);
            });
        };
        terminal.Notified += (title, body) =>
            App.Post(() => Notify(title is { Length: > 0 } ? $"{app.Name}: {title}: {body}" : $"{app.Name}: {body}"));
    }

    private void OnExited(HostedApp app, VtTerminal terminal)
    {
        if (_closing.Remove(terminal))
        {
            terminal.Dispose();
            if (_quitting && _closing.Count == 0) App.Quit();
            return;
        }
        if (!ReferenceEquals(app.Terminal, terminal) || !_apps.Contains(app) || terminal.Exit is not { } exit) return;

        if (HostedApp.EndedCleanly(exit))
        {
            Remove(app);
            return;
        }

        app.Failure = exit;
        if (!ReferenceEquals(_foreground, app)) Notify($"{app.Name} {exit}");
    }

    private void Restart(HostedApp app)
    {
        var old = app.Terminal;
        if (!StartTerminal(app, reportFailure: true)) return;
        old.Dispose();
        app.Failure = null;
        app.Terminal.HandleFocus(true);
    }

    private void Close(HostedApp app)
    {
        if (app.Failure is not null)
        {
            Remove(app);
            return;
        }
        var terminal = app.Terminal;
        _closing.Add(terminal);
        terminal.Hangup();
        _ = KillLater(terminal);
        Forget(app);
    }

    private async Task KillLater(VtTerminal terminal)
    {
        await Task.Delay(config.CloseTimeout, CancellationToken.None).ConfigureAwait(false);
        terminal.Kill();
    }

    private void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        foreach (var app in _apps.ToArray())
        {
            if (app.Failure is null)
            {
                Close(app);
            }
            else
            {
                Forget(app);
                app.Terminal.Dispose();
            }
        }
        if (_closing.Count == 0)
        {
            App.Quit();
            return;
        }

        // Whatever is still here after the kill can't be helped, and shouldn't keep the terminal
        _ = QuitLater();
    }

    private async Task QuitLater()
    {
        await Task.Delay(config.CloseTimeout + GraceAfterKill, CancellationToken.None).ConfigureAwait(false);
        App.Post(App.Quit);
    }

    private void Remove(HostedApp app)
    {
        Forget(app);
        app.Terminal.Dispose();
    }

    // Takes it off the lists, and brings the previous app to the front. With none left, the launcher opens rather
    // than Desk quitting, so that typing exit once doesn't end the session.
    private void Forget(HostedApp app)
    {
        _apps.Remove(app);
        _recent.Remove(app);
        if (!ReferenceEquals(_foreground, app)) return;

        _foreground = null;
        if (_recent.Count > 0) SwitchTo(_recent[0]);
        else if (!_quitting) ShowLauncher();
    }

    private void SwitchTo(HostedApp app)
    {
        var previous = _foreground;
        if (!ReferenceEquals(previous, app)) previous?.Terminal.HandleFocus(false);
        _foreground = app;
        app.WantsAttention = false;
        _recent.Remove(app);
        _recent.Insert(0, app);
        if (!ReferenceEquals(previous, app)) app.Terminal.HandleFocus(true);
    }

    private void ResizeAll()
    {
        if (_pane.IsEmpty) return;
        foreach (var app in _apps) app.Terminal.Resize(_pane.Width, _pane.Height);
    }

    private void ApplyDefaultColors(VtTerminal terminal)
    {
        lock (terminal.Lock)
        {
            if (_defaultForeground is { } foreground) terminal.Screen.DefaultForeground = foreground;
            if (_defaultBackground is { } background) terminal.Screen.DefaultBackground = background;
        }
    }

    private DateTimeOffset Now() => (time ?? TimeProvider.System).GetLocalNow();

    // ---- Notifications ----

    private void Notify(string text)
    {
        text = OneLine(text);
        _notifications.Add(new Notification(Now(), text));
        if (_notifications.Count > MaxNotifications) _notifications.RemoveAt(0);
        _unread = Math.Min(_unread + 1, MaxNotifications);
        if (!_doNotDisturb) App.Toasts.Push(text);
    }

    private static string OneLine(string text) => string.Create(text.Length, text, static (span, source) =>
    {
        for (var i = 0; i < span.Length; i++) span[i] = char.IsControl(source[i]) ? ' ' : source[i];
    });
}

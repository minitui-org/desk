using MiniTui.Desk.Protocol;

namespace MiniTui.Desk;

/// <summary>Native apps (spec §8, §13.4): what they say over the protocol, and asking them to quit.</summary>
internal sealed partial class DeskShell
{
    private const string ShellId = "shell";
    private const string UnnamedId = "app";

    private readonly HashSet<DeskConnection> _connections = [];
    private volatile DeskServer? _server;
    private bool _quitWaiting;

    internal string? SocketPath => _server?.SocketPath;

    private void StartServer()
    {
        try
        {
            _server = DeskServer.Start(config.RuntimeDirectory ?? DeskServer.DefaultDirectory(), Accept);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Notify($"Native apps are off: {e.Message}");
        }
    }

    /// <summary>From any thread, for a signal that will end the process.</summary>
    internal void RemoveSocket() => _server?.Dispose();

    private void StopServer()
    {
        _server?.Dispose();
        _server = null;
        foreach (var connection in _connections) connection.Dispose();
        _connections.Clear();
    }

    private string NewId(AppEntry entry)
    {
        var id = entry.Id is { Length: > 0 } given ? given : UnnamedId;
        var number = _instances.GetValueOrDefault(id) + 1;
        _instances[id] = number;
        return $"{id}-{number}";
    }

    // On the server's thread. Everything after this happens on the UI thread, in the order it arrived.
    private void Accept(DeskConnection connection) => App.Post(() =>
    {
        if (_server is null)
        {
            connection.Dispose();
            return;
        }
        _connections.Add(connection);
        connection.Start(
            message => App.Post(() => OnMessage(connection, message)),
            () => App.Post(() => OnClosed(connection)));
    });

    private void OnMessage(DeskConnection connection, DeskMessage message)
    {
        if (!connection.IsOpen) return;
        if (connection.App is not { } app)
        {
            if (message.Type == DeskWire.Hello) OnHello(connection, message);
            return;
        }

        switch (message.Type)
        {
            case DeskWire.Title:
                connection.Title = message.Text ?? ""; // stripped where it's shown
                break;
            case DeskWire.Notify:
                OnNotify(app, connection, message);
                break;
            case DeskWire.Launch:
                OnLaunch(app, message);
                break;
            case DeskWire.QuitDeclined:
                app.QuitAsked = false;
                _quitWaiting = false;
                break;
            case DeskWire.Bye:
                connection.Dispose(); // and OnClosed follows
                break;
        }
    }

    private void OnHello(DeskConnection connection, DeskMessage hello)
    {
        var app = _apps.Find(a => a.Id == hello.AppId && a.Failure is null);
        if (app is null)
        {
            connection.Dispose();
            return;
        }

        connection.App = app;
        connection.Name = OneLine(hello.Name ?? "").Trim();
        app.Connections.Add(connection);
        connection.Send(new DeskMessage
        {
            Type = DeskWire.Welcome,
            Protocol = Math.Clamp(hello.Protocol ?? 0, 0, DeskWire.ProtocolVersion),
            Theme = _terminalIsDark == false ? "light" : "dark",
        });
        connection.Send(new DeskMessage { Type = DeskWire.Focus, Focused = ReferenceEquals(_foreground, app) });
    }

    private void OnClosed(DeskConnection connection)
    {
        _connections.Remove(connection);
        if (connection.App is not { } app) return;
        app.Connections.Remove(connection);
        if (app.Connections.Count == 0) app.QuitAsked = false;
        CheckQuitWait();
    }

    // The app's urgency is a request, and Do Not Disturb wins (spec §13.4)
    private void OnNotify(HostedApp app, DeskConnection connection, DeskMessage message)
    {
        var heading = OneLine(message.Title ?? "");
        var body = OneLine(message.Body ?? "");
        var text = OneLine(app.Name) + ": " + (heading.Length > 0 ? heading + ": " + body : body);
        DeskAction[] actions = [.. (message.Actions ?? [])
            .Where(a => a is { Id: not null, Label: not null })
            .Select(a => a with { Label = OneLine(a.Label) })];
        var toast = message.Urgency != "low";

        var existing = message.Id is null ? null : _notifications.Find(n => n.AppId == app.Id && n.Id == message.Id);
        if (existing is not null)
        {
            existing.Time = Now();
            existing.Text = text;
            existing.Read = false;
            existing.Source = connection;
            existing.Actions = actions;
            if (toast && !_doNotDisturb) App.Toasts.Push(text);
            return;
        }
        Add(new Notification(Now(), text) { AppId = app.Id, Id = message.Id, Source = connection, Actions = actions }, toast);
    }

    // Only from the app on the screen: one in the background doesn't get to take it
    private void OnLaunch(HostedApp app, DeskMessage message)
    {
        if (!ReferenceEquals(_foreground, app) || _quitting || _quitWaiting) return;
        if (config.Apps.FirstOrDefault(e => e.Id is not null && e.Id == message.AppId) is not { } entry) return;
        string[] args = [.. entry.Args ?? [], .. (message.Args ?? []).Where(a => a is not null)];
        Launch(entry with { Args = args });
    }

    private static void SendFocus(HostedApp app, bool focused)
    {
        foreach (var connection in app.Connections) connection.Send(new DeskMessage { Type = DeskWire.Focus, Focused = focused });
    }

    private static void SendQuitRequest(HostedApp app)
    {
        foreach (var connection in app.Connections) connection.Send(new DeskMessage { Type = DeskWire.QuitRequest });
    }

    // Ctrl+X. A native app is asked first, as it may have something to save; asking again forces it.
    private void AskToClose(HostedApp app)
    {
        if (app.Failure is null && app.Connections.Count > 0 && !app.QuitAsked)
        {
            app.QuitAsked = true;
            SendQuitRequest(app);
            return;
        }
        Close(app);
    }

    // Ctrl+Q, confirmed. Native apps are asked, and the rest are closed once those have all gone.
    private void BeginQuit()
    {
        var native = WaitingToQuit().ToArray();
        if (native.Length == 0)
        {
            Quit();
            return;
        }
        _quitWaiting = true;
        foreach (var app in native) SendQuitRequest(app);
        BringWaitingToFront();
    }

    // Most recently used first, so the one the user was looking at is the one they're shown
    private IEnumerable<HostedApp> WaitingToQuit() => _recent.Where(a => a.Failure is null && a.Connections.Count > 0);

    private void CheckQuitWait()
    {
        if (!_quitWaiting) return;
        if (!WaitingToQuit().Any())
        {
            _quitWaiting = false;
            Quit();
            return;
        }
        BringWaitingToFront();
    }

    // One that's staying is probably asking the user something
    private void BringWaitingToFront()
    {
        if (_foreground is { } app && WaitingToQuit().Contains(app)) return;
        if (WaitingToQuit().FirstOrDefault() is { } waiting) SwitchTo(waiting);
    }

    private void CancelQuitWait() => _quitWaiting = false;
}

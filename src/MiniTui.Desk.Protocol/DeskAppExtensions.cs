namespace MiniTui.Desk.Protocol;

/// <summary>The one-call opt-in to Desk.</summary>
public static class DeskAppExtensions
{
    /// <summary>
    /// Connect to the Desk shell, if there is one. Events arrive on the app's thread through <see cref="App.Post"/>,
    /// and with no <see cref="DeskClient.QuitRequested"/> handler, a request to quit calls <see cref="App.Quit"/>. The
    /// connection closes when the app stops. Outside Desk, this does nothing.
    /// </summary>
    /// <param name="app">The app.</param>
    /// <param name="name">The app's name, for the status line and the switcher.</param>
    /// <param name="client">The connection, to set the title and send notifications with.</param>
    /// <param name="version">The app's version, if it has one to report.</param>
    public static App WithDesk(this App app, string name, out DeskClient client, string? version = null) => WithDesk(
        app,
        name,
        out client,
        version,
        Environment.GetEnvironmentVariable("MINITUI_DESK_SOCKET"),
        Environment.GetEnvironmentVariable("MINITUI_DESK_APP_ID"));

    internal static App WithDesk(this App app, string name, out DeskClient client, string? version, string? socketPath, string? appId)
    {
        ArgumentNullException.ThrowIfNull(app);
        var connected = DeskClient.Connect(name, version, socketPath, appId, app.Post, app.Quit);
        app.Stopping.Register(() => _ = connected.DisposeAsync().AsTask());
        client = connected;
        return app;
    }
}

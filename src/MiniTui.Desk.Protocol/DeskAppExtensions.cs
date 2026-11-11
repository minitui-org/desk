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
    /// <param name="manifest">
    /// How Desk lists the app and how other apps open it. With <c>MINITUI_DESK_MANIFEST=1</c> in the environment, this
    /// writes it to stdout as one line of JSON and exits, before the app touches the terminal: that's how
    /// <c>tuidesk add</c> reads it.
    /// </param>
    public static App WithDesk(this App app, string name, out DeskClient client, string? version = null, DeskManifest? manifest = null)
    {
        if (Environment.GetEnvironmentVariable(ManifestVariable) == "1")
        {
            WriteManifest(Console.Out, name, version, manifest);
            Console.Out.Flush();
            Environment.Exit(0);
        }
        return WithDesk(
            app,
            name,
            out client,
            version,
            Environment.GetEnvironmentVariable("MINITUI_DESK_SOCKET"),
            Environment.GetEnvironmentVariable("MINITUI_DESK_APP_ID"));
    }

    /// <summary>Set to <c>1</c> to have <c>WithDesk</c> write the manifest and exit (spec §16.4).</summary>
    internal const string ManifestVariable = "MINITUI_DESK_MANIFEST";

    internal static void WriteManifest(TextWriter output, string name, string? version, DeskManifest? manifest)
    {
        var answer = new ManifestAnswer
        {
            Id = manifest?.Id,
            Name = name,
            Version = version,
            Description = manifest?.Description,
            Args = manifest?.Args is { } args ? [.. args] : null,
        };
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(answer, DeskJson.Default.ManifestAnswer));
    }

    // startupWait is for tests: a busy machine can take longer than the default to say welcome
    internal static App WithDesk(this App app, string name, out DeskClient client, string? version, string? socketPath, string? appId, TimeSpan? startupWait = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        var connected = DeskClient.Connect(name, version, socketPath, appId, app.Post, app.Quit, startupWait);
        app.Stopping.Register(() => _ = connected.DisposeAsync().AsTask());
        client = connected;
        return app;
    }
}

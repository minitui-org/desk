using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Threading.Channels;

namespace MiniTui.Desk.Protocol;

/// <summary>
/// A connection to the Desk shell (spec §8). Outside Desk, or once the connection has dropped, it's inactive: every
/// member does nothing, nothing throws, and the app carries on. Apps don't need to check.
/// </summary>
/// <remarks>
/// Without <see cref="DeskAppExtensions.WithDesk(App, string, out DeskClient, string?)"/>, events arrive on a background thread, one at a time, and one that
/// arrives before its handler is added is missed. An exception from a handler there is ignored.
/// </remarks>
public sealed class DeskClient : IAsyncDisposable
{
    private const int QueueCapacity = 256;
    private static readonly TimeSpan StartupWait = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FlushWait = TimeSpan.FromMilliseconds(200);

    private readonly Action<Action>? _post;
    private readonly Action? _unhandledQuit;
    private readonly ManualResetEventSlim _welcomed = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<byte[]> _outgoing = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueueCapacity)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });

    private Socket? _socket;
    private Task _writer = Task.CompletedTask;
    private volatile bool _active;
    private volatile bool _focused = true;
    private volatile string? _theme;
    private volatile bool _disposed;
    private int _dropped;

    private DeskClient(Action<Action>? post, Action? unhandledQuit)
    {
        _post = post;
        _unhandledQuit = unhandledQuit;
    }

    /// <summary>
    /// Connect using what the shell put in the environment. Waits up to half a second for the shell to answer, so that
    /// <see cref="Theme"/> is known. Outside Desk it returns at once, inactive.
    /// </summary>
    /// <param name="name">The app's name, for the status line and the switcher.</param>
    /// <param name="version">The app's version, if it has one to report.</param>
    public static DeskClient Connect(string name, string? version = null) => Connect(
        name,
        version,
        Environment.GetEnvironmentVariable("MINITUI_DESK_SOCKET"),
        Environment.GetEnvironmentVariable("MINITUI_DESK_APP_ID"));

    internal static DeskClient Connect(
        string? name,
        string? version,
        string? socketPath,
        string? appId,
        Action<Action>? post = null,
        Action? unhandledQuit = null,
        TimeSpan? startupWait = null)
    {
        var client = new DeskClient(post, unhandledQuit);
        if (!string.IsNullOrEmpty(socketPath) && !string.IsNullOrEmpty(appId))
            client.Open(socketPath, appId, name ?? "", version, startupWait ?? StartupWait);
        return client;
    }

    /// <summary>Whether there's a live connection to a shell.</summary>
    public bool IsActive => _active;

    /// <summary>Whether this app is on the screen. Always true when inactive.</summary>
    public bool IsFocused => _focused;

    /// <summary><c>light</c> or <c>dark</c>, once the shell has said. Null outside Desk.</summary>
    public string? Theme => _theme;

    /// <summary>The app came to the front or went to the back. It may want to pause in the background.</summary>
    public event Action<bool>? FocusChanged;

    /// <summary>The user chose an action from a notification: its id, and the action's id.</summary>
    public event Action<string, string>? NotificationAction;

    /// <summary>
    /// The shell asks the app to close. It should exit, or ask the user and call <see cref="DeclineQuit"/> if they
    /// choose to stay. With no handler, an app using <see cref="DeskAppExtensions.WithDesk(App, string, out DeskClient, string?)"/> quits.
    /// </summary>
    public event Action? QuitRequested;

    /// <summary>Set the short status in the status line and the switcher.</summary>
    public void SetTitle(string text)
    {
        if (text is not null) Send(new DeskMessage { Type = DeskWire.Title, Text = text });
    }

    /// <summary>Send a notification, for the shell to queue by its own policy.</summary>
    public void Notify(DeskNotification notification)
    {
        if (notification is null) return;
        Send(new DeskMessage
        {
            Type = DeskWire.Notify,
            Id = notification.Id,
            Title = notification.Title,
            Body = notification.Body,
            Urgency = DeskWire.UrgencyName(notification.Urgency),
            Actions = notification.Actions?.ToArray(),
        });
    }

    /// <summary>Open another app from the user's <c>apps.json</c>. Only honored while this app is on the screen.</summary>
    /// <param name="appId">The id in <c>apps.json</c>.</param>
    /// <param name="args">Arguments, after the entry's own.</param>
    public void Launch(string appId, params string[] args)
    {
        if (appId is not null) Send(new DeskMessage { Type = DeskWire.Launch, AppId = appId, Args = args ?? [] });
    }

    /// <summary>Tell the shell that the user chose to stay, after <see cref="QuitRequested"/>.</summary>
    public void DeclineQuit() => Send(new DeskMessage { Type = DeskWire.QuitDeclined });

    /// <summary>Say goodbye and close the connection. Doesn't wait long for a shell that isn't reading.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        var wasActive = _active;
        if (wasActive) Send(new DeskMessage { Type = DeskWire.Bye });
        _disposed = true;
        _outgoing.Writer.TryComplete();
        if (wasActive)
        {
            try
            {
                await _writer.WaitAsync(FlushWait).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // It isn't reading, which is no reason to hold the app up
            }
        }
        Drop();
        await _stop.CancelAsync().ConfigureAwait(false);
        _stop.Dispose();
        _welcomed.Dispose();
    }

    private void Open(string path, string appId, string name, string? version, TimeSpan wait)
    {
        var clock = Stopwatch.StartNew();
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var timeout = new CancellationTokenSource(wait);
            socket.ConnectAsync(new UnixDomainSocketEndPoint(path), timeout.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception e) when (IsConnectionFailure(e) || e is ArgumentException or PlatformNotSupportedException)
        {
            socket.Dispose();
            return;
        }

        _socket = socket;
        _active = true;
        var stream = new NetworkStream(socket, ownsSocket: false);
        Send(new DeskMessage { Type = DeskWire.Hello, AppId = appId, Name = name, Version = version, Protocol = DeskWire.ProtocolVersion });
        _writer = Task.Run(() => WriteLoop(stream));
        _ = Task.Run(() => ReadLoop(stream));

        var left = wait - clock.Elapsed;
        if (left > TimeSpan.Zero) _welcomed.Wait(left);
    }

    private void Send(DeskMessage message)
    {
        if (!_active) return;
        if (DeskWire.Encode(message) is { } line) _outgoing.Writer.TryWrite(line);
    }

    private async Task WriteLoop(NetworkStream stream)
    {
        try
        {
            await foreach (var line in _outgoing.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
                await stream.WriteAsync(line, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (IsConnectionFailure(e))
        {
            Drop();
        }
    }

    private async Task ReadLoop(NetworkStream stream)
    {
        var framer = new DeskLineFramer();
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (read == 0 || !framer.Push(buffer.AsSpan(0, read), Handle)) break;
            }
        }
        catch (Exception e) when (IsConnectionFailure(e))
        {
            // Dropped below
        }
        Drop();
    }

    private void Handle(byte[] line)
    {
        if (DeskWire.Decode(line) is not { } message) return;
        switch (message.Type)
        {
            case DeskWire.Welcome:
                _theme = message.Theme;
                _welcomed.Set();
                break;
            case DeskWire.Focus when message.Focused is { } focused && focused != _focused:
                _focused = focused;
                Raise(() => FocusChanged?.Invoke(focused));
                break;
            case DeskWire.NotificationAction when message.Id is { } id && message.Action is { } action:
                Raise(() => NotificationAction?.Invoke(id, action));
                break;
            case DeskWire.QuitRequest:
                Raise(() =>
                {
                    if (QuitRequested is { } handler) handler();
                    else _unhandledQuit?.Invoke();
                });
                break;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Spec §8.4. A handler on the reader thread would otherwise take the process down.")]
    private void Raise(Action action)
    {
        void IfOpen()
        {
            if (!_disposed) action();
        }

        if (_post is { } post)
        {
            post(IfOpen);
            return;
        }
        try
        {
            IfOpen();
        }
        catch (Exception)
        {
            // Documented on the class
        }
    }

    // However it ended. An app that paused in the background isn't left waiting for a focus that can't come now.
    private void Drop()
    {
        if (Interlocked.Exchange(ref _dropped, 1) != 0) return;
        _active = false;
        _outgoing.Writer.TryComplete();
        _socket?.Dispose();
        if (_focused) return;
        _focused = true;
        if (!_disposed) Raise(() => FocusChanged?.Invoke(true));
    }

    private static bool IsConnectionFailure(Exception e) =>
        e is SocketException or IOException or OperationCanceledException or ObjectDisposedException;
}

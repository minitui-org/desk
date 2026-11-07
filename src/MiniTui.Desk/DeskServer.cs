using System.Globalization;
using System.Net.Sockets;

namespace MiniTui.Desk;

/// <summary>The socket native apps connect to (spec §8.2). It only accepts: each connection is handed on as it arrives.</summary>
internal sealed class DeskServer : IDisposable
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode GroupAndOthers = (UnixFileMode)0x3F;

    private readonly Socket _listener;
    private int _disposed;

    private DeskServer(Socket listener, string path)
    {
        _listener = listener;
        SocketPath = path;
    }

    public string SocketPath { get; }

    /// <summary><c>$XDG_RUNTIME_DIR/tuidesk</c>, or <c>/tmp/tuidesk-&lt;uid&gt;</c> without it.</summary>
    public static string DefaultDirectory() =>
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime
            ? Path.Combine(runtime, "tuidesk")
            : Path.Combine("/tmp", "tuidesk-" + Libc.EffectiveUserId.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Listen at <c>&lt;directory&gt;/&lt;pid&gt;.sock</c>, replacing a stale file from a dead shell. Throws an
    /// <see cref="IOException"/> saying why it can't.
    /// </summary>
    /// <param name="accepted">Called on a background thread with each new connection, not yet started.</param>
    public static DeskServer Start(string directory, Action<DeskConnection> accepted)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        PrepareDirectory(directory);

        // No live process can have this pid, so a file here is left from a shell that died
        var path = Path.Combine(directory, Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".sock");
        File.Delete(path);

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(16);
        }
        catch (Exception e) when (e is SocketException or ArgumentException)
        {
            listener.Dispose();
            throw new IOException($"can't listen at {path}: {e.Message}", e);
        }

        var server = new DeskServer(listener, path);
        _ = Task.Run(() => server.AcceptLoop(accepted));
        return server;
    }

    /// <summary>Make it a directory only its owner can use, refusing one that belongs to someone else.</summary>
    public static void PrepareDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory, OwnerOnly);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"can't create {directory}: {e.Message}", e);
        }

        if (!Libc.TryDirectoryOwner(directory, out var owner, out var mode))
            throw new IOException($"{directory} isn't a directory");
        if (owner != Libc.EffectiveUserId)
            throw new IOException($"{directory} belongs to another user");
        if ((mode & GroupAndOthers) != 0) File.SetUnixFileMode(directory, OwnerOnly);
    }

    /// <summary>Stop listening and remove the socket. Connections already made are the caller's.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _listener.Dispose();

        // .NET removes a socket file it bound when the socket closes, but doesn't promise to
        try
        {
            File.Delete(SocketPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Replaced next time a shell gets this pid
        }
    }

    private async Task AcceptLoop(Action<DeskConnection> accepted)
    {
        while (true)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                continue;
            }
            accepted(new DeskConnection(socket));
        }
    }
}

using System.Net.Sockets;
using System.Threading.Channels;
using MiniTui.Desk.Protocol;

namespace MiniTui.Desk;

/// <summary>
/// One app's connection (spec §8). It reads and writes on background threads, and a full queue drops what's sent
/// rather than holding up the shell. Everything else on it belongs to the UI thread.
/// </summary>
internal sealed class DeskConnection : IDisposable
{
    private const int QueueCapacity = 256;

    private readonly Socket _socket;
    private readonly Channel<byte[]> _outgoing = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueueCapacity)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });
    private int _closed;

    public DeskConnection(Socket socket) => _socket = socket;

    /// <summary>The app it said hello for. Until then, nothing it sends counts.</summary>
    public HostedApp? App { get; set; }

    public string Name { get; set; } = "";

    /// <summary>From <c>title</c>, as sent. Null until it sends one.</summary>
    public string? Title { get; set; }

    public bool IsOpen => Volatile.Read(ref _closed) == 0;

    /// <summary>Start reading and writing. <paramref name="received"/> gets each message in order, and <paramref name="closed"/> runs once, last.</summary>
    public void Start(Action<DeskMessage> received, Action closed)
    {
        var stream = new NetworkStream(_socket, ownsSocket: false);
        _ = Task.Run(() => WriteLoop(stream));
        _ = Task.Run(() => ReadLoop(stream, received, closed));
    }

    public void Send(DeskMessage message)
    {
        if (IsOpen && DeskWire.Encode(message) is { } line) _outgoing.Writer.TryWrite(line);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _outgoing.Writer.TryComplete();
        _socket.Dispose();
    }

    private async Task WriteLoop(NetworkStream stream)
    {
        try
        {
            await foreach (var line in _outgoing.Reader.ReadAllAsync().ConfigureAwait(false))
                await stream.WriteAsync(line).ConfigureAwait(false);
        }
        catch (Exception e) when (IsConnectionFailure(e))
        {
            Dispose();
        }
    }

    private async Task ReadLoop(NetworkStream stream, Action<DeskMessage> received, Action closed)
    {
        var framer = new DeskLineFramer();
        var buffer = new byte[4096];
        void Line(byte[] line)
        {
            if (DeskWire.Decode(line) is { } message) received(message);
        }

        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0 || !framer.Push(buffer.AsSpan(0, read), Line)) break;
            }
        }
        catch (Exception e) when (IsConnectionFailure(e))
        {
            // Closed below
        }
        Dispose();
        closed();
    }

    private static bool IsConnectionFailure(Exception e) =>
        e is SocketException or IOException or OperationCanceledException or ObjectDisposedException;
}

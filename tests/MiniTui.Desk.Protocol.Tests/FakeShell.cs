using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;

namespace MiniTui.Desk.Protocol.Tests;

/// <summary>The other end of the socket: real, but scripted.</summary>
internal sealed class FakeShell : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string _directory = Directory.CreateTempSubdirectory("desk-protocol-").FullName;
    private readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly BlockingCollection<string> _lines = [];
    private readonly ManualResetEventSlim _connected = new();
    private Socket? _socket;

    public FakeShell(string? welcome = """{"type":"welcome","protocol":0,"theme":"dark"}""", bool read = true)
    {
        SocketPath = Path.Combine(_directory, "s");
        _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        _listener.Listen();
        new Thread(() => Serve(welcome, read)) { IsBackground = true }.Start();
    }

    public string SocketPath { get; }

    /// <summary>The next line the client sent.</summary>
    public string Next()
    {
        Assert.True(_lines.TryTake(out var line, Timeout), "Nothing arrived");
        return line!;
    }

    /// <summary>Nothing more arrives in a moment.</summary>
    public void AssertQuiet() => Assert.False(_lines.TryTake(out var line, TimeSpan.FromMilliseconds(100)), $"Unexpected: {line}");

    public void Send(string line) => SendBytes(Encoding.UTF8.GetBytes(line + "\n"));

    public void SendBytes(byte[] bytes)
    {
        Assert.True(_connected.Wait(Timeout));
        _socket!.Send(bytes);
    }

    public void Hangup()
    {
        Assert.True(_connected.Wait(Timeout));
        _socket!.Dispose();
    }

    public void Dispose()
    {
        _socket?.Dispose();
        _listener.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private void Serve(string? welcome, bool read)
    {
        try
        {
            _socket = _listener.Accept();
            _connected.Set();
            if (!read) return;

            var pending = new StringBuilder();
            var buffer = new byte[4096];
            while (_socket.Receive(buffer) is var count and > 0)
            {
                pending.Append(Encoding.UTF8.GetString(buffer, 0, count));
                for (var end = pending.ToString().IndexOf('\n', StringComparison.Ordinal); end >= 0; end = pending.ToString().IndexOf('\n', StringComparison.Ordinal))
                {
                    var line = pending.ToString(0, end);
                    pending.Remove(0, end + 1);
                    if (welcome is not null && line.Contains("\"hello\"", StringComparison.Ordinal)) Send(welcome);
                    _lines.Add(line);
                }
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            // The test has finished with it
        }
    }
}

using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;

namespace MiniTui.Desk.Tests;

/// <summary>A connection to the shell's socket that sends whatever it's told, for what a well-behaved client won't do.</summary>
internal sealed class RawClient : IDisposable
{
    private readonly Socket _socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly ConcurrentQueue<string> _lines = new();
    private volatile bool _closed;

    public RawClient(string path)
    {
        _socket.Connect(new UnixDomainSocketEndPoint(path));
        _ = Task.Run(Read);
    }

    /// <summary>The shell closed it.</summary>
    public bool Closed => _closed;

    public IReadOnlyCollection<string> Received => _lines;

    public bool Got(string fragment) => _lines.Any(l => l.Contains(fragment, StringComparison.Ordinal));

    public void Send(string text) => SendBytes(Encoding.UTF8.GetBytes(text));

    public void SendBytes(byte[] bytes)
    {
        try
        {
            _socket.Send(bytes);
        }
        catch (SocketException)
        {
            // The shell may have closed it already, which the test checks for itself
        }
    }

    public void Hello(string appId, string name = "Raw") =>
        Send($"{{\"type\":\"hello\",\"appId\":\"{appId}\",\"name\":\"{name}\",\"version\":\"1\",\"protocol\":0}}\n");

    public void Dispose() => _socket.Dispose();

    private void Read()
    {
        var pending = new StringBuilder();
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var count = _socket.Receive(buffer);
                if (count == 0) break;
                pending.Append(Encoding.UTF8.GetString(buffer, 0, count));
                var text = pending.ToString();
                var end = text.LastIndexOf('\n');
                if (end < 0) continue;
                foreach (var line in text[..end].Split('\n')) _lines.Enqueue(line);
                pending.Clear().Append(text[(end + 1)..]);
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            // Closed either way
        }
        _closed = true;
    }
}

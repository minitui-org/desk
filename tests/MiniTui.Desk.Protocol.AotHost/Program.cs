using System.Net.Sockets;
using System.Text;
using MiniTui;
using MiniTui.Desk.Protocol;

// Exits 0 when every check passes. A real client on a real socket, for the AOT-compiled code.
var failures = 0;

var directory = Directory.CreateTempSubdirectory("desk-aot-").FullName;
var path = Path.Combine(directory, "s");
using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
listener.Bind(new UnixDomainSocketEndPoint(path));
listener.Listen();

Check("inactive outside Desk", !DeskClient.Connect("Host").IsActive);

Environment.SetEnvironmentVariable("MINITUI_DESK_SOCKET", path);
Environment.SetEnvironmentVariable("MINITUI_DESK_APP_ID", "host-1");

var shell = Task.Run(() =>
{
    using var socket = listener.Accept();
    var received = new StringBuilder();
    var buffer = new byte[4096];
    while (!received.ToString().Contains("\"bye\"", StringComparison.Ordinal))
    {
        var count = socket.Receive(buffer);
        if (count == 0) break;
        received.Append(Encoding.UTF8.GetString(buffer, 0, count));
        if (received.ToString().Contains("\"hello\"", StringComparison.Ordinal) && !received.ToString().Contains("sent", StringComparison.Ordinal))
        {
            received.Append("sent");
            socket.Send("""{"type":"welcome","protocol":0,"theme":"dark","later":1}"""u8.ToArray());
            socket.Send("\n{\"type\":\"focus\",\"focused\":false}\n{\"type\":\"notificationAction\",\"id\":\"a\",\"action\":\"done\"}\n"u8.ToArray());
        }
    }
    return received.ToString();
});

var client = DeskClient.Connect("Host", "1.0");
var action = new TaskCompletionSource<string>();
client.NotificationAction += (id, chosen) => action.TrySetResult(id + "/" + chosen);
Check("active", client.IsActive);
Check("theme", client.Theme == "dark");

client.SetTitle("title");
client.Notify(new DeskNotification("a", "t", "b") { Urgency = DeskUrgency.High, Actions = [new DeskAction("done", "Done")] });
client.Launch("other", "x");
client.DeclineQuit();

var deadline = DateTime.UtcNow.AddSeconds(5);
while (client.IsFocused && DateTime.UtcNow < deadline) Thread.Sleep(5);
Check("focus", !client.IsFocused);
Check("notification action", await action.Task.WaitAsync(TimeSpan.FromSeconds(5)) == "a/done");
await client.DisposeAsync();

var seen = await shell.WaitAsync(TimeSpan.FromSeconds(10));
Check("hello", seen.Contains("""{"type":"hello","appId":"host-1","name":"Host","version":"1.0","protocol":0}""", StringComparison.Ordinal));
Check("title", seen.Contains("""{"type":"title","text":"title"}""", StringComparison.Ordinal));
Check("notify", seen.Contains("""{"type":"notify","id":"a","title":"t","body":"b","urgency":"high","actions":[{"id":"done","label":"Done"}]}""", StringComparison.Ordinal));
Check("launch", seen.Contains("""{"type":"launch","appId":"other","args":["x"]}""", StringComparison.Ordinal));
Check("declined", seen.Contains("""{"type":"quitDeclined"}""", StringComparison.Ordinal));
Check("bye", seen.Contains("""{"type":"bye"}""", StringComparison.Ordinal));

Environment.SetEnvironmentVariable("MINITUI_DESK_SOCKET", null);
var app = App.Create();
Check("WithDesk", ReferenceEquals(app.WithDesk("Host", out var inactive), app) && !inactive.IsActive);

Directory.Delete(directory, recursive: true);
Console.WriteLine(failures == 0 ? "All checks passed" : $"{failures} failed");
return failures == 0 ? 0 : 1;

void Check(string name, bool passed)
{
    Console.WriteLine($"{(passed ? "ok  " : "FAIL")} {name}");
    if (!passed) failures++;
}

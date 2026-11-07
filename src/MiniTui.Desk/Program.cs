using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MiniTui;
using MiniTui.Desk;

[assembly: SupportedOSPlatform("linux")]

var app = App.Create()
    .WithMouse()
    .WithHover()
    .WithTickRate(TimeSpan.FromSeconds(1)); // the clock

var shell = new DeskShell(DeskConfig.Load(DeskConfig.DefaultDirectory()));
app.Screens.Push(shell);

// The terminal going away ends the process without unwinding, so the socket goes here. MiniTui restores the terminal.
using var hangup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => shell.RemoveSocket());
using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => shell.RemoveSocket());

try
{
    app.Run();
}
finally
{
    shell.Shutdown();
}

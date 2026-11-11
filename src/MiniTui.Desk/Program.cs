using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MiniTui;
using MiniTui.Desk;

[assembly: SupportedOSPlatform("linux")]

switch (args)
{
    case []:
        break;
    case ["--version"]:
        Console.WriteLine(DeskVersion.Current);
        return 0;
    case var _ when DeskCommands.Handles(args):
        return new DeskCommands(DeskConfig.DefaultDirectory(), DeskApps.DefaultDirectory(), Environment.GetEnvironmentVariable("PATH"), Console.Out, Console.Error).Run(args);
    default:
        Console.Error.WriteLine(DeskCommands.Usage);
        return 2;
}

if (Console.IsInputRedirected || Console.IsOutputRedirected)
{
    Console.Error.WriteLine("tuidesk needs a terminal: run it in one, without redirecting its input or output.");
    return 1;
}

var app = App.Create()
    .WithMouse()
    .WithHover()
    .WithTickRate(TimeSpan.FromSeconds(1)); // the clock

var config = DeskConfig.Load(DeskConfig.DefaultDirectory(), DeskApps.DefaultDirectory(), Environment.GetEnvironmentVariable("PATH"));
var shell = new DeskShell(config with { StatePath = DeskState.DefaultPath() });
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
return 0;

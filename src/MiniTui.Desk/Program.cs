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

try
{
    app.Run();
}
finally
{
    shell.Shutdown();
}

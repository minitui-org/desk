using MiniTui;
using MiniTui.Desk.Protocol;
using MiniTui.Desk.Reader;

if (args.Length > 1 || args is ["--help" or "-h"])
{
    Console.Error.WriteLine("Usage: tuireader [folder or file]");
    return 2;
}

var app = App.Create()
    .WithTheme(Theme.Dark, Theme.Light)
    .WithChrome(statusBar: true, breadcrumb: false)
    .WithMouse()
    .WithDesk("Reader", out var desk);

var reader = new ReaderScreen(
    new Places(Places.DefaultDirectory()),
    args is [var start] ? start : Directory.GetCurrentDirectory(),
    setTitle: desk.SetTitle,
    sendToNotes: NotesLink.For(app, desk));
app.Screens.Push(reader);
DeskLink.Join(reader, desk);
app.Run();
return 0;

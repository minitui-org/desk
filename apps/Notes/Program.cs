using MiniTui;
using MiniTui.Desk.Notes;
using MiniTui.Desk.Protocol;

var app = App.Create()
    .WithTheme(Theme.Dark, Theme.Light)
    .WithChrome(statusBar: true, breadcrumb: false)
    .WithDesk("Notes", out var desk);

var notes = new NotesScreen(new NotesStore(NotesStore.DefaultDirectory()), setTitle: desk.SetTitle);
app.Screens.Push(notes);

DeskLink.Join(notes, desk);

app.Run();
notes.Save(); // nothing to do unless the app stopped some other way

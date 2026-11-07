using System.Runtime.InteropServices;
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

// A hangup or SIGTERM ends the process once the handlers return, so the note is saved first (spec §13.8)
void SaveBeforeEnding(PosixSignalContext _)
{
    var saved = new ManualResetEventSlim(); // not disposed: a save that comes late still sets it
    app.Post(() =>
    {
        notes.Save();
        saved.Set();
    });
    saved.Wait(TimeSpan.FromSeconds(2));
}
using var hangup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, SaveBeforeEnding);
using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, SaveBeforeEnding);

app.Run();
notes.Save(); // nothing to do unless the app stopped some other way

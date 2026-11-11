using MiniTui;
using MiniTui.Desk.Notes;
using MiniTui.Desk.Protocol;

var app = App.Create()
    .WithTheme(Theme.Dark, Theme.Light)
    .WithChrome(statusBar: true, breadcrumb: false)
    .WithMouse()
    .WithDesk("Notes", out var desk, manifest: new DeskManifest { Id = "notes", Description = "Markdown notes in plain files" }); // as tuidesk lists it (spec §16.4)

// tuinotes --append <name> <text> (spec §13.8): how Reader sends a passage
(string, string)? append = args is ["--append", var name, var text] ? (name, text) : null;
if (append is null && args.Length > 0)
{
    Console.Error.WriteLine("Usage: tuinotes [--append <name> <text>]");
    return 2;
}

var notes = new NotesScreen(new NotesStore(NotesStore.DefaultDirectory()), setTitle: desk.SetTitle, append: append);
app.Screens.Push(notes);

DeskLink.Join(notes, desk);

app.Run();
notes.Save(); // nothing to do unless the app stopped some other way
return 0;

using MiniTui;
using MiniTui.Desk.Protocol;
using MyApp;

var app = App.Create()
    .WithTheme(Theme.Dark, Theme.Light)
    .WithChrome(statusBar: true, breadcrumb: false)
    .WithMouse()
    // Joins Desk when it runs in it, and does nothing otherwise. The manifest is what `tuidesk add myapp` saves.
    .WithDesk("MyApp", out var desk, manifest: new DeskManifest { Id = "my-app-id", Description = "A MiniTui Desk app" });

// Inside Desk, notifications go to Desk's queue; outside it they're toasts
var screen = new MainScreen(setTitle: desk.SetTitle, notify: desk.IsActive ? desk.Notify : null);
app.Screens.Push(screen);

// When Desk asks the app to quit, it quits. To save first, or ask, handle desk.QuitRequested, and call
// desk.DeclineQuit() if the user chooses to stay.
app.Run();

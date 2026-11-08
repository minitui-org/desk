using MiniTui;
using MiniTui.Desk.Protocol;
using MiniTui.Desk.Tasks;

var app = App.Create()
    .WithTheme(Theme.Dark, Theme.Light)
    .WithChrome(statusBar: true, breadcrumb: false)
    .WithMouse()
    .WithDesk("Tasks", out var desk);

// Inside Desk, reminders go to the shell; outside it they're toasts
var tasks = new TasksScreen(new TaskStore(TaskStore.DefaultDirectory()), setTitle: desk.SetTitle,
    notify: desk.IsActive ? desk.Notify : null);
app.Screens.Push(tasks);

DeskLink.Join(tasks, desk);

app.Run();

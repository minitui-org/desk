using System.Globalization;
using MiniTui;
using MiniTui.Desk.Protocol;

// desk-test-app <name> <log>: logs what the shell says, and runs a command for each line typed.
//   title <text>                                   underscores are spaces, here and in notify
//   notify <id> <urgency> <title> <body> [action=Label]...
//   launch <appId> [args]...
//   decline | stay                                 answer quit requests by declining, or by doing nothing
//   later <ms> <command>                           run a command after a while, wherever the app is
//   exit <code>
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: desk-test-app <name> <log>");
    return 2;
}

var log = args[1];
void Log(string line) => File.AppendAllText(log, line + "\n");

var exitCode = 0;
var app = App.Create().WithDesk(args[0], out var desk);
Log($"started active={desk.IsActive} theme={desk.Theme ?? "none"}");
desk.FocusChanged += focused => Log($"focus {focused}");
desk.NotificationAction += (id, action) => Log($"action {id} {action}");

void Run(string command)
{
    var words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (words.Length == 0) return;
    static string Spaced(string word) => word.Replace('_', ' ');
    switch (words[0])
    {
        case "title":
            desk.SetTitle(Spaced(string.Join(' ', words[1..])));
            break;
        case "notify" when words.Length >= 5:
            var urgency = Enum.Parse<DeskUrgency>(words[2], ignoreCase: true);
            var actions = words[5..].Select(w => w.Split('=', 2)).Select(p => new DeskAction(p[0], Spaced(p[1]))).ToArray();
            desk.Notify(new DeskNotification(words[1], Spaced(words[3]), Spaced(words[4])) { Urgency = urgency, Actions = actions });
            break;
        case "launch" when words.Length >= 2:
            desk.Launch(words[1], words[2..]);
            break;
        case "decline":
            desk.QuitRequested += () =>
            {
                Log("quitRequest declined");
                desk.DeclineQuit();
            };
            break;
        case "stay":
            desk.QuitRequested += () => Log("quitRequest ignored");
            break;
        case "later" when words.Length >= 3:
            var delay = int.Parse(words[1], CultureInfo.InvariantCulture);
            var rest = string.Join(' ', words[2..]);
            _ = Task.Delay(delay).ContinueWith(_ => app.Post(() => Run(rest)), TaskScheduler.Default);
            break;
        case "exit" when words.Length >= 2:
            exitCode = int.Parse(words[1], CultureInfo.InvariantCulture);
            app.Quit();
            break;
    }
    Log("ran " + command);
}

app.Screens.Push(new CommandScreen(args[0], Run));
app.Run();
Log("exiting");
return exitCode;

internal sealed class CommandScreen(string name, Action<string> run) : Screen
{
    private string _typed = "";

    public override string Title => name;

    protected override void OnMount() => App.OnKey(key =>
    {
        if (key.Key == ConsoleKey.Enter)
        {
            var command = _typed;
            _typed = "";
            run(command);
        }
        else if (key.Key == ConsoleKey.Backspace && _typed.Length > 0)
        {
            _typed = _typed[..^1];
        }
        else if (!char.IsControl(key.KeyChar))
        {
            _typed += key.KeyChar;
        }
        return true;
    });

    public override void Render(CellBuffer buf, Rect area, RenderContext ctx)
    {
        ArgumentNullException.ThrowIfNull(buf);
        buf.SetString(area.X, area.Y, $"{name} ready", default, area.Width);
        buf.SetString(area.X, area.Y + 1, "> " + _typed, default, area.Width);
    }
}

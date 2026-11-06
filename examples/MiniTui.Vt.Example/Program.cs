using System.Runtime.Versioning;
using MiniTui;
using MiniTui.Vt;

[assembly: SupportedOSPlatform("linux")]

// A shell in a pane of an ordinary MiniTui app, with the terminal's state beside it. Every key goes to the shell,
// except Ctrl+Q, which quits. `dotnet run --project examples/MiniTui.Vt.Example -- nvim` runs something else.
//
// This shows how MiniTui.Vt fits into an app. The real shell is tuidesk, which comes with Phase 1d.

var command = args.Length > 0 ? args[0] : Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh";

var app = App.Create().WithMouse().WithHover();
var info = new VtStartInfo(command) { Args = args.Skip(1).ToArray() };
using var terminal = VtTerminal.Start(info);

var bells = 0;
var lastNotification = "";
var pane = default(Rect);

terminal.Changed += app.RequestRedraw;
terminal.Exited += () => app.Post(app.Quit);
terminal.Bell += () => app.Post(() => bells++);
terminal.ClipboardSet += text => app.Post(() => Clipboard.SetText(text));
terminal.Notified += (title, body) => app.Post(() => lastNotification = title is null ? body : title + ": " + body);

// The hosted program asks about these to choose a light or dark look, so it's told what the real terminal said.
app.OnEvent(e =>
{
    if (e is not TerminalInfoEvent { Info: var terminalInfo }) return;
    lock (terminal.Lock)
    {
        if (terminalInfo.Foreground is { } foreground) terminal.Screen.DefaultForeground = foreground;
        if (terminalInfo.Background is { } background) terminal.Screen.DefaultBackground = background;
    }
});

app.OnKey(key =>
{
    if (key.Key == ConsoleKey.Q && key.Modifiers == ConsoleModifiers.Control)
    {
        app.Quit();
        return true;
    }
    terminal.HandleKey(key);
    return true;
});

app.OnPaste(text =>
{
    terminal.HandlePaste(text);
    return true;
});

// Positions are relative to the pane. Outside it, or when the program didn't ask, the event goes nowhere.
app.OnMouse(mouse =>
{
    var inside = mouse.X >= pane.X && mouse.X < pane.Right && mouse.Y >= pane.Y && mouse.Y < pane.Bottom;
    if (inside) terminal.HandleMouse(mouse with { X = mouse.X - pane.X, Y = mouse.Y - pane.Y });
    return true;
});

app.Render((buf, area) =>
{
    var columns = Layout.Split(area, Direction.Horizontal, Constraint.Fill(1), Constraint.Length(30));
    var block = new BlockBuilder().Padding(new Margin(0)).Title(" " + command + " ");
    pane = block.Inner(columns[0]);
    new TerminalViewBuilder().Terminal(terminal).Focused().Block(block).Draw(buf, columns[0]);

    VtModes modes;
    string? title;
    (int Width, int Height) size;
    lock (terminal.Lock)
    {
        modes = terminal.Screen.Modes;
        title = terminal.Screen.Title;
        size = (terminal.Screen.Width, terminal.Screen.Height);
    }

    var status = string.Join('\n',
        $"size      {size.Width}x{size.Height}",
        $"alt       {modes.AlternateScreen}",
        $"cursor    {(modes.ApplicationCursorKeys ? "application" : "normal")}",
        $"keypad    {(modes.ApplicationKeypad ? "application" : "normal")}",
        $"kitty     {modes.KeyboardFlags}",
        $"mouse     {modes.Mouse}",
        $"encoding  {modes.MouseEncoding}",
        $"paste     {(modes.BracketedPaste ? "bracketed" : "plain")}",
        $"focus     {modes.FocusReporting}",
        $"sync      {modes.SynchronizedOutput}",
        $"bells     {bells}",
        "",
        "title",
        title ?? "(none)",
        "",
        "notification",
        lastNotification.Length == 0 ? "(none)" : lastNotification,
        "",
        "Ctrl+Q quits");
    new ParagraphBuilder().Text(status).Wrap(WrapMode.Char).Block(new BlockBuilder().Title(" Terminal ")).Draw(buf, columns[1]);
});

app.Run();

if (terminal.Exit is { } exit) Console.WriteLine(command + ": " + exit);

// Disposing hangs up the terminal, which ends the program unless it ignores that.
terminal.Hangup();

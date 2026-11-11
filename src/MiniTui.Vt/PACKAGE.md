# MiniTui.Vt

A terminal emulator for [MiniTui](https://minitui.org): run a program in a pseudo-terminal and draw its screen in a MiniTui app, as a pane like any other widget.

```csharp
var terminal = VtTerminal.Start(new VtStartInfo("bash") { Width = 80, Height = 24 });
// in Render:
new TerminalViewBuilder().Terminal(terminal).Focused(true).Draw(buf, area);
```

Colors, styles, the alternate screen, scroll regions, wide characters, mouse and keyboard modes, bracketed paste, synchronized output and terminal queries are handled; keys, mouse and pastes are encoded back the way the program asked. It's what [MiniTui Desk](https://www.nuget.org/packages/MiniTui.Desk) runs every app in. Linux only, AOT-safe, and depends on MiniTui only.

Example: [an app with a shell pane](https://github.com/minitui-org/desk/blob/main/examples/MiniTui.Vt.Example). Docs: [minitui.org/desk](https://minitui.org/desk).

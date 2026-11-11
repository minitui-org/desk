# MiniTui Desk

`tuidesk`: a focus-first workspace in the terminal, built with [MiniTui](https://minitui.org). One app fills the screen, or two share it, with a status line of what's running, quiet notifications, and `Ctrl+Space` as the only key it takes from apps by default.

```bash
dotnet tool install -g MiniTui.Desk
tuidesk
```

Any terminal program runs in it. Apps made with MiniTui and `MiniTui.Desk.Protocol` also show their name and status in the status line, send notifications, and open each other. Linux, x64 and arm64.

Docs: [minitui.org/desk](https://minitui.org/desk). Source and changelog: [github.com/minitui-org/desk](https://github.com/minitui-org/desk).

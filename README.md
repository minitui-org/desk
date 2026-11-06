# MiniTui Desk

A distraction-free, terminal-based desktop environment for Linux, built on
[MiniTui](https://github.com/minitui-org/minitui).

Early days: there's no desktop yet. The plan is in [`docs/SPEC.md`](docs/SPEC.md).

To try the terminal emulator it's built on, run a shell in a pane of an ordinary MiniTui app. Ctrl+Q quits.

```bash
dotnet run --project examples/MiniTui.Vt.Example
dotnet run --project examples/MiniTui.Vt.Example -- nvim
```

MIT licensed, © 2026 Discover Edge LLC.

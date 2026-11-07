# MiniTui Desk

A distraction-free, terminal-based desktop environment for Linux, built on
[MiniTui](https://github.com/minitui-org/minitui).

Early days. The plan is in [`docs/SPEC.md`](docs/SPEC.md). What exists today is `tuidesk`: one app on the screen, one
row of status, and one key taken from the app. It needs nothing but the .NET 10 SDK to try, and opens your shell.

```bash
dotnet run --project src/MiniTui.Desk
```

Every key goes to the app except `Ctrl+Space`. After it, press one key: `Space` starts an app, `Tab` switches, `1`-`9`
go to a position, `x` closes the app, `q` quits, and `?` lists the rest.

Add apps to `~/.config/tuidesk/apps.json`, and set the prefix or turn Do Not Disturb off in `config.json`:

```json
[{ "id": "k9s", "name": "Kubernetes", "command": "k9s", "args": [], "cwd": "~", "env": {} }]
```
```json
{ "prefix": "ctrl+space", "doNotDisturb": true }
```

To try the terminal emulator on its own, run a shell in a pane of an ordinary MiniTui app. Ctrl+Q quits.

```bash
dotnet run --project examples/MiniTui.Vt.Example
dotnet run --project examples/MiniTui.Vt.Example -- nvim
```

MIT licensed, © 2026 Discover Edge LLC.

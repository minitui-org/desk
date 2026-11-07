# MiniTui Desk

A distraction-free, terminal-based desktop environment for Linux, built on
[MiniTui](https://github.com/minitui-org/minitui).

Early days. The plan is in [`docs/SPEC.md`](docs/SPEC.md). What exists today is `tuidesk`: one app on the screen, one
row of status, and one key taken from the app. It needs nothing but the .NET 10 SDK to try, and opens your shell.

```bash
dotnet run --project src/MiniTui.Desk
```

Every key goes to the app except `Ctrl+Space`, which opens the launcher. Type to filter and press `Enter` to start an
app, `Tab` swaps to the list of running apps, and `Esc` closes. A footer shows the commands: `Ctrl+X` closes the app,
`Ctrl+Q` quits, and `F1` lists the rest.

Add apps to `~/.config/tuidesk/apps.json`, and set the prefix or turn Do Not Disturb off in `config.json`:

```json
[{ "id": "k9s", "name": "Kubernetes", "command": "k9s", "args": [], "cwd": "~", "env": {} }]
```
```json
{ "prefix": "ctrl+space", "doNotDisturb": true }
```

Notes is the first app made for Desk: Markdown notes in plain files, saved as you type. Run it on its own, or add it
to `apps.json` to see the open note in Desk's status line. Its keys are in [`apps/Notes`](apps/Notes/README.md).

```bash
dotnet run --project apps/Notes
```

To try the terminal emulator on its own, run a shell in a pane of an ordinary MiniTui app. Ctrl+Q quits.

```bash
dotnet run --project examples/MiniTui.Vt.Example
dotnet run --project examples/MiniTui.Vt.Example -- nvim
```

MIT licensed, © 2026 Discover Edge LLC.

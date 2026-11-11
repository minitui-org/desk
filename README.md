# MiniTui Desk

A distraction-free, terminal-based desktop environment for Linux, built on
[MiniTui](https://github.com/minitui-org/minitui).

Early days. The plan is in [`docs/SPEC.md`](docs/SPEC.md), and the guides at
[minitui.org/desk](https://minitui.org/desk), written in [`docs/site`](docs/site). What exists today is `tuidesk`: one app on the screen, one
row of status, and one key taken from the app. It needs nothing but the .NET 10 SDK to try, and opens your shell.

```bash
dotnet run --project src/MiniTui.Desk
```

Desk and its apps install as .NET tools, each a Native AOT binary for linux-x64 or linux-arm64:

```bash
dotnet tool install -g MiniTui.Desk         # tuidesk
dotnet tool install -g MiniTui.Desk.Notes   # tuinotes, and .Tasks and .Reader likewise
```

To make an app for Desk, `dotnet new install MiniTui.Desk.Templates`, then `dotnet new tuidesk-app -n Weather`: a
MiniTui app with `WithDesk` and a manifest, tests with `ComponentHarness`, and packing as a tool, with a README that
says how to install it and `tuidesk add` it. The template is in [`templates/`](templates/content/tuidesk-app).

`tools/pack.sh <folder> any` packs the libraries, the template and the tools' top-level packages, and `tools/pack.sh <folder>
linux-x64` each tool's binary for that platform, which has to be built on it. `dotnet tool install MiniTui.Desk
--tool-path <dir> --add-source <folder>` then installs from the folder.

Every key goes to the app except `Ctrl+Space`, which opens the launcher, and `Alt+H` and `Alt+L`, which go to the
previous and the next running app, in the order the status line lists them. `Alt+J` goes to the other pane when two apps are
showing. In the launcher, type to filter and press `Enter` to start an
app, `Tab` swaps to the list of running apps, and `Esc` closes. A footer shows the commands: `Ctrl+X` closes the app,
`Ctrl+Q` quits, and `F1` lists the rest.
The first time Desk starts, a welcome lists these keys, and the status line shows `Ctrl+Space menu` until you
first press it. To see them again, delete `~/.local/state/tuidesk/state.json`.

Two apps can show at once. In the launcher or the switcher, `Ctrl+R` opens the selected app in a pane to the right of
the current one and `Ctrl+B` in a pane below it, where `Enter` opens it in the current pane. `Ctrl+O` goes back to one
pane, and the other app keeps running. There are never more than two. A click in a pane focuses it, and an app that
opens another, as Reader does with Notes, opens it in the other pane.

`tuidesk add <command>` puts an app in the launcher. An app made with `MiniTui.Desk.Protocol` says its own id and
name; any other program needs `--id` and `--name`, and its arguments go after `--`. `tuidesk apps` lists what the
launcher shows and where each came from, and `tuidesk remove <id>` takes one out. Notes, Tasks and Reader are there
without being added when they're on `PATH`.

```bash
tuidesk add k9s --id k9s --name Kubernetes -- -A
```

Or write apps into `~/.config/tuidesk/apps.json`, which wins over the others on an id, and set the keys or turn Do Not Disturb off in `config.json`. Set `nextApp`,
`previousApp` or `otherPane` to `""` to give that key back to the apps:

```json
[{ "id": "k9s", "name": "Kubernetes", "command": "k9s", "args": [], "cwd": "~", "env": {} }]
```
```json
{ "prefix": "ctrl+space", "nextApp": "alt+l", "previousApp": "alt+h", "otherPane": "alt+j", "doNotDisturb": true }
```

Notes is the first app made for Desk: Markdown notes in plain files, saved as you type. Run it on its own, or in Desk, where
the status line shows the open note. Its keys are in [`apps/Notes`](apps/Notes/README.md).

```bash
dotnet run --project apps/Notes
```

Tasks is the second: a to-do list with due dates in a todo.txt file, which reminds you when something falls due. In
Desk the reminders are notifications you can mark done or snooze. Its keys are in [`apps/Tasks`](apps/Tasks/README.md).

```bash
dotnet run --project apps/Tasks
```

Reader is the third: it reads Markdown and text files where they are, formatted, and `Ctrl+N` sends the highlighted
passage to Notes, quoted, with room to write under it. Its keys are in [`apps/Reader`](apps/Reader/README.md).

```bash
dotnet run --project apps/Reader -- ~/docs
```

To try the terminal emulator on its own, run a shell in a pane of an ordinary MiniTui app. Ctrl+Q quits.

```bash
dotnet run --project examples/MiniTui.Vt.Example
dotnet run --project examples/MiniTui.Vt.Example -- nvim
```

MIT licensed, © 2026 Discover Edge LLC.

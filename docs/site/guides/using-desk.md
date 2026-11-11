# Using Desk

Install Desk, learn its keys, put your programs in its launcher, and set it up.

## Install

Desk and its apps are .NET tools, each a Native AOT binary for Linux x64 or arm64. They need the
[.NET 10 SDK](https://dotnet.microsoft.com/download):

```bash
dotnet tool install -g MiniTui.Desk          # tuidesk
dotnet tool install -g MiniTui.Desk.Notes    # tuinotes: Markdown notes
dotnet tool install -g MiniTui.Desk.Tasks    # tuitasks: a to-do list with reminders
dotnet tool install -g MiniTui.Desk.Reader   # tuireader: Markdown and text, formatted to read
```

.NET puts tools in `~/.dotnet/tools`, which has to be on `PATH`. Then start Desk in any terminal:

```bash
tuidesk
```

It opens your shell. The first time, a welcome lists the keys below, and the status line shows `Ctrl+Space menu`
until you first press it. `dotnet tool update -g MiniTui.Desk` updates it.

## Keys

Every key goes to the app on the screen except these:

| Key | Does |
|---|---|
| `Ctrl+Space` | Opens the launcher: the prefix |
| `Alt+L`, `Alt+H` | The next and the previous running app, in the order the status line lists them |
| `Alt+J` | The other pane, when two apps are showing |

In the launcher, type to filter and `Enter` starts the selected app. `Tab` swaps to the list of running apps,
and `Esc` closes. The launcher's footer shows its commands:

| Key | Does |
|---|---|
| `Ctrl+R`, `Ctrl+B` | Opens the selected app in a pane to the right, or below |
| `Ctrl+O` | Back to one pane. The other app keeps running |
| `Ctrl+N` | The notifications |
| `Ctrl+D` | Do Not Disturb on or off |
| `Ctrl+S` | Sends `Ctrl+Space` to the app, for a program that uses it |
| `Ctrl+X` | Closes the app on the screen |
| `Ctrl+Q` | Quits Desk, closing every app |
| `F1` | Help: these keys, and where the settings are |

## Apps and panes

Each app runs in a terminal of its own, so anything that runs in a terminal runs in Desk. The status line lists the
running apps, with the one on the screen first and its title after its name.

Two apps can show at once, and never more. A click in a pane focuses it, and an app that opens another, as Reader
does with Notes, opens it in the other pane.

An app that exits normally closes, and the previous one comes to the front. One that fails leaves its last screen
frozen and says how it ended, with `r` to restart it and `c` to close it. When the last app closes, the launcher
opens, so typing `exit` once doesn't end the session.

## Notifications

Apps send notifications through the [protocol](protocol.md), or with a terminal's OSC 9 or OSC 777, and a bell or a
failure from an app in the background adds one too. Do Not Disturb is on by default: notifications only add to the
count in the status line, and `Ctrl+N` in the launcher lists them. With it off, all but the `low` ones also appear for a moment. A
notification with actions, such as Tasks' "Done" and "Snooze 1 hour", lists them on `Enter`.

## The launcher's apps

The launcher always has the shell. Notes, Tasks and Reader are there whenever they're on `PATH`. Anything else is
added:

```bash
tuidesk add weather                                  # an app that says its own id and name
tuidesk add k9s --id k9s --name Kubernetes -- -A     # any other program, with its arguments after --
tuidesk apps                                         # what the launcher shows, and where each came from
tuidesk remove k9s
```

An app made with MiniTui Desk's protocol package says its own id, name and description: that's its
[manifest](manifest.md). Any other program needs `--id` and `--name`. A running Desk shows the change from its next
start.

Apps can also be written by hand into `~/.config/tuidesk/apps.json`, which wins over an added app or a found one with
the same id:

```json
[{ "id": "k9s", "name": "Kubernetes", "command": "k9s", "args": ["-A"], "cwd": "~", "env": {} }]
```

Only `command` is needed. The id is what other apps open it by, so `notes` here replaces the Notes found on `PATH`
for Reader's `Ctrl+N` too.

## Settings

`~/.config/tuidesk/config.json` sets the keys and Do Not Disturb. Each is optional, and these are the defaults:

```json
{ "prefix": "ctrl+space", "nextApp": "alt+l", "previousApp": "alt+h", "otherPane": "alt+j", "doNotDisturb": true }
```

Set `nextApp`, `previousApp` or `otherPane` to `""` to give that key back to the apps. A file that can't be read, or a
name that isn't a key, is ignored, and a notification says why.

## Files

| File | Holds |
|---|---|
| `~/.config/tuidesk/config.json` | The settings above |
| `~/.config/tuidesk/apps.json` | Apps written by hand |
| `~/.local/share/tuidesk/apps/<id>.json` | Apps saved by `tuidesk add` |
| `~/.local/state/tuidesk/state.json` | Whether the welcome and the hint have been seen. Delete it to see them again |

Each folder follows the XDG variable when it's set: `$XDG_CONFIG_HOME`, `$XDG_DATA_HOME` and `$XDG_STATE_HOME`.

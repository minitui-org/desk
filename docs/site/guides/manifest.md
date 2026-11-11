# The manifest

An app's manifest says how Desk lists it and how other apps open it:

```json
{ "id": "notes", "name": "Notes", "description": "Markdown notes in plain files", "command": "tuinotes", "args": [] }
```

| Field | Means |
|---|---|
| `id` | What `launch` opens it by, and its file's name. Lowercase letters, digits and `-`; `shell` is taken |
| `name` | What the launcher and the status line call it |
| `description` | A line about it. Desk keeps it, but doesn't show it yet |
| `command` | The program, as typed: a name found on `PATH`, or a path |
| `args` | Its arguments, before any that `launch` adds |

An added app can also have the `cwd` and `env` of an [`apps.json` entry](using-desk.md#the-launchers-apps), written
into its file by hand.

## Declaring it

A MiniTui app declares its manifest in `WithDesk`. The name is `WithDesk`'s, and the command is whatever
`tuidesk add` was given:

```csharp
app.WithDesk("Notes", out var desk, manifest: new DeskManifest { Id = "notes", Description = "Markdown notes in plain files" });
```

An app without one has only a name, so `tuidesk add` needs `--id` for it.

## Manifest mode

With `MINITUI_DESK_MANIFEST=1` in its environment, `WithDesk` writes the manifest as one line of JSON and exits 0,
before the app touches the terminal:

```console
$ MINITUI_DESK_MANIFEST=1 tuinotes
{"id":"notes","name":"Notes","description":"Markdown notes in plain files"}
```

That's what `tuidesk add <command>` does: it runs the command so, gives it 3 seconds, and saves what it says. The
first `{` on a line starts the answer, so whatever a terminal library writes first doesn't matter. A program that
says nothing, says something that isn't a manifest, or exits with another code needs `--id` and `--name`, and is
added as a plain terminal app.

A program in any language can answer: check the variable before doing anything else, print the line and exit.

```python
import json, os, sys

if os.environ.get("MINITUI_DESK_MANIFEST") == "1":
    print(json.dumps({"id": "weather", "name": "Weather", "description": "The forecast"}))
    sys.exit(0)
```

## Where they're kept

`tuidesk add` saves each in `~/.local/share/tuidesk/apps/<id>.json` (`$XDG_DATA_HOME/tuidesk/apps`), and Desk reads
them at startup. `tuidesk remove <id>` deletes one, and adding an id again replaces it.

When two have the same id, the first of these wins:

1. An entry in `~/.config/tuidesk/apps.json`, so a hand-written one always overrides.
2. A manifest that `tuidesk add` saved.
3. Notes, Tasks and Reader, as `tuinotes`, `tuitasks` and `tuireader` on `PATH`, which are in the launcher without
   being added.

`tuidesk apps` lists what the launcher shows and which of these each came from. A file that can't be read, or whose
id isn't its file's name, is skipped, and a notification in Desk says why.

# Notes

`tuinotes`: Markdown notes in plain files, with a list and an editor. It runs on its own or inside `tuidesk`, where
the status line shows the open note. Spec: [`docs/SPEC.md`](../../docs/SPEC.md) §13.8.

```bash
dotnet run --project apps/Notes
```

Notes are `<name>.md` files in `$XDG_DATA_HOME/tuinotes/`, normally `~/.local/share/tuinotes/`, so any other editor,
`grep` or `git` can use them too. Everything is saved as you go: a second after you stop typing, and whenever you
switch notes, switch apps in Desk or quit. There's no save key. If a note changes on disk while you have unsaved
text in it, your text goes in a new note, `<name> (conflict)`, rather than over the other one.

| Key | Where | Does |
|---|---|---|
| `↑` `↓` `PgUp` `PgDn` `Home` `End` | List | Select a note, which shows in the editor |
| Typing | List | Filter by name. `Esc` clears the filter |
| `Enter`, `Tab` | List | Edit the selected note |
| `Esc`, `Tab` | Editor | Back to the list |
| `Ctrl+N` | Both | New note, named from the filter if you typed one |
| `F2` | Both | Rename the selected note |
| `Delete` | List | Delete the selected note, after asking |
| `Ctrl+Q` | Both | Quit |

The editor is MiniTui's `TextArea`: `Ctrl+Z` and `Ctrl+Y` undo and redo, `Ctrl+F` finds, `Shift` with the arrows
selects, and `Ctrl+C`, `Ctrl+X` and `Ctrl+V` use the clipboard.

To try it inside Desk, add it to `~/.config/tuidesk/apps.json`:

```json
[{ "id": "notes", "name": "Notes", "command": "/path/to/tuinotes" }]
```

## Gaps in MiniTui

What Notes needed from MiniTui and didn't find (spec §13.1). Each is a MiniTui issue.

- **`TextAreaState` says nothing when its text changes** ([#74](https://github.com/minitui-org/minitui/issues/74)).
  `TextFieldState` has `Changed`, but the editor has no event or version, so Notes compares the whole text after
  every key to know whether there's anything to save.
- **No way to run code before the app ends** ([#75](https://github.com/minitui-org/minitui/issues/75)).
  `Ctrl+C` calls `App.Quit` without consulting any key map unless the focused component takes the key, and a hangup
  or `SIGTERM` restores the terminal and lets the process end. Notes takes `Ctrl+C` in each of its components and
  registers its own signal handlers to save first.
- **No text prompt** ([#76](https://github.com/minitui-org/minitui/issues/76)). `Overlays` can confirm,
  show a message or a menu, but can't ask for a line of text, so Notes has its own `NamePrompt` for new and renamed
  notes.
- **The filterable list is internal** ([#77](https://github.com/minitui-org/minitui/issues/77)). The palette's
  filter line and fuzzy-matched rows (`FilterList`) aren't public, so Notes builds its own from `Fuzzy` and
  `TextFieldState`, as `tuidesk`'s launcher had to.
- **`TextArea` can't style parts of the text** ([#78](https://github.com/minitui-org/minitui/issues/78)). It
  draws every line in one style, so Markdown headings, emphasis and code can't be shown as such. Notes edits plain
  text for now.
- **A toast is one line** ([#79](https://github.com/minitui-org/minitui/issues/79)). A long message is cut off
  with `…` rather than wrapped, so Notes keeps its messages short.

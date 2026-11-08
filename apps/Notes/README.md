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
| Typing | List | Filter by name, opening the best match. `Esc` clears the filter |
| Click, double-click | List | Open a note; edit it |
| `Enter`, `Tab` | List | Edit the selected note |
| `Esc` | Editor | Back to the list |
| `Tab`, `Shift+Tab` | Editor | Indent and outdent the line, or the selected lines |
| `Ctrl+N` | Both | New note, named from the filter if you typed one |
| `F2` | Both | Rename the selected note |
| `Delete` | List | Delete the selected note, after asking |
| `F1` | Both | Show the keys (`?` is typed here) |
| `Ctrl+Q` | Both | Quit |

The editor is MiniTui's `TextArea`: `Ctrl+Z` and `Ctrl+Y` undo and redo, `Ctrl+F` finds, `Shift` with the arrows
selects, and `Ctrl+C`, `Ctrl+X` and `Ctrl+V` use the clipboard.
`Tab` indents with spaces, 4 at a time, and Enter keeps the line's indentation. Tab characters in a note
made elsewhere become spaces when Notes next saves it.

To try it inside Desk, add it to `~/.config/tuidesk/apps.json`:

```json
[{ "id": "notes", "name": "Notes", "command": "/path/to/tuinotes" }]
```

## Gaps in MiniTui

What Notes needed from MiniTui and didn't find (spec §13.1). Each was a MiniTui issue, fixed in MiniTui 2.2.0
(the first six) and 2.3.0, which Notes now uses:

- **`TextAreaState` said nothing when its text changed** ([#74](https://github.com/minitui-org/minitui/issues/74)).
  Notes now saves from `TextAreaState.Changed`.
- **No way to run code before the app ended** ([#75](https://github.com/minitui-org/minitui/issues/75)). Notes
  took `Ctrl+C` in each component and registered its own signal handlers; now `App.OnQuitting` saves on `Ctrl+C`, a
  hangup and `SIGTERM`.
- **No text prompt** ([#76](https://github.com/minitui-org/minitui/issues/76)). Notes' own `NamePrompt` is gone for
  `OverlayStack.PromptAsync`.
- **The filterable list was internal** ([#77](https://github.com/minitui-org/minitui/issues/77)). The notes list is
  now `FilterListState` and `Widget.FilterList`, as is `tuidesk`'s launcher.
- **`TextArea` couldn't style parts of the text** ([#78](https://github.com/minitui-org/minitui/issues/78)).
  `TextAreaBuilder.Highlight` can now; Notes still edits Markdown as plain text (spec §13.8).
- **A toast was one line** ([#79](https://github.com/minitui-org/minitui/issues/79)). Toasts wrap now.
- **Help couldn't be opened while typing** ([#85](https://github.com/minitui-org/minitui/issues/85)), and in Notes
  the focus is always in the filter or the editor, where `?` is typed. `F1` opens it now.

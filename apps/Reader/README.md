# Reader

`tuireader`: reads Markdown and text files where they are, formatted, and sends passages to Notes. It runs on its
own or inside `tuidesk`, where the status line shows the open file. Spec: [`docs/SPEC.md`](../../docs/SPEC.md) §13.10.

```bash
dotnet run --project apps/Reader -- ~/docs          # a folder to browse
dotnet run --project apps/Reader -- ~/docs/plan.md  # a file to read, with its folder in the list
```

The list shows the folder's subfolders, then its `.md`, `.markdown` and `.txt` files. With no argument it starts in
the current folder, which inside Desk is the `apps.json` entry's `cwd`. Markdown is formatted: headings styled, marks
like `**` and `#` hidden, lists and quotes wrapped under their bullets and bars, code as written. Text files are shown
as written. The page wraps to the full width of its pane.

One block is always highlighted, with a bar beside it: a heading, a paragraph, a list item, a quote or a code block.
That's the passage, unless you've selected text with the mouse.

| Key | Where | Does |
|---|---|---|
| `↑` `↓` `PgUp` `PgDn` `Home` `End` | List | Select a file or folder, and show a selected file |
| Typing, `Backspace` | List | Filter by name. `Esc` clears the filter |
| `Enter`, `→` | List | Go into a folder, or read a file |
| `←`, `Backspace` with nothing typed | List | Up to the folder above |
| `Tab` | Both | Swap between the list and the page |
| `↑` `↓` | Page | Move the highlight a block |
| `Shift+↑` `Shift+↓` | Page | Stretch the highlight over the block above or below |
| `PgUp` `PgDn` `Home` `End` | Page | Scroll, taking the highlight along |
| Click, drag, wheel | Page | Highlight a block; select text; scroll |
| `Ctrl+N` | Page | Send the passage to Notes |
| `Ctrl+C` | Page | Copy the passage |
| `F7` | Page | Show a caret: the arrows move it and `Shift` with them selects text. `F7` or `Esc` hides it |
| `Esc` | Page | Clear a selection, hide the caret, or back to the list |
| `F1` | Both | Show the keys |
| `Ctrl+Q` | Both | Quit |

**To Notes:** `Ctrl+N` adds the passage to the note named after the file, `garden-plan` for `garden-plan.md`, as a
quote with where it came from, and opens Notes there with the cursor under it so you can write:

```
> Tomatoes need six hours of sun
> and deep watering twice a week.

— ~/docs/garden-plan.md
```

Inside Desk, Reader asks Desk to open the `apps.json` entry whose id is `notes`, so Notes must be in `apps.json`
with that id. Outside Desk, Reader runs `tuinotes` in the same terminal, the one next to `tuireader` or on `PATH`,
and comes back when you quit it.

Reader remembers where you were in the last 200 files, in `$XDG_DATA_HOME/tuireader/places.txt`.

Reader uses the mouse, so to select the terminal's own text, hold Shift while you drag.

To try it inside Desk, add both to `~/.config/tuidesk/apps.json`:

```json
[
  { "id": "reader", "name": "Reader", "command": "/path/to/tuireader", "cwd": "~/docs" },
  { "id": "notes", "name": "Notes", "command": "/path/to/tuinotes" }
]
```

## Gaps in MiniTui

What Reader needed from MiniTui and didn't find (spec §13.1), each a MiniTui issue. All three are fixed in MiniTui
2.4.0, and Reader uses the fixes:

- **No Markdown widget** ([#89](https://github.com/minitui-org/minitui/issues/89)). Reader formatted a CommonMark
  subset itself; that code moved into MiniTui as `Widget.Markdown` and `MarkdownState`, with the theme's new
  `Heading`, `Code` and `Link` styles, and the block highlight as its block cursor.
- **Paragraph text can only be selected with the mouse** ([#90](https://github.com/minitui-org/minitui/issues/90)).
  `F7` now shows a caret for selecting with the keyboard.
- **Wrapping styled text into rows is internal** ([#91](https://github.com/minitui-org/minitui/issues/91)). Now
  `Paragraph.Wrap`, which the Markdown widget uses.

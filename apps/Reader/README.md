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
as written. The page is at most 80 columns wide.

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
| `Esc` | Page | Clear a selection, or back to the list |
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

What Reader needed from MiniTui and didn't find (spec §13.1), each a MiniTui issue:

- **No Markdown widget** ([#89](https://github.com/minitui-org/minitui/issues/89)). Reader formats a CommonMark
  subset itself, in `Markdown.cs`, `Inline.cs` and `Page.cs`, which could move into MiniTui. The theme has no heading,
  code or link styles either, so Reader uses `TitleFocused` for headings and `Info` for code and links.
- **Paragraph text can only be selected with the mouse** ([#90](https://github.com/minitui-org/minitui/issues/90)).
  So the passage is a highlighted block you move with the arrows, and a mouse drag still selects exact text.
- **Wrapping styled text into rows is internal** ([#91](https://github.com/minitui-org/minitui/issues/91)). Reader
  wraps its rows itself, to know which block each row belongs to and to indent wrapped lines under a bullet.

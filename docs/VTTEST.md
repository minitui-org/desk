# `VtScreen` against vttest and tmux

How `VtScreen` was checked in Phase 1b, on 2026-11-05, with vttest 2.7 (20230201) and tmux 3.4.

## How

`tools/vt_golden.py` runs programs and saves the bytes they write. tmux replays those bytes, and `capture-pane` gives the expected text, colors, attributes and cursor. Every session below runs in the test suite, so CI needs none of these programs.

| Session | Source | Checkpoints |
|---|---|---|
| `nvim` | Scripted: syntax, numbers, wide text, splits, scrolling, `:help`, substitution, exit | 14 |
| `htop` | Scripted, in a PID namespace so no other process appears: setup, help, tree view, exit | 9 |
| `vttest-cursor`, `vttest-screen` | vttest's first two menus | 7 and 15, plus 7 checked against vttest's own instructions |
| `fuzz-1` to `fuzz-4` | Generated control sequences, 1,000 each | 40 each |

**Limits:** nothing here was looked at in a real terminal. The vttest results come from reading captured screens as text, with attributes checked as cell data, and vttest's "does this look right" tests are only as good as that. Blink is dropped by design (§11.2), so vttest's blink lines look like plain text. Colors are only checked as the values a cell holds.

## vttest

| Menu | Test | Result |
|---|---|---|
| 1 Cursor | Border and frame at 80 columns | Pass |
| 1 Cursor | Same at 132 columns | Can't pass: the shell decides the size, so the switch only clears the screen. tmux and xterm's default do no better |
| 1 Cursor | Autowrap with control characters | Pass. tmux loses some letters; `VttestTests` checks the margins |
| 1 Cursor | Control characters inside sequences | Pass |
| 1 Cursor | Leading zeros | Pass |
| 2 Screen | Wrap around | Pass |
| 2 Screen | Tab setting and resetting | Pass |
| 2 Screen | 132 column modes | Text wraps at 80, as above. Light background is correct |
| 2 Screen | 80 column modes | Pass, with light background |
| 2 Screen | Soft and jump scroll regions | Pass |
| 2 Screen | Origin mode | Pass. tmux puts the line in the wrong place; `VttestTests` checks it |
| 2 Screen | Graphic rendition | Pass except blink. tmux ignores light background; `VttestTests` checks it |
| 2 Screen | Save and restore cursor | Pass |

vttest's other menus weren't run. Keyboard and terminal reports belong to 1c. Double-size characters (`ESC # 3` to `6`) are ignored, since a `Cell` can't hold them, and the VT52 mode isn't implemented.

## Generated sequences

On the final code, 40 seeds of 300 sequences each were compared with a screen check after every sequence. 39 matched throughout, and seed 16 stopped at its first difference, the column 0 bug in the table below, so the rest of that seed wasn't compared. Four seeds of 1,000 sequences are kept as regression tests.

Reading the differences found two real `VtScreen` issues: the attributes left behind when a wide character is cut in two, and a wide character with no room and wrap off, which overwrote its neighbour. Everything else was tmux departing from xterm, which is what `TERM=xterm-256color` promises. xterm itself wasn't run, so its column is from its documented behaviour and source, as I understand them. The generator avoids these cases, and `VtScreenTests` pins the behaviour here where noted.

| Case | tmux | `VtScreen`, intended to match xterm | Pinned |
|---|---|---|---|
| Insert or delete lines outside the scroll region | Moves lines | Ignored | Yes |
| Column after inserting or deleting lines | Unchanged | 0 | Yes |
| Insert characters, wider than what moves | Leaves old text in the gap | Blank | Yes |
| Insert mode across a wrap | Loses a character | Keeps every character | Yes |
| `CSI I` (tab forward) | Not implemented | Works | Yes |
| Repeat (`CSI b`) | Only straight after an ASCII character, up to the edge | Any character, and wraps | Yes |
| Saved cursor | One, shared between screens | One per screen, saved with origin mode and wrap | Yes |
| Setting a region in origin mode | Goes to the screen's first line | Goes to the region's | Yes |
| While a wrap is pending | Counts from past the edge, and keeps it across a line feed | Counts from the last column, and cancels it | Yes |
| Scrolling caused by a wrap | Default background | Current background | No |
| Entering the alternate screen with a background set | Default background | Current background | No |
| Wide character whose second half is overwritten, in column 0 | Keeps it | Blanks it | Yes |
| Line drawing for letters outside its usual set | Letter | The VT100 symbol | Yes |

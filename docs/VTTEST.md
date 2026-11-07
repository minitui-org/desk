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

## Input and replies (Phase 1c)

Checked on 2026-11-06, with the same vttest and tmux. `tools/key_probe.py` runs a program that prints the raw bytes it receives, directly in tmux and hosted in the example, and sends both the same keys.

**Limits:** tmux is the outer terminal throughout, driven by scripted keys, so no real keyboard or GUI terminal was involved. Kitty's forms were sent as raw bytes, not by a terminal that speaks the protocol.

**Keys in [PRIOR-ART.md](PRIOR-ART.md):** all 10 arrive identical to bare tmux: a, Esc, Ctrl+B, Up, Ctrl+Space, Alt+Left, Ctrl+PageUp, Ctrl+Shift+Up, Alt+Enter and F10. Shift+Enter is `CSI 13;2u` for a program that pushed kitty flags, also identical, and `\r` for one that didn't, as from xterm.

**Other keys:** 52 more were sent, and 49 were identical at the time. The three that differed:

| Key | Bare tmux | Hosted | Why |
|---|---|---|---|
| Home | `CSI 1 ~` | `CSI H` | tmux's own form. A hosted program was told `TERM=xterm-256color`, whose form is `CSI H` |
| End | `CSI 4 ~` | `CSI F` | The same |
| Ctrl+J | `\n` | `\r` | A gap in MiniTui, which decoded `0x0A` as Enter. Fixed in MiniTui 2.1.1, and now identical |

**Mouse and paste:** a click, release and wheel arrive with positions relative to the pane. A click outside it, or in a program that didn't ask, sends nothing. Pasted line breaks arrive as `\r`.

**vttest menu 6 (reports):**

| Test | Result |
|---|---|
| Device status 5 and 6 | Pass: "TERMINAL OK", and the cursor position marked OK |
| Primary attributes | `CSI ? 62 ; 22 c`: VT200 family, color |
| Secondary attributes | `CSI > 1 ; 10 ; 0 c`: VT220, firmware 1.0 |
| Answerback | Empty, which is allowed: no message is set |

Menu 6's other tests weren't run. Menu 5 (keyboard) wasn't either, as it asks a person to press keys, and the probe above covers the same ground.

**Real programs in the example:** nvim started with no terminal-response warning and pushed kitty flags, and followed a resize; htop drew correctly; Ctrl+C stopped `yes`. Each was read from captured text. k9s and Claude Code are on the 1d list.

## The shell (Phase 1d)

Checked on 2026-11-06, with `tuidesk` in tmux 3.4 under a private server, reading captured text.

**Limits:** as in Phase 1c, tmux is the outer terminal and keys are scripted, so colors and the feel of it on a real GUI terminal are unchecked. Kitty's forms, including Shift+Enter, were sent as raw bytes. Text reaching a real system clipboard is unchecked; the end-to-end test sees the OSC 52 that `tuidesk` sends.

**`tools/key_probe.py --desk`:** 0 unexpected differences. Every key in [PRIOR-ART.md](PRIOR-ART.md) arrives identical to bare tmux (Ctrl+Space by way of `Ctrl+S` in the launcher, as it's the prefix), and so does Ctrl+J, with MiniTui 2.1.1. Of the other keys, only Home and End differ, for the reason above.

**Real programs:**

| Program | Result |
|---|---|
| nvim, with `-u NONE` | Draws correctly, `:messages` is empty, so no terminal-response warning |
| htop | Draws correctly, and follows a resize |
| k9s | Draws correctly against a real cluster, and is the right size after a resize while in the background |
| Claude Code | Draws correctly, and Shift+Enter in the input adds a line rather than sending. No prompt was sent |

Closing the tmux session ended `tuidesk` and everything it hosted.

**Native AOT:** `tuidesk` publishes with no warnings. The binary started a shell, and showed help and a broken `config.json`'s notification.

**The launcher opening straight away:** rechecked on 2026-11-06 the same way, with `tools/key_probe.py --desk` at 0 unexpected differences, sending Ctrl+Space by way of `Ctrl+S`. The footer, the swap to the switcher, help and quitting were read from captured text at 100 × 30 and 50 × 12, and `Ctrl+Space` and `Ctrl+Q` were sent through the published AOT binary. nvim, htop, k9s and Claude Code weren't rerun, as nothing between an app and the terminal changed.

**Not done:** the working session in §12.9, which needs a person.

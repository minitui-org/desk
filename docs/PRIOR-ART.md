# Prior art: vtm and desktop-tui

What Phase 1 should learn from each, from trying them on 2026-11-05. Versions: vtm v2026.10.12, desktop-tui v0.3.2.

## How they were tested

Each ran in a 120×35 tmux session on Ubuntu 24.04 (WSL2), driven by scripted keys and mouse events, with an isolated `HOME`. A small probe printed the bytes a hosted app received. The same probe in bare tmux got every key, so anything missing below was taken by the program under test.

**Limits:** this was a scripted session of about an hour, not daily use, and it was seen as captured text, so it says little about look and feel. tmux sits between the driver and the program, which may have changed some results for keys and colors. k9s and Claude Code weren't tried. Chords that need keys held together, such as vtm's Esc+F11, couldn't be tested.

## Results

| | vtm | desktop-tui |
|---|---|---|
| Install | Static binary, one download (needs 7-Zip) | `cargo install` fails without `libncursesw` dev files |
| First use | Empty desktop and an app list; one click | Write a TOML file per app first, then click the app, then Start |
| Keyboard only | Alt+Shift+N runs an app | Found no way: F10, Tab, arrows, Enter and Alt+letters did nothing |
| Screen given to an app | 100×31 of 120×35 when maximized | Opens as a 98×23 floating window |
| `TERM` | `xterm-256color` | `screen-256color`, forced |
| bash, htop, nvim | All correct | bash correct; htop garbled; nvim garbled |
| Ctrl+C during `yes` flood | About 0.1 s | About 19 s |
| App killed with `kill -9` | Window disappears, no message | Window disappears, no message |
| Outer terminal shrinks | Window stays its size and is cut off | Window is cut off |
| Settings | XML-style config; the settings doc is over 2,100 lines | One TOML file per app |

### Keys reaching a hosted app

| Key | vtm | desktop-tui |
|---|---|---|
| Letters, Esc, Ctrl+B, Up | Yes | Yes |
| Ctrl+Space | Yes | **Arrives as a plain space** |
| Alt+Left | **No** (find next match) | **No** |
| Ctrl+PageUp | **No** (switch focus) | **No** |
| Ctrl+Shift+Up | **No** (scrollback) | **No** |
| Alt+Enter | Yes | **No** |
| F10 | Yes | **No** |
| Shift+Enter (kitty form) | Yes | **No** |

## Friction, and what Desk does about it

**1. The host takes the app's keys.** vtm documents dozens of bindings, and many are live inside a hosted app: Alt+arrows, Ctrl+PageUp, Ctrl+Shift+arrows, Alt+Shift+letters. An app can't see them, and nvim users bind these. Desk's single prefix key is the right answer: **every key but the prefix reaches the app, unchanged**. desktop-tui also loses modified keys, turning Ctrl+Space into a space, which is the risk of decoding keys and encoding them again (§11.3).

**2. Chrome takes the screen.** Maximized in vtm, an app gets 100×31 of 120×35: the taskbar costs 20 columns and the window title and menu rows cost the rest. Desk gives the app everything but one row.

**3. Windows are the default, and they need a mouse.** vtm opens a 74×24 window on a desktop, and overlapping windows pile up. desktop-tui offers no keyboard path that I could find. Desk has one foreground app, and everything works from the keyboard.

**4. A crash leaves no trace.** In both, an app killed with `kill -9` just vanishes, with no exit status and no message. Desk should keep the dead app's last screen, show how it ended, and offer restart or close.

**5. Resizing doesn't follow the terminal.** In both, a window stays its size when the outer terminal shrinks, and content ends up off-screen. A full-screen app avoids this as long as it's resized with the terminal.

**6. Query replies matter on day one.** In desktop-tui, nvim printed `terminal did not respond to DSR request for 'background' color` over its own screen, which confirms §11.4. htop was also garbled there, for a cause I didn't establish.

**7. Floods can lock you out.** desktop-tui took about 19 s to respond to Ctrl+C during `yes`, while vtm took about 0.1 s. So this is solvable, and worth a measured criterion.

**8. Setup should be optional.** vtm starts usefully with no config but has a very large settings surface. desktop-tui needs a file per app before it does anything. Desk should open a shell with no config at all, with `apps.json` adding to that.

## What vtm does well

- bash, htop and nvim all ran correctly, and a flood was handled well.
- Single static binary, and it starts ready to use.
- It's actively developed and far more capable than Desk v1 will be: tiling, a scrollback terminal, its own protocol, and a GUI mode. Desk shouldn't compete on features, only on focus (§3).

desktop-tui says of itself that it's an experiment made in 2 days and not meant to be used as is, so its rough edges are expected. They're useful here as a list of what goes wrong.

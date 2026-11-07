# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- `tuidesk`, the shell (spec §12.3 to §12.7): one app on the screen and a status line, with `Ctrl+Space` as the only key taken from the app. A switcher and a launcher, apps that keep their screen and say how they ended when they fail, quiet notifications with Do Not Disturb on by default, and optional config files.
- `MiniTui.Vt`: `VtTerminal.Start(info, setup)` runs `setup` before any output is read, so a program that rings the bell or asks about its colors as it starts can't get ahead of the handlers.
- `MiniTui.Vt`: `PtyProcess` starts a program in a pseudo-terminal of its own, with its size, working directory and environment, and reads, writes, resizes, hangs up and kills it, and reports how it ended.
- `MiniTui.Vt`: `VtScreen` turns a program's output into MiniTui cells: colors and styles, cursor movement, scroll regions, insert and delete, the alternate screen, wide characters and combining marks, line wrap, resize, the title and input modes. Any bytes are safe to feed it, split anywhere.
- `MiniTui.Vt`: `VtInput` turns keys, mouse events, pastes and focus changes back into the bytes a program expects, in xterm's forms or kitty's, following the modes it set.
- `MiniTui.Vt`: `VtScreen` answers terminal queries through `IVtHost`: device attributes, status, cursor position, mode state, default colors and kitty flags. It reports the bell, clipboard writes and notifications. A program can't read the clipboard.
- `MiniTui.Vt`: `VtTerminal` runs a program and keeps its screen current on a thread of its own, and a program that stops reading can't block it. `TerminalViewBuilder` draws one in a `CellBuffer`, showing the last complete frame while a program draws the next (mode 2026, for up to 200 ms).
- An example that runs a shell in a pane of an ordinary MiniTui app, and `tools/key_probe.py`, which compares the bytes a program receives with bare tmux.

### Changed
- `Ctrl+Space` opens the launcher straight away, and `Tab` swaps it for the switcher, instead of a bar waiting for a second key. Commands moved into both lists, shown in a footer: `Ctrl+X` closes the app, `Ctrl+Q` quits, `Ctrl+N` shows notifications, `Ctrl+D` toggles Do Not Disturb, `Ctrl+S` sends `Ctrl+Space` to the app, and `F1` is help. `1`-`9` work while the filter is empty, and pressing `Ctrl+Space` again closes the list.
- MiniTui 2.1.1, where Ctrl+J reaches a hosted program as itself on terminals without a keyboard protocol.

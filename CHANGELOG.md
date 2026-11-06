# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- `MiniTui.Vt`: `PtyProcess` starts a program in a pseudo-terminal of its own, with its size, working directory and environment, and reads, writes, resizes, hangs up and kills it, and reports how it ended.
- `MiniTui.Vt`: `VtScreen` turns a program's output into MiniTui cells: colors and styles, cursor movement, scroll regions, insert and delete, the alternate screen, wide characters and combining marks, line wrap, resize, the title and input modes. Any bytes are safe to feed it, split anywhere.
- `MiniTui.Vt`: `VtInput` turns keys, mouse events, pastes and focus changes back into the bytes a program expects, in xterm's forms or kitty's, following the modes it set.
- `MiniTui.Vt`: `VtScreen` answers terminal queries through `IVtHost`: device attributes, status, cursor position, mode state, default colors and kitty flags. It reports the bell, clipboard writes and notifications. A program can't read the clipboard.
- `MiniTui.Vt`: `VtTerminal` runs a program and keeps its screen current on a thread of its own, and a program that stops reading can't block it. `TerminalViewBuilder` draws one in a `CellBuffer`, showing the last complete frame while a program draws the next (mode 2026, for up to 200 ms).
- An example that runs a shell in a pane of an ordinary MiniTui app, and `tools/key_probe.py`, which compares the bytes a program receives with bare tmux.

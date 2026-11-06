# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- `MiniTui.Vt`: `PtyProcess` starts a program in a pseudo-terminal of its own, with its size, working directory and environment, and reads, writes, resizes, hangs up and kills it, and reports how it ended.
- `MiniTui.Vt`: `VtScreen` turns a program's output into MiniTui cells: colors and styles, cursor movement, scroll regions, insert and delete, the alternate screen, wide characters and combining marks, line wrap, resize, the title and input modes. Any bytes are safe to feed it, split anywhere.

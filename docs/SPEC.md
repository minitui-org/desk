# MiniTui Desk — Spec v0 (handoff)

Status: draft, pre-implementation. Captures decisions from the design discussion of 2026-11-05.
Audience: Claude Code (implementation) and Ben (owner). Write specs per phase before code, as with `docs/SPEC-1.x.md` in the MiniTui repo.

---

## 1. Summary

MiniTui Desk is a **distraction-free, terminal-based desktop environment for Linux**, built on the MiniTui framework.

- **Focus is the design principle.** One task is in the foreground at a time, notifications are batched or suppressed by default, and there is no visual clutter.
- **Runs in two places:** inside any terminal emulator first, and later straight from the Linux console (DRM/KMS) with no X11 or Wayland.
- **Two kinds of app:** existing terminal programs (nvim, k9s, Claude Code, shells) hosted in PTYs, and native MiniTui apps that opt into a small protocol for deeper integration.
- **MiniTui is the primary product.** Desk is a sub-product built on it, and serves as the framework's flagship app and dogfood.

## 2. Brand and naming (decided)

| Context | Form |
|---|---|
| Brand, org | **MiniTui** (minitui.org, GitHub `minitui-org`) |
| Framework | **MiniTui** — NuGet ID `MiniTui`, unchanged |
| Desktop (prose) | **MiniTui Desk** |
| Desktop (.NET) | `MiniTui.Desk` |
| Protocol package | `MiniTui.Desk.Protocol` |
| Terminal-emulator component | `MiniTui.Vt` |
| Binary, config dir, systemd unit | `tuidesk`, `~/.config/tuidesk/`, `tuidesk.service` |
| Repo | `minitui-org/desk` |
| Docs | `minitui.org/desk` (a section; the framework leads the site) |

Rejected names:
- **MiniTDE**: "TDE" is the Trinity Desktop Environment, which is actively maintained (R14.1.7, Nov 2026) and in the same category.
- **Termdesk**: an existing 2009 project with the same concept.
- **TuiTop**: an existing project with the same concept.
- **MiniTuiDesk** as one word: allowed only for handles and usernames.

## 3. Prior art and differentiation

Existing projects: vtm (C++, mature, ~1.6k stars), desktop-tui (Rust, built in 2 days), tuiui, artemis-shell, Kern (a bundle of fzf, zellij and framebuffer tools), Twin, Turbo Vision/tvision.

Windows and a taskbar in a terminal are already solved. **The gap Desk fills:**
1. An opinionated, focus-first product, not a tmux with chrome added.
2. A first-class native app model and SDK: MiniTui, plus the protocol.
3. A coherent set of first-party apps.
4. A bare-console mode as a deliberate target.

Before Phase 1, spend an hour with vtm and desktop-tui and write down their friction points. That list feeds Phase 1's acceptance criteria.

## 4. Goals and non-goals

**Goals**
- Usable as a daily terminal workspace inside a terminal emulator (Phase 1).
- Native MiniTui apps light up inside Desk and behave identically outside it.
- Bare-console boot as an optional later backend, with no changes to the shell or apps.
- Using MiniTui standalone is never compromised (see §7).

**Non-goals (v1)**
- Floating or overlapping windows. They're expensive and they work against the focus premise.
- Any GUI or browser story. Text-mode browsers can be hosted as ordinary PTY apps.
- Windows or macOS as Desk hosts. Desk is Linux-first; the framework stays cross-platform.
- Rendering native apps in-process.

## 5. Architecture

### 5.1 Process model
- `tuidesk` is a MiniTui app: the **shell**. It owns the screen, input, focus and notifications.
- Every app, native or not, runs **out of process in a PTY**. The shell runs a VT emulator (`MiniTui.Vt`) per app and composites the foreground app's screen into its own `CellBuffer`.
- Native apps also connect to the shell over a **Unix domain socket** for metadata only (§8). Rendering always goes over the PTY as ANSI, so MiniTui core rendering is unchanged.
- An app crash never takes down the shell.

### 5.2 Packages and dependencies

```
MiniTui                 (core; no dependencies — unchanged rule)
 ├─ MiniTui.Vt          PTY + VT parser + screen model + TerminalView widget
 ├─ MiniTui.Desk.Protocol   message types + client; inactive outside Desk
 └─ MiniTui.Desk        the shell (tuidesk); depends on all of the above
```

- `MiniTui.Vt` has standalone value: an embedded shell pane in any MiniTui app.
- `MiniTui.Desk.Protocol` must be tiny, AOT- and trim-safe, and use System.Text.Json source generation with no other dependencies.
- Target `net10.0` to match core. Trim- and AOT-compatible throughout.

### 5.3 Rendering backends
MiniTui already has a public, cell-level `ITerminalBackend` (`GetSize`, `Enter`, `Leave`, `Draw(changed cells, cursor, fullRedraw)`). `Terminal` does the diffing. Desk uses the default `AnsiBackend` in Phase 1. The bare-console mode (Phase 3) is a new `ITerminalBackend` plus a new input source. No shell code changes.

## 6. Phases

Each phase gets its own spec section before implementation begins, with acceptance criteria.

**Phase 0 — Contracts (spec only)**
- Desk Protocol v0 (§8) finalized.
- `MiniTui.Vt` public API sketched.
- MiniTui core changes (§7) specced in the MiniTui repo's own spec style.
- Exit: specs reviewed by Ben.

**Phase 1 — Shell inside a terminal emulator**
- `MiniTui.Vt`:
  - PTY spawn via `openpty`/`forkpty` P/Invoke, with resize (TIOCSWINSZ plus SIGWINCH to the child)
  - VT parser (DEC/ANSI state machine) and screen model: SGR (16, 256 and truecolor), cursor movement, scroll regions, alternate screen, wide characters, bracketed paste and mouse passthrough
  - A `TerminalView` widget drawing into a `CellBuffer`
- Shell: one foreground app at a time, filling the screen apart from a one-row status line.
  - App switcher (command-palette style, using the existing `CommandPalette` and `Fuzzy`)
  - Launcher driven by `~/.config/tuidesk/apps.json`
  - Status line: clock, app name, notification count
  - Notification queue with Do Not Disturb on by default; notifications are batched and viewed on demand
  - A configurable global prefix key, tmux-style, so hosted apps keep all their other keys
- Exit criteria: nvim, k9s, htop, bash and Claude Code run correctly, resize correctly and survive app crashes. Ben uses it for a working session.

**Phase 2 — Native apps**
- `MiniTui.Desk.Protocol` client implemented; MiniTui apps opt in with one call.
- Two or three first-party apps, for example notes, tasks and a reader. Each lists the framework gaps it hit, and each gap becomes a MiniTui issue.
- Exit: native apps show their name and status in the shell and route notifications through it, and they behave identically when run outside Desk.

**Phase 3 — Bare console (optional)**
- A DRM/KMS `ITerminalBackend` with font rasterization (FreeType or a prebuilt bitmap font atlas).
- A libinput-based input source, plus libseat/logind for session and VT switching.
- Boot to `tuidesk` from a TTY, for example via a systemd unit.
- This is the highest-risk phase. Start it only once Phases 1 and 2 have proven the concept. C# P/Invoke is the default; a thin Rust layer is an acceptable alternative if P/Invoke becomes painful.

**Phase 4 — SDK as a product**
- `dotnet new` template for a Desk-aware MiniTui app.
- An app manifest format, docs at `minitui.org/desk`, and examples.

## 7. MiniTui core: required changes and guardrails

**Rule:** nothing enters core without a standalone justification. Anything needed only by Desk lives in a Desk package.

Known changes:
1. **Public rich input source.** Today the public `IInputSource` handles keys only (`TryRead(out ConsoleKeyInfo)`). Pastes, mouse events and terminal replies flow through internal interfaces (`InputItem`, `IInputItemSource`, `IMouseCapture`, `IInputLifecycle`, `IWaitableInputSource`). Phase 3's libinput source needs a public equivalent.
   - Standalone justification: custom input sources, replay and recording, and test harnesses.
2. **Backend capability detection.** `AppLoop` and `AppSuspend` special-case `AnsiBackend` and `LegacyConsoleBackend` in places: clipboard query, terminal queries, color mode, suspend. A third-party backend needs a capability surface, such as an optional interface for color mode, clipboard and queries, instead of type checks.
   - Standalone justification: alternative backends in general.

Anything else discovered in Phases 1 and 2 must be filed against MiniTui with its standalone justification written down.

Constraints that must not regress: no runtime dependencies, cross-platform core, trim/AOT compatibility, and `PublicAPI.Shipped.txt` discipline.

## 8. Desk Protocol v0 (draft)

- **Discovery:** the shell sets `MINITUI_DESK_SOCKET=<path>` and `MINITUI_DESK_APP_ID=<id>` in each child's environment. If these are absent, the client does nothing and the app behaves exactly as it does outside Desk.
- **Transport:** a Unix domain socket carrying newline-delimited JSON. Each message is `{"v":0,"type":"…",…}`.
- **App → shell**
  - `hello` `{appId, name, version, protocol: 0}`
  - `title` `{text}`: short status shown in the status line and switcher
  - `notify` `{id, title, body, urgency: low|normal|high}`: queued by the shell's notification policy
  - `launch` `{appId, args}`: ask the shell to open another app
  - `bye`
- **Shell → app**
  - `welcome` `{protocol: 0, theme: light|dark}`
  - `focus` `{focused: bool}`: apps may pause work while in the background
  - `notificationAction` `{id, action}`
  - `quitRequest`: polite close; the app may refuse with a confirm dialog
- **Compatibility:** unknown message types and fields are ignored. A version mismatch is negotiated down at `hello`/`welcome`.
- **Out of scope for v0:** clipboard (the PTY already carries OSC 52), rendering, and IPC between apps.

## 9. Open decisions

| # | Question | Recommendation |
|---|---|---|
| 1 | Write the VT parser in C# or bind libvterm? | Pure C#, keeping it dependency-free and AOT-friendly; test with vttest and recorded sessions |
| 2 | Where does `MiniTui.Vt` live: the `desk` repo or the `minitui` repo? | Start in `desk`; move it to `minitui` when its API stabilizes |
| 3 | Config format | JSON (System.Text.Json, no extra dependency) |
| 4 | Prefix key default | `Ctrl+Space` (avoids tmux's `Ctrl+B` clash) |
| 5 | Desk versioning | 0.x, pinned to MiniTui ≥ 2.1; follows the framework's major version |
| 6 | Splits in v1? | No; revisit after Phase 1 use (`Widget.Split` exists if wanted) |

## 10. Handoff notes for Claude Code

- Start with Phase 0: write specs, not code. Use `docs/SPEC-1.x.md` in the MiniTui repo as the style reference: numbered sections, one phase per section, explicit acceptance criteria.
- The MiniTui repo is at `github.com/minitui-org/minitui` (moved from `discover-edge/minitui`). Version 2.1.0 was released on 2026-11-04.
- Read `src/MiniTui/Terminal/ITerminalBackend.cs`, `Terminal/IInputSource.cs`, `App/AppLoop.cs` and `App/AppSuspend.cs` before writing §7 specs.
- Don't modify MiniTui core from the `desk` repo. Raise core changes as specs or PRs in `minitui` with their standalone justification.
- The test strategy should mirror MiniTui's: `TestBackend` and `ScriptedInputSource` for the shell, and golden-screen tests for `MiniTui.Vt`.

# MiniTui Desk — Spec v0 (handoff)

Status: Phase 0 done; Phase 1 (§12) specced. Captures decisions from the design discussion of 2026-11-05, corrected against MiniTui 2.1.0 and a PTY spike (§11.1).
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

Existing projects: vtm (C++, mature, ~3.4k stars), desktop-tui (Rust, built in 2 days, ~1.2k stars), tuiui, artemis-shell, Kern (a bundle of fzf, zellij and framebuffer tools), Twin, Turbo Vision/tvision.

Windows and a taskbar in a terminal are already solved. **The gap Desk fills:**
1. An opinionated, focus-first product, not a tmux with chrome added.
2. A first-class native app model and SDK: MiniTui, plus the protocol.
3. A coherent set of first-party apps.
4. A bare-console mode as a deliberate target.

vtm and desktop-tui were tried on 2026-11-05: see [PRIOR-ART.md](PRIOR-ART.md). The friction list feeds Phase 1's exit criteria.

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
- `MiniTui.Desk.Protocol` must be tiny, AOT- and trim-safe, and use System.Text.Json source generation. It depends on `MiniTui` (for the one-call opt-in, §8.5) and nothing else.
- Target `net10.0` to match core. Trim- and AOT-compatible throughout.

### 5.3 Rendering backends
MiniTui already has a public, cell-level `ITerminalBackend` (`GetSize`, `Enter`, `Leave`, `Draw(changed cells, cursor, fullRedraw)`). `Terminal` does the diffing. Desk uses the default `AnsiBackend` in Phase 1. The bare-console mode (Phase 3) is a new `ITerminalBackend` plus a new input source. No shell code changes.

## 6. Phases

Each phase gets its own spec section before implementation begins, with acceptance criteria.

**Phase 0 — Contracts (spec only)**
- Desk Protocol v0 (§8) finalized.
- `MiniTui.Vt` public API sketched.
- PTY spawning proven (§11.1).
- Core changes (§7) are not specced now: Phase 1 and 2 don't need them, so they're specced when Phase 3 starts.
- Exit: specs reviewed by Ben.

**Phase 1 — Shell inside a terminal emulator** (full spec: §12)
- `MiniTui.Vt`:
  - PTY spawn via `posix_openpt` and `posix_spawn` P/Invoke (§11.1), never `forkpty`, with resize by `TIOCSWINSZ`
  - VT parser (DEC/ANSI state machine) and screen model: SGR (16, 256 and truecolor), cursor movement, scroll regions, alternate screen, wide characters, synchronized output
  - Replies to terminal queries, and OSC 52 forwarding (§11.4)
  - Input encoding: keys, mouse and pastes back to bytes, following each child's modes (§11.3)
  - A `TerminalView` widget drawing into a `CellBuffer`
- Shell: one foreground app at a time, filling the screen apart from a one-row status line.
  - App switcher, in most-recently-used order
  - Launcher driven by `~/.config/tuidesk/apps.json`
  - Status line: clock, app name, notification count
  - Notification queue with Do Not Disturb on by default; notifications are batched and viewed on demand
  - A configurable global prefix key, tmux-style, so hosted apps keep all their other keys
- Exit criteria: nvim, k9s, htop, bash and Claude Code run correctly, resize correctly and survive app crashes. Ben uses it for a working session. From [PRIOR-ART.md](PRIOR-ART.md):
  - **Keys:** every key but the prefix reaches the app unchanged, shown by a byte probe compared with the bare terminal. Shift+Enter reaches Claude Code when the outer terminal reports it.
  - **Screen:** a hosted app gets the full terminal minus one row.
  - **Keyboard:** everything works without a mouse.
  - **No config:** with no `apps.json`, Desk opens a shell.
  - **Crashes:** a dead app keeps its last screen and shows how it ended, with restart and close.
  - **Resize:** the app follows the outer terminal with nothing left off-screen.
  - **Floods:** Ctrl+C during `yes` gets back to a prompt in under 500 ms.
  - **Queries:** nvim starts with no terminal-response warning.
- Sub-phases, one branch and PR each: **1a** PTY, **1b** parser and screen, **1c** input encoding and query replies, **1d** the shell.

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

Phase 1 and 2 use `AnsiBackend` and the default input source, so they need no core changes. Both changes below are specced when Phase 3 starts.

Known changes (Phase 3):
1. **Public rich input source.** Today the public `IInputSource` handles keys only (`TryRead(out ConsoleKeyInfo)`). Pastes, mouse events and terminal replies flow through internal interfaces (`InputItem`, `IInputItemSource`, `IMouseCapture`, `IInputLifecycle`, `IWaitableInputSource`). Phase 3's libinput source needs a public equivalent.
   - Standalone justification: custom input sources, replay and recording, and test harnesses.
2. **Backend capability detection.** `AppLoop`, `AppSuspend`, `AppTerminalInfo`, `Clipboard` and `Terminal` special-case `AnsiBackend` and `LegacyConsoleBackend` in places: clipboard write and query, terminal queries, color mode, suspend, frame byte counts. A third-party backend needs a capability surface, such as an optional interface for color mode, clipboard and queries, instead of type checks.
   - Standalone justification: alternative backends in general.

Candidate from Phase 1:
- **Richer cell styles.** `Modifiers` has no blink, and `Cell` has no curly or coloured underlines or OSC 8 hyperlinks. Phase 1 flattens them (§11.2). Adding them is justified for any app that wants them, so file it once real use shows it matters.

Anything else discovered in Phases 1 and 2 must be filed against MiniTui with its standalone justification written down.

Constraints that must not regress: no runtime dependencies, cross-platform core, trim/AOT compatibility, and `PublicAPI.Shipped.txt` discipline.

## 8. Desk Protocol v0

### 8.1 Discovery
The shell sets `MINITUI_DESK_SOCKET=<path>` and `MINITUI_DESK_APP_ID=<id>` in each child's environment. If either is absent, or the socket can't be reached, the client does nothing and the app behaves exactly as it does outside Desk.

Children inherit these, so a MiniTui app started from a hosted bash connects with bash's id. That's accepted: it's what's in the foreground of that PTY. An id can have several connections, and the most recent `hello` provides the name and title until it disconnects, when the previous one takes over.

### 8.2 Transport
- A Unix domain socket at `$XDG_RUNTIME_DIR/tuidesk/<shell pid>.sock`, in a directory of mode `0700`. Without `XDG_RUNTIME_DIR`, Desk uses `/tmp/tuidesk-<uid>/`, refusing it if another user owns it.
- Newline-delimited JSON, UTF-8, one object per line, each with a `type`. A line longer than 64 KiB closes the connection.
- The app speaks first, with `hello`. The shell ignores anything else before it.

### 8.3 Messages

App → shell:

| Type | Fields | Meaning |
|---|---|---|
| `hello` | `appId, name, version, protocol` | `protocol` is the highest version the app speaks |
| `title` | `text` | Short status in the status line and switcher. Control characters are stripped |
| `notify` | `id, title, body, urgency, actions?` | Queued by the shell's policy. `urgency` is `low`, `normal` or `high`. `actions` is a list of `{id, label}`. A repeated `id` replaces the earlier one from that app |
| `launch` | `appId, args` | Open another app from `apps.json`. An unknown id is ignored |
| `bye` | | The app is closing the connection on purpose |

Shell → app:

| Type | Fields | Meaning |
|---|---|---|
| `welcome` | `protocol, theme` | The version to use: the lower of the two. `theme` is `light` or `dark` |
| `focus` | `focused` | Sent when it changes; apps may pause while in the background |
| `notificationAction` | `id, action` | The user chose an action from `notify.actions` |
| `quitRequest` | | Polite close. There's no reply: the app exits, or stays and asks the user. The switcher can always kill it |

```
{"type":"hello","appId":"notes-1","name":"Notes","version":"0.1.0","protocol":0}
{"type":"welcome","protocol":0,"theme":"dark"}
{"type":"notify","id":"sync","title":"Synced","body":"12 notes","urgency":"low"}
```

### 8.4 Compatibility and failure
- Unknown message types and fields are ignored.
- The client never throws into the app. If the connection drops, it goes inactive and doesn't reconnect, and the app carries on.
- **Decision:** there's no per-message `v` field, unlike the draft. The version is agreed once per connection at `hello`/`welcome`, so repeating it adds nothing.
- **Out of scope for v0:** clipboard (§11.4), rendering, and IPC between apps.

### 8.5 Client API

```csharp
namespace MiniTui.Desk.Protocol;

public sealed class DeskClient : IAsyncDisposable
{
    public static DeskClient Connect(string name, string? version = null);  // inactive outside Desk
    public bool IsActive { get; }
    public bool IsFocused { get; }                                          // true outside Desk
    public string? Theme { get; }
    public void SetTitle(string text);
    public void Notify(DeskNotification notification);
    public void Launch(string appId, params string[] args);
    public event Action<bool>? FocusChanged;
    public event Action<string, string>? NotificationAction;                // id, action
    public event Action? QuitRequested;
}

public static class DeskAppExtensions
{
    // The one-call opt-in: events arrive on the app's thread through App.Post. With no handler, quitRequest calls app.Quit.
    public static App WithDesk(this App app, string name, out DeskClient client, string? version = null);
}
```

Every member is a no-op when inactive, so apps don't need to check.

## 9. Decisions

Accepted by Ben on 2026-11-05, as recommended.

| # | Question | Decision |
|---|---|---|
| 1 | Write the VT parser in C# or bind libvterm? | Pure C#, keeping it dependency-free and AOT-friendly; test with vttest and recorded sessions. Stress-test early in 1b; falling back to libvterm would be a new decision |
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

## 11. `MiniTui.Vt` (Phase 0 contract)

### 11.1 Spawning (proven)
`forkpty` is out: the child would run managed code after `fork`, in a copy of the runtime with its other threads gone. Instead:

1. `posix_openpt`, `grantpt`, `unlockpt` and `ptsname_r`, with the master `O_CLOEXEC`. (The spike used `openpty`, which needs libutil on older glibc.) The parent keeps the slave open across the spawn, so the terminal always has one.
2. `posix_spawn` with `POSIX_SPAWN_SETSID`, `SETSIGDEF` for every signal and `SETSIGMASK` with an empty mask. .NET's ignored signals would otherwise reach the child.
3. File actions: open the slave by path as fd 0, then `dup2` it to 1 and 2. Opening it after `setsid` is what makes it the controlling terminal.

A spike on Ubuntu 24.04 (glibc, .NET 10, run with the normal runtime) confirmed: the child is a session leader with the PTY as its controlling terminal and the right size; no signals are ignored or blocked; `TIOCSWINSZ` alone delivers `SIGWINCH`, so the shell doesn't send it; Ctrl+C reaches the foreground job; Ctrl+Z and `jobs` work in an interactive bash.

1a added Native AOT (with `LibraryImport`), working directory (`posix_spawn_file_actions_addchdir_np`), and reaping alongside `System.Diagnostics.Process`: each child has its own waiting thread, which calls `waitid` and `waitpid` for that pid only.

### 11.2 Screen model

```csharp
namespace MiniTui.Vt;

public sealed class VtScreen
{
    public VtScreen(int width, int height);
    public int Width { get; }
    public int Height { get; }
    public Cell this[int x, int y] { get; }          // MiniTui's Cell, so drawing is a copy
    public (int X, int Y)? Cursor { get; }           // null when hidden
    public string? Title { get; }
    public VtModes Modes { get; }
    public IVtHost? Host { get; set; }               // replies, clipboard, bell
    public void Feed(ReadOnlySpan<byte> bytes);      // handles UTF-8 split across calls
    public void Resize(int width, int height);
}

public interface IVtHost
{
    void Reply(ReadOnlySpan<byte> bytes);            // goes back to the child
    void SetClipboard(string text);
    void Bell();
    void Notify(string? title, string body);         // OSC 9 and OSC 777 (§12.6)
}

public readonly record struct VtModes(
    bool AlternateScreen, bool ApplicationCursorKeys, bool ApplicationKeypad, bool BracketedPaste,
    VtMouseMode Mouse, VtMouseEncoding MouseEncoding, int KeyboardFlags, bool SynchronizedOutput);
```

- **`TERM`** is `xterm-256color`, so hosted apps need no terminfo entry of ours.
- **Styles `Cell` can't hold:** curly and coloured underlines become a plain underline; blink and OSC 8 hyperlinks are dropped (§7).
- **Synchronized output (mode 2026):** `TerminalView` draws the last complete frame until the mode ends, or for at most 200 ms.
- **Scrollback** isn't in v1. A parameter with no way to read the lines back wouldn't do anything, so the constructor gains one when an embedded pane needs it.
- **`Host` and `IVtHost`** arrive in 1c with the replies they carry. 1b parses those sequences and ignores them.
- **Where xterm and tmux differ,** `TERM` promises xterm, so xterm's behaviour wins. [VTTEST.md](VTTEST.md) lists the cases.

### 11.3 Input encoding
The shell gets decoded keys, and MiniTui has nothing that turns them back into bytes.

```csharp
public static class VtInput
{
    public static bool TryEncode(ConsoleKeyInfo key, VtModes modes, IBufferWriter<byte> output);
    public static bool TryEncode(MouseEvent mouse, VtModes modes, IBufferWriter<byte> output);  // false if the child didn't ask for it
    public static void EncodePaste(string text, VtModes modes, IBufferWriter<byte> output);
}
```

- Cursor keys follow `ApplicationCursorKeys`. Modified keys use xterm's `CSI 1;m` forms, or kitty's when the child pushed `KeyboardFlags`.
- Mouse coordinates are relative to the view; the child's mode and encoding decide what's sent.
- A paste is wrapped when the child enabled `BracketedPaste`, and any `ESC` in it is removed so that pasted text can't end the paste early.
- **Tests:** round trips against MiniTui's own decoder: encode, decode, compare, for every key and modifier it can report.
- **Known limit:** a child can only receive what the outer terminal reports and MiniTui decodes. Key-release events aren't available. A real gap found here is filed against MiniTui (§7).

### 11.4 Queries and clipboard
Apps ask the terminal questions, and the emulator has to answer or they wait for a timeout.

| Query | Reply |
|---|---|
| Device attributes (`CSI c`, `CSI > c`) | As a VT220-class terminal with the features implemented |
| Status and cursor position (`CSI 5 n`, `CSI 6 n`) | Ready; the cursor's position |
| Mode state (`DECRQM`), including 2026 | From `VtModes` |
| Foreground and background color (OSC 10, 11) | From the shell's theme, so apps pick the right light or dark look |
| Kitty keyboard flags (`CSI ? u`) | `KeyboardFlags` |
| Anything else | No reply, as most terminals do |

**OSC 52:** the sequence stops at the emulator, so the host forwards writes with `Clipboard.SetText`. **Decision:** clipboard reads from hosted apps are refused with an empty reply in v1, since any process in a PTY could otherwise read it silently.

### 11.5 Tying it together

```csharp
public sealed class VtTerminal : IDisposable
{
    public static VtTerminal Start(VtStartInfo info);    // file, args, environment, working directory, size
    public VtScreen Screen { get; }                      // read under Lock
    public object Lock { get; }
    public bool HasExited { get; }
    public int? ExitCode { get; }
    public event Action? Changed;                        // from the reader thread, coalesced; use App.Post
    public event Action? Exited;
    public bool HandleKey(ConsoleKeyInfo key);
    public bool HandleMouse(MouseEvent mouse);
    public void HandlePaste(string text);
    public void Resize(int width, int height);
    public void Kill();
}

public sealed class TerminalViewBuilder               // MiniTui's builder conventions
{
    public TerminalViewBuilder Terminal(VtTerminal terminal);
    public TerminalViewBuilder Focused(bool focused = true);
    public TerminalViewBuilder Block(BlockBuilder block);
    public void Draw(CellBuffer buf, Rect area);       // resizes the terminal to fit, copies cells, sets the cursor
}
```

- **Threads:** one reader thread per terminal feeds `VtScreen` under `Lock`. MiniTui's diffing and frame loop limit what reaches the real terminal, so a flood costs parsing, not drawing.
- **Back-pressure:** the reader stops reading while a parse holds the lock, and the kernel's PTY buffer then blocks the child.
- **Background apps** keep parsing, so they're current when switched to.

### 11.6 Acceptance for Phase 0
- [x] §8 and §11 reviewed by Ben; each **Decision** accepted or changed.
- [x] §9 recorded as decided.
- [x] The vtm and desktop-tui friction list from §3 is written.
- [x] The Phase 1 spec section (§12) is written before 1a starts.

## 12. Shell inside a terminal emulator (Phase 1)

### 12.1 Purpose
A daily terminal workspace: one app on the screen, one row of status, one prefix key, and nothing else. Everything in §11 is built here, and the shell on top of it. MiniTui itself doesn't change: `App.OnKey` sees keys before Ctrl+C and the focus chain, so the shell can pass everything through, and `WithKeyboardProtocol` is already on by default.

### 12.2 Repo
```
src/MiniTui.Vt/           the §11 library, with PublicApiAnalyzers
src/MiniTui.Desk/         the tuidesk executable
tests/MiniTui.Vt.Tests/
tests/MiniTui.Desk.Tests/
```
- Both reference the `MiniTui` 2.1.0 NuGet package, not a source checkout.
- The build gate is MiniTui's: `-warnaserror`, xUnit, and CI on Ubuntu. Every PR updates `CHANGELOG.md`.
- Nothing is published in Phase 1.

### 12.3 Screen and keys
The top rows are the foreground app, full width. The last row is the status line: app name, the app's title (OSC 0/2), notification count, clock. Do Not Disturb shows as a muted marker.

Every key goes to the foreground app except the prefix, `Ctrl+Space` by default. After the prefix, one key:

| Key | Action |
|---|---|
| `Tab` | The switcher (§12.4) |
| `Space` | The launcher (§12.4) |
| `1`–`9` | The app at that position |
| `n` | Notifications (§12.6) |
| `d` | Toggle Do Not Disturb |
| `x` | Close the app (§12.5) |
| `q` | Quit Desk, confirming if apps are running |
| `?` | Help |
| The prefix again | Send it to the app |
| `Esc`, or anything else | Cancel |

While waiting for that key, the status line shows the choices, and there's no timeout. Pastes go to the app, wrapped by §11.3.

The mouse goes to the app when it asked for mouse events, with no shell interpretation. Clicks on the status line open the switcher. The shell uses `WithMouse` and `WithHover`, since a hosted app may ask for any-motion events. Focus reporting (mode 1004) is sent to apps that enabled it when they're switched to and from.

### 12.4 Switcher and launcher
**Decision:** two lists, each with one job, both behind the prefix so that `Ctrl+Space` stays the only key taken from hosted apps.

- **Switcher (`Tab`):** running apps in most-recently-used order, with the previous app selected, so `Ctrl+Space`, `Tab`, `Enter` goes back to it. It can't start anything.
- **Launcher (`Space`):** apps from `apps.json`, plus a shell. It always starts a new instance.

Alt+Tab isn't used: Windows and most Linux desktops take it before the terminal sees it, and a terminal usually can't report Alt's release. `Alt+Tab` can be offered as an optional binding once Phase 3's bare console exists.

`CommandPalette` is sealed, titled " Commands ", and can't start on a chosen row, so both lists are a small `Overlay` in `MiniTui.Desk`, using `Fuzzy` for matching.

### 12.5 App lifecycle
- **Start:** with no config, Desk opens `$SHELL`, falling back to `/bin/sh`.
- **Environment:** the shell's, with `TERM=xterm-256color`, `COLORTERM=truecolor`, and `TERM_PROGRAM=tuidesk`.
- **Exit 0, or Ctrl+C (exit 130, or killed by SIGINT):** the app closes and the previous one comes to the front.
- **Any other non-zero exit, or killed by another signal:** the last screen stays, frozen, and the status line says how it ended (`exited 1`, `killed by SIGKILL`) with `r` to restart and `c` to close. In the background, it also adds a notification.
- **Decision: the last app closing** opens the launcher rather than quitting, so typing `exit` once doesn't end the session.
- **Close (`x`):** SIGHUP, then SIGKILL after 3 seconds. No protocol is involved until Phase 2.
- **Quit:** closes every app the same way, then restores the terminal.
- **Resize:** every app is resized when the terminal is, including background ones, so they're right when switched to.

### 12.6 Notifications
Phase 2 adds the protocol. Phase 1's sources need no cooperation from apps:
- OSC 9 and OSC 777 notifications.
- A bell from a background app ("Name wants attention"). A bell from the foreground app does nothing.
- A background app ending with a failure (§12.5).

**Do Not Disturb is on by default:** notifications only increase the count, and `prefix n` lists them. With it off, they also appear as a toast. The queue is capped at 100, dropping the oldest, and doesn't survive a restart.

### 12.7 Config
`~/.config/tuidesk/config.json` and `apps.json`. Both are optional, read at startup, and parsed with System.Text.Json source generation. A broken file doesn't stop Desk: it's ignored, with a notification saying why.

```json
{ "prefix": "ctrl+space", "doNotDisturb": true }
```
```json
[{ "id": "k9s", "name": "Kubernetes", "command": "k9s", "args": [], "cwd": "~", "env": {} }]
```

### 12.8 Limits accepted for v1
- **Cursor shape (DECSCUSR):** ignored, since `CellBuffer` can only place the cursor. A §7 candidate, with undercurl.
- **Graphics** (Sixel, kitty images): dropped, since a `Cell` holds text.
- **Nesting** `tuidesk` inside itself works, but the inner one only gets the prefix when it's sent twice.
- **No scrollback, session restore, or detach.** Hosted apps and the terminal handle their own scrollback.

### 12.9 Sub-phases and acceptance

**1a — Repo and PTY.** `PtyProcess`: the §11.1 spawn, with working directory, environment, resize, kill, and exit status.
- [x] CI builds and tests on Ubuntu.
- [x] Tests start real children and check the controlling terminal, size, `SIGWINCH`, Ctrl+C, signal mask and exit status.
- [x] The test suite passes under a Native AOT build of a small host, and alongside `System.Diagnostics.Process`.

**1b — Parser and screen.** `VtScreen`, with golden-screen tests.
- [x] SGR, cursor movement, scroll regions, insert and delete, alternate screen, wide characters and combining marks, line wrap, and resize.
- [x] UTF-8 and escape sequences split across any `Feed` boundary give the same screen.
- [x] Malformed input never throws, tested with random bytes.
- [x] `vttest`'s cursor and screen menus (SGR is part of the screen menu) are checked and the results recorded in [VTTEST.md](VTTEST.md).
- [x] Recorded sessions of nvim and htop replay to the expected screens.
- [x] Generated control sequences match tmux, and `tools/vt_golden.py` regenerates every expected screen.

**1c — Input and replies.** `VtInput`, `VtTerminal`, `TerminalViewBuilder`, query replies, OSC 52, and synchronized output.
- [ ] Round-trip tests against MiniTui's decoder, for every key and modifier it can report.
- [ ] A byte probe receives what bare tmux receives, for every key in [PRIOR-ART.md](PRIOR-ART.md).
- [ ] Each query in §11.4 gets its reply, and a hosted app can't read the clipboard.
- [ ] A small example embeds a shell pane in an ordinary MiniTui app.

**1d — The shell.** §12.3 to §12.7.
- [ ] Shell behaviour is tested with `TestBackend` and `ScriptedInputSource`.
- [ ] End-to-end tests run `tuidesk` itself inside a `VtTerminal`, hosting real children, covering the rest of this list.
- [ ] Every key but the prefix reaches the app, unchanged.
- [ ] A hosted app gets the full terminal minus one row, and follows a resize with nothing off-screen.
- [ ] Everything works without a mouse, and with no config.
- [ ] A crashed app keeps its screen and says how it ended.
- [ ] Ctrl+C during `yes` gets back to a prompt in under 500 ms.
- [ ] nvim starts with no terminal-response warning, and htop and k9s draw correctly.
- [ ] Claude Code runs correctly, including Shift+Enter when the outer terminal reports it.
- [ ] Ben uses it for a working session.

### 12.10 Out of scope
The protocol and socket (Phase 2), splits, floating windows, session persistence, themes beyond MiniTui's light and dark, and packaging.

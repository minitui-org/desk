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
| First-party apps (Phase 2) | `tui` and the app: `tuinotes`, with its data in `~/.local/share/tuinotes/` |
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
| `quitDeclined` | | The user chose to stay after a `quitRequest` |
| `bye` | | The app is closing the connection on purpose |

Shell → app:

| Type | Fields | Meaning |
|---|---|---|
| `welcome` | `protocol, theme` | The version to use: the lower of the two. `theme` is `light` or `dark` |
| `focus` | `focused` | Sent when it changes; apps may pause while in the background |
| `notificationAction` | `id, action` | The user chose an action from `notify.actions` |
| `quitRequest` | | Polite close. The app exits, or asks the user and sends `quitDeclined` if they choose to stay. The shell can always kill it |

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
    public void DeclineQuit();                                              // after QuitRequested, if the user chose to stay
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
    public Color DefaultForeground { get; set; }     // what OSC 10 and 11 report
    public Color DefaultBackground { get; set; }
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
    VtMouseMode Mouse, VtMouseEncoding MouseEncoding, int KeyboardFlags, bool SynchronizedOutput, bool FocusReporting);
```

- **`TERM`** is `xterm-256color`, so hosted apps need no terminfo entry of ours.
- **Styles `Cell` can't hold:** curly and coloured underlines become a plain underline; blink and OSC 8 hyperlinks are dropped (§7).
- **Synchronized output (mode 2026):** `TerminalView` draws the last complete frame until the mode ends, or for at most 200 ms.
- **Scrollback** isn't in v1. A parameter with no way to read the lines back wouldn't do anything, so the constructor gains one when an embedded pane needs it.
- **Where xterm and tmux differ,** `TERM` promises xterm, so xterm's behaviour wins. [VTTEST.md](VTTEST.md) lists the cases.

### 11.3 Input encoding
The shell gets decoded keys, and MiniTui has nothing that turns them back into bytes.

```csharp
public static class VtInput
{
    public static bool TryEncode(ConsoleKeyInfo key, VtModes modes, IBufferWriter<byte> output);
    public static bool TryEncode(MouseEvent mouse, VtModes modes, IBufferWriter<byte> output);  // false if the child didn't ask for it
    public static void EncodePaste(string text, VtModes modes, IBufferWriter<byte> output);
    public static bool TryEncodeFocus(bool focused, VtModes modes, IBufferWriter<byte> output);  // mode 1004, for §12.3
}
```

- Cursor keys follow `ApplicationCursorKeys`. Modified keys use xterm's `CSI 1;m` forms, or kitty's when the child pushed `KeyboardFlags`.
- **A key is encoded for the child, not copied from the outer terminal.** Shift+Enter reported in kitty's form reaches a child that didn't ask for kitty as `\r`, as it would from xterm, since a program like bash would otherwise print the sequence as text.
- **Not offered:** xterm's modifyOtherKeys (`CSI > 4 ; 2 m`) is ignored, as a program that asks for it falls back to the legacy forms, and kitty's alternate keys (flag 4) aren't sent, as they're optional in the protocol.
- Mouse coordinates are relative to the view; the child's mode and encoding decide what's sent.
- A paste is wrapped when the child enabled `BracketedPaste`, and any `ESC` in it is removed so that pasted text can't end the paste early. Line breaks become carriage returns, as from a keyboard.
- **Tests:** round trips against MiniTui's own decoder: encode, decode, compare, for every key and modifier it can report. `tools/key_probe.py` compares the bytes a real program receives with bare tmux. Results are in [VTTEST.md](VTTEST.md).
- **Known limit:** a child can only receive what the outer terminal reports and MiniTui decodes. Key-release events aren't available.
- **Fixed in MiniTui 2.1.1 (§7):** on an outer terminal without a keyboard protocol, MiniTui decoded byte `0x0A` as Enter, so Ctrl+J reached the child as `\r` rather than `\n`.

### 11.4 Queries and clipboard
Apps ask the terminal questions, and the emulator has to answer or they wait for a timeout.

| Query | Reply |
|---|---|
| Device attributes (`CSI c`, `CSI > c`) | `CSI ? 62 ; 22 c` (VT220 with color), and `CSI > 1 ; 10 ; 0 c` |
| Status and cursor position (`CSI 5 n`, `CSI 6 n`) | Ready; the cursor's position |
| Mode state (`DECRQM`), including 2026 | From `VtModes` |
| Foreground and background color (OSC 10, 11) | What the host sets, so apps pick the right light or dark look. A hosted app draws on the real terminal's default colors, so the host passes on `App.TerminalInfo`, not its theme. xterm's defaults until it's told |
| Kitty keyboard flags (`CSI ? u`) | `KeyboardFlags` |
| Anything else | No reply, as most terminals do |

**OSC 52:** the sequence stops at the emulator, so the host forwards writes with `Clipboard.SetText`. **Decision:** clipboard reads from hosted apps are refused with an empty reply in v1, since any process in a PTY could otherwise read it silently.

**OSC 9:** ConEmu uses the same number for other things, such as `9;4` for progress bars, so a body starting with a number and a semicolon isn't a notification.

### 11.5 Tying it together

```csharp
public sealed class VtTerminal : IDisposable
{
    public static VtTerminal Start(VtStartInfo info);    // file, args, environment, working directory, size
    public static VtTerminal Start(VtStartInfo info, Action<VtTerminal>? setup);  // setup runs before any output is read
    public VtScreen Screen { get; }                      // read under Lock
    public Lock Lock { get; }
    public int Pid { get; }
    public bool HasExited { get; }
    public PtyExit? Exit { get; }                        // an exit code or a signal, which §12.5 tells apart
    public event Action? Changed;                        // from a background thread, coalesced; use App.RequestRedraw
    public event Action? Exited;                         // a handler added after the exit runs at once
    public event Action? Bell;
    public event Action<string>? ClipboardSet;
    public event Action<string?, string>? Notified;
    public bool HandleKey(ConsoleKeyInfo key);
    public bool HandleMouse(MouseEvent mouse);
    public void HandlePaste(string text);
    public void HandleFocus(bool focused);
    public void Resize(int width, int height);
    public void Hangup();
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
- **Input:** a second thread per terminal writes from a queue, as a write waits while the child isn't reading. If the reader wrote replies itself, a child writing queries without reading the answers would deadlock it. Replies stop being queued past 1 MiB, so such a child can't fill memory.
- **Exit:** `Exited` waits up to 250 ms for the last output, and no longer, as a background process left behind can hold the terminal open.
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
examples/MiniTui.Vt.Example/  a shell in a pane of an ordinary MiniTui app
```
- Both reference the `MiniTui` NuGet package (2.5.0 since the lists got vim-style keys), not a source checkout. Its version is `MiniTuiVersion` in `Directory.Build.props`, which a build can override to try an unreleased one.
- The build gate is MiniTui's: `-warnaserror`, xUnit, and CI on Ubuntu. Every PR updates `CHANGELOG.md`.
- Nothing is published in Phase 1.

### 12.3 Screen and keys
The top rows are the foreground app, full width. The last row is the status line: the running apps, notification count, clock. Do Not Disturb shows as a muted marker. Each app shows its number for `1`–`9` and its name, and the current one is highlighted in the selection style with its title (OSC 0/2) after its name. The title is cut to leave room for the others, and if even their names don't fit, only the current app shows. A message (closing, waiting to quit, how an app ended, asked to quit) takes the apps' place while it applies.

Every key goes to the foreground app except the prefix, `Ctrl+Space` by default, which opens the launcher (§12.4). In the launcher and the switcher, typing filters, so their commands aren't plain letters:

| Key | Action |
|---|---|
| `Tab`, `Shift+Tab` | Swap between the launcher and the switcher |
| `1`–`9` | The app at that position, while the filter is empty |
| `Ctrl+X` | Close the current app (§12.5), whichever row is selected |
| `Ctrl+Q` | Quit Desk, confirming if apps are running |
| `Ctrl+N` | Notifications (§12.6) |
| `Ctrl+D` | Toggle Do Not Disturb |
| `Ctrl+S` | Send the prefix to the app |
| `F1` | Help |
| `Esc`, or the prefix again | Close, sending nothing |

Apart from `Tab`, each of these closes the list. A footer below a rule at the foot of the list shows the keys that fit, each key bold in the title color and what it does as text, and help lists them all. A prefix configured to one of these keys stays the prefix, and that command has no key. Pastes go to the app, wrapped by §11.3.

The mouse goes to the app when it asked for mouse events, with no shell interpretation. A click on another app's name in the status line switches to it, and a click anywhere else on it opens the switcher. The shell uses `WithMouse` and `WithHover`, since a hosted app may ask for any-motion events. Focus reporting (mode 1004) is sent to apps that enabled it when they're switched to and from.

Only the foreground app's clipboard writes (§11.4) are forwarded, so a program out of sight can't replace what the user copied.

### 12.4 Switcher and launcher
**Decision:** two lists, each with one job, both behind the prefix so that `Ctrl+Space` stays the only key taken from hosted apps. The prefix opens the launcher straight away, as starting an app is a main reason to press it, and `Tab` swaps it for the switcher.

- **Launcher:** apps from `apps.json`, plus a shell. It always starts a new instance.
- **Switcher:** running apps in most-recently-used order, with the previous app selected, so `Ctrl+Space`, `Tab`, `Enter` goes back to it. It can't start anything.

Alt+Tab isn't used: Windows and most Linux desktops take it before the terminal sees it, and a terminal usually can't report Alt's release. `Alt+Tab` can be offered as an optional binding once Phase 3's bare console exists.

`CommandPalette` is sealed, titled " Commands ", and can't start on a chosen row, so both lists are a small `Overlay` in `MiniTui.Desk`, using `Fuzzy` for matching.

### 12.5 App lifecycle
- **Start:** with no config, Desk opens `$SHELL`, falling back to `/bin/sh`.
- **Environment:** the shell's, with `TERM=xterm-256color`, `COLORTERM=truecolor`, and `TERM_PROGRAM=tuidesk`.
- **Exit 0, or Ctrl+C (exit 130, or killed by SIGINT):** the app closes and the previous one comes to the front.
- **Any other non-zero exit, or killed by another signal:** the last screen stays, frozen, and the status line says how it ended (`exited 1`, `killed by SIGKILL`) with `r` to restart and `c` to close. In the background, it also adds a notification.
- **Decision: the last app closing** opens the launcher rather than quitting, so typing `exit` once doesn't end the session.
- **Close (`Ctrl+X`):** SIGHUP, then SIGKILL after 3 seconds. No protocol is involved until Phase 2.
- **Quit:** closes every app the same way, then restores the terminal.
- **Resize:** every app is resized when the terminal is, including background ones, so they're right when switched to.

### 12.6 Notifications
Phase 2 adds the protocol. Phase 1's sources need no cooperation from apps:
- OSC 9 and OSC 777 notifications.
- A bell from a background app ("Name wants attention"). A bell from the foreground app does nothing.
- A background app ending with a failure (§12.5).

**Do Not Disturb is on by default:** notifications only increase the count, and `Ctrl+N` in the launcher or switcher lists them. With it off, they also appear as a toast. The queue is capped at 100, dropping the oldest, and doesn't survive a restart.

### 12.7 Config
`~/.config/tuidesk/config.json` and `apps.json` (`$XDG_CONFIG_HOME/tuidesk` when that's set). Both are optional, read at startup, and parsed with System.Text.Json source generation. A broken file doesn't stop Desk: it's ignored, with a notification saying why.

```json
{ "prefix": "ctrl+space", "doNotDisturb": true }
```
```json
[{ "id": "k9s", "name": "Kubernetes", "command": "k9s", "args": [], "cwd": "~", "env": {} }]
```

### 12.8 Limits accepted for v1
- **Cursor shape (DECSCUSR):** ignored, since `CellBuffer` can only place the cursor. A §7 candidate, with undercurl.
- **Graphics** (Sixel, kitty images): dropped, since a `Cell` holds text.
- **Nesting** `tuidesk` inside itself works, but the inner one only gets the prefix when the outer one sends it, with `Ctrl+Space`, `Ctrl+S`.
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
- [x] Round-trip tests against MiniTui's decoder, for every key and modifier it can report.
- [x] A byte probe receives what bare tmux receives, for every key in [PRIOR-ART.md](PRIOR-ART.md).
- [x] Each query in §11.4 gets its reply, and a hosted app can't read the clipboard.
- [x] A small example embeds a shell pane in an ordinary MiniTui app.

**1d — The shell.** §12.3 to §12.7.
- [x] Shell behaviour is tested with `TestBackend` and `ScriptedInputSource`.
- [x] End-to-end tests run `tuidesk` itself inside a `VtTerminal`, hosting real children, covering the rest of this list.
- [x] Every key but the prefix reaches the app, unchanged.
- [x] A hosted app gets the full terminal minus one row, and follows a resize with nothing off-screen.
- [x] Everything works without a mouse, and with no config.
- [x] A crashed app keeps its screen and says how it ended.
- [x] Ctrl+C during `yes` gets back to a prompt in under 500 ms.
- [x] nvim starts with no terminal-response warning, and htop and k9s draw correctly (by hand, [VTTEST.md](VTTEST.md)).
- [x] Claude Code runs correctly, including Shift+Enter when the outer terminal reports it (by hand, sent as raw bytes, [VTTEST.md](VTTEST.md)).
- [x] Ben uses it for a working session (2026-11-07).

### 12.10 Out of scope
The protocol and socket (Phase 2), splits, floating windows, session persistence, themes beyond MiniTui's light and dark, and packaging.

## 13. Native apps (Phase 2)

### 13.1 Purpose
Prove the second kind of app (§1): a MiniTui app that opts in with one call, shows its name and status in the shell, routes notifications through it, and behaves identically outside Desk. The apps are also dogfood: each one lists the framework gaps it hit, and each gap becomes a MiniTui issue with its standalone justification (§7). MiniTui core doesn't change for this phase unless such an issue is accepted there.

### 13.2 Repo
```
src/MiniTui.Desk.Protocol/          message types, DeskClient and WithDesk (§8.5), with PublicApiAnalyzers
tests/MiniTui.Desk.Protocol.Tests/
apps/<Name>/                        one executable per first-party app, with tests beside it
```
- `MiniTui.Desk.Protocol` depends only on `MiniTui`, and is trim- and AOT-safe (§5.2). CI's AOT check covers it.
- Nothing is published in Phase 2. Packaging waits for Phase 4.

### 13.3 Client
- `Connect` never throws and never blocks the app for long: anything going wrong leaves it inactive (§8.4).
- A background thread reads, and a bounded queue feeds a background writer. If the shell stops reading, messages are dropped rather than blocking the app.
- A message that would exceed 64 KiB is dropped by the client, not sent.
- With `WithDesk`, events run on the app's thread through `App.Post`, when the app next runs, so handlers added before `Run` hear everything. Raw `DeskClient` users get them on a background thread, one at a time. One that arrives before its handler is added is missed, and an exception from a handler there is ignored.
- When the connection drops, an app in the background is told it has the focus, so it doesn't stay paused.
- **Startup:** `appId` comes from `MINITUI_DESK_APP_ID`. The client waits up to 500 ms for `welcome` so that `Theme` is known at startup, and is active but with no theme if it hasn't arrived.

### 13.4 Shell
- **Socket:** created at startup as in §8.2, removed on exit, with a stale file from a dead shell replaced. Failing to create it is a notification, and Desk carries on with PTY apps only.
- **Environment:** every child gets `MINITUI_DESK_SOCKET` and `MINITUI_DESK_APP_ID`. The id is the `apps.json` id, or `shell`, plus an instance number: `notes-1`. An entry with no id is `app-1`, and can't be opened by `launch`. Numbers aren't reused within a session, and a restart keeps the id. Without a socket, both variables are removed, so a Desk running inside Desk doesn't pass on the outer one's.
- **`hello`:** an `appId` that isn't running closes the connection. Connections follow §8.1.
- **`title`:** wins over the OSC title for that app while its connection is open. Both still have control characters stripped.
- **`focus`:** sent on every switch, and when a connection arrives, alongside mode 1004.
- **`theme`:** from the terminal's background (MiniTui's `TerminalInfo.IsDark`) at `welcome`, and `dark` until the terminal has answered. MiniTui themes carry no light or dark flag, so `Theme.Current` can't say. The terminal's background isn't watched for changes, so it isn't sent again.

**Decision: notification policy.** The app's `urgency` is a request, and the user's Do Not Disturb wins:

| | DND on (default) | DND off |
|---|---|---|
| `low` | Count | Count |
| `normal` | Count | Count and toast |
| `high` | Count | Count and toast |

OSC notifications stay `normal`. A repeated `id` replaces the entry, keeps its position, and only counts as unread if it was read.

**Actions:** the notifications list (`Ctrl+N`) becomes selectable, and filters by typing like the other lists. An entry with actions is marked `actions`, Enter lists them, and choosing one sends `notificationAction` on the connection that sent the notification. Actions from a connection that has closed are hidden, and the entry stays.

**Decision: `launch`** is honored only from the foreground app, as a background app shouldn't take the screen. It starts a new instance of the `apps.json` entry, with `args` after the entry's own, and brings it to the front. From the background it's ignored.

**Decision: closing a native app.** An app with an open connection can stay and ask the user (§8.3), so nothing is killed behind its back:
- `Ctrl+X` sends `quitRequest`, and the status line says so while the app is still running: "Asked Notes to quit, Ctrl+X again to force". Pressing it again takes the hangup path of §12.5.
- `Ctrl+Q` still confirms first when apps are running (§12.3). It then sends `quitRequest` to every connected app, and closes the others as in §12.5 only once those have all gone. If a connected app stays open, Desk brings it to the front so the user can answer it, and the status line says "Waiting for Notes, Ctrl+Q again to force". Pressing it again takes the hangup path for whatever is left. There's no time limit, so being slow to answer can't lose work.
- **Staying:** `quitDeclined` cancels the quit and clears the status line, and every app that hasn't exited keeps running. An app that never sends it can't hold Desk: switching to another app also cancels the wait.
- Apps with no connection behave as they do today.

### 13.5 First-party apps
Each is one executable, uses `WithDesk`, and has a `README` section listing the gaps it hit, with links to their MiniTui issues. They keep their data in plain files under `$XDG_DATA_HOME/<app>`. They turn on the mouse (MiniTui's `WithMouse`), so clicks and the wheel reach them, in Desk or not; selecting the terminal's own text then takes Shift+drag.

- **Notes:** Markdown files, with a list and an editor. Sets its title to the open note. Detail in §13.8.
- **Tasks:** a list with due dates, stored as plain text (todo.txt). Notifies when something falls due, using actions for done and snooze. Detail in §13.9.
- **Reader:** reads local Markdown and text files, with `launch` opening Notes from a passage. Detail in §13.10.

### 13.6 Sub-phases and acceptance
One branch and PR each.

**2a — Protocol and client.** §8 and §13.3.
- [x] Every message in §8.3 round-trips, and unknown types and fields are ignored.
- [x] With either variable missing, or an unreachable socket, every member is a no-op and nothing throws.
- [x] A dropped connection, a silent shell, and a shell that stops reading never block or throw into the app.
- [x] `WithDesk` delivers events through `App.Post`, and with no handler `quitRequest` calls `app.Quit`.
- [x] An AOT build of a small app using it publishes with no warnings, and runs.

**2b — Shell side.** §8.1, §8.2 and §13.4.
- [x] The socket directory is mode `0700`, another user's `/tmp` directory is refused, and the socket goes away on exit.
- [x] A 64 KiB+ line closes that connection, anything before `hello` is ignored, and no client behaviour can take down the shell.
- [x] Titles, the notification policy table, replacement by `id`, actions, `launch`, both close paths and `quitDeclined` are tested with `TestBackend` and with a real client in a real child.
- [x] Several connections on one id follow §8.1, including a MiniTui app started from a hosted bash.

**2c, 2d, 2e — One app each.** §13.5. Notes (2c), Tasks (2d) and Reader (2e) are done: §13.8 to §13.10.
- [x] The app works with the keyboard alone, and with no existing data. Notes, Tasks and Reader: done.
- [x] Its screens are identical in Desk and outside it, tested by running it both ways. Only the status line and notifications differ. Notes, Tasks and Reader: done.
- [x] Its gaps are filed against MiniTui. Notes: minitui#74 to #79, fixed in MiniTui 2.2.0, and #85 (help while typing), fixed in 2.3.0, which Notes and `tuidesk` now use in place of their workarounds. Tasks: minitui#88 (row styles), first worked around with `✓` and `Overdue` in the text, then fixed in MiniTui 2.4.0: done tasks are muted and an overdue time is in the error color, and the `✓` went, as the hint says `Done`. Reader: minitui#89 (no Markdown widget), #90 (paragraph text can only be selected with the mouse) and #91 (wrapping styled text is internal), first worked around with its own formatter, a highlighted block and its own wrapping, then fixed in MiniTui 2.4.0, where Reader's formatter and block highlight moved; Reader now uses `Widget.Markdown`.

**Phase exit:** the apps show their name and status in the shell and route notifications through it, and Ben uses them in a working session.

### 13.7 Out of scope
Clipboard over the protocol, rendering over the socket, IPC between apps (§8.4), a manifest format and `dotnet new` template (Phase 4), and publishing packages.

### 13.8 Notes (2c)
`apps/Notes/`, the `tuinotes` executable. Plain Markdown files, written as typed, in `$XDG_DATA_HOME/tuinotes/` (normally `~/.local/share/tuinotes/`).

- **Files:** one note is one `<name>.md` file directly in the folder. Other files, hidden ones and folders are left alone and not listed. The folder is created when the first note is. Notes are written as UTF-8, by writing a temporary file and renaming it over the note, so a crash leaves the old version or the new one, never half of one.
- **Screen:** the list of notes on the left, most recently changed first with the time it changed, and the selected note in an editor on the right. A status bar shows the keys. Under 50 columns only the side being used shows. With no notes, the editor says how to make one.
- **Names:** a name can't be empty, start with `.`, contain `/`, or match another note's name ignoring case.

| Key | Where | Does |
|---|---|---|
| `↑` `↓` `j` `k` `PgUp` `PgDn` `Home` `End` | List | Select a note, which shows in the editor |
| `/`, then typing | List | Filter by name, opening the best match. `Enter` keeps the filter and `Esc` clears it, and either gives the keys back to the list |
| Click, double-click | List | Open a note; edit it |
| `Enter`, `Tab`, `l` | List | Edit the selected note |
| `Esc` | Editor | Back to the list |
| `Tab`, `Shift+Tab` | Editor | Indent and outdent the line, or the selected lines |
| `Ctrl+N` | Both | New note, asking for its name, which starts as the filter |
| `F2` | Both | Rename the selected note |
| `Delete` | List | Delete the selected note, after confirming |
| `F1`, or `?` in the list | Both | Show the keys |

- **Decision, asked:** the list has the keys, not its filter line, here and in Tasks (§13.9) and Reader (§13.10): `j` and `k` move and `/` starts the filter, as people who use vim-style keys expect. The lists first filtered as you typed, which put `j` in the filter. Desk's launcher and switcher keep filtering as you type (§12.3), since they're opened to find one thing by name. It's MiniTui's `FilterListState.TypeToFilter`, off (MiniTui 2.5.0).
| `Ctrl+Q` | Both | Quit |

The editor is MiniTui's `TextArea`, with its own keys: undo, find, selection and the clipboard. The Markdown is edited as text, with no preview. `Tab` indents with spaces to the next multiple of 4 (MiniTui's `TabIndents`), and Enter keeps the line's indentation. The editor holds only spaces, so a tab character in a note made elsewhere becomes spaces when it's next saved.

**Decision: autosave.** There is no save key and no "unsaved changes" question. A note is saved 1 second after the last change, and before anything else happens to it: selecting another note, renaming it, losing the focus in Desk, and quitting by `Ctrl+Q`, `quitRequest`, hangup or `SIGTERM`. If a save fails, Notes shows why and keeps the text. A quit that can't save stays open, and inside Desk it sends `quitDeclined`, so the text isn't lost.
- **Changed elsewhere:** Notes reads the folder again when it gets the focus back and when a note is selected, and a note with no unsaved changes is reloaded if its file changed. If the file changed on disk while Notes has unsaved changes too, Notes doesn't overwrite it: it saves its text as a new note, `<name> (conflict)`, says so, and carries on in that one.
- **Title:** the selected note's name, or nothing when there are no notes.
- **`--append <name> <text>`:** `tuinotes --append garden-plan "…"` adds the text to the end of that note, making it if there's none (names match ignoring case), with a blank line between it and what was there. It saves at once, then opens the note in the editor with the cursor at the end. A name that can't be a note's name (§13.8 Names) is refused, and Notes opens as usual and says why. Reader uses it (§13.10).
- **Theme:** MiniTui's light and dark themes, picked from the terminal's background as MiniTui does. Inside Desk the hosted terminal reports the real background (§11.4), so the screen is the same either way.


### 13.9 Tasks (2d)
`apps/Tasks/`, the `tuitasks` executable. One [todo.txt](https://github.com/todotxt/todo.txt) file, `$XDG_DATA_HOME/tuitasks/todo.txt` (normally `~/.local/share/tuitasks/todo.txt`), so other todo.txt tools and editors can read and change it too.

**Decision: todo.txt.** Due dates are what Tasks is for, and todo.txt is the plain-text format where they mean the same in other tools. A Markdown checklist would read as a list in Notes, but has no standard for dates.

- **Lines:** one task per line, as the format says: `x` and the date it was done, a priority `(A)` to `(Z)`, the date it was made, then the text, with `+project`, `@context` and `key:value` tags. Blank lines are skipped. A line Tasks can't make sense of is a task whose text is the whole line.
- **Tags Tasks reads:** `due:YYYY-MM-DD`, the common extension, and `at:HH:MM`, a time on that day, which other tools ignore. `snooze:YYYY-MM-DDTHH:MM` holds a snoozed reminder. Every other tag is kept as written.
- **Done:** marking a task done puts `x` and today's date in front, and its priority becomes a `pri:A` tag, as `todo.sh` does; undoing it puts both back. Done tasks stay in the file, and are listed after the others, most recently done first. There's no archive to `done.txt`.
- **New tasks** are added at the end of the file with today's date as the date they were made.
- **Writing:** every change reads the file again first and changes only its own line, then writes the whole file by renaming a temporary file over it, as Notes does. Every other line is written back byte for byte. If the line has changed or gone meanwhile, the change isn't made: Tasks says so and shows the file as it is now. The folder and file are made with the first task.
- **Changed elsewhere:** the file is read again when Tasks gets the focus back in Desk, and once a minute if its time has changed.

**Screen:** one list, filtered with `/`, of tasks in this order: those with a due date by when they're due (so overdue ones first), then those without by priority and their place in the file, then the done ones. Each row is the task's text without the date tags, the priority in front, and its due time on the right: `Overdue · Nov 5` (in the error color, as well as the word, so it doesn't rest on color alone), `Today 15:00`, `Tomorrow`, `Fri`, `Nov 20`, or `2027-01-04` in another year. Done tasks are muted, with `Done · Nov 6` on the right. With no tasks, the list says how to add one.

| Key | Does |
|---|---|
| `↑` `↓` `j` `k` `PgUp` `PgDn` `Home` `End` | Select a task |
| `/`, then typing | Filter. `Enter` keeps the filter and `Esc` clears it, and either gives the keys back to the list |
| Click, double-click | Select a task; edit it |
| `Ctrl+N` | New task, asking for its text, which starts as the filter |
| `Enter` | Edit the selected task's text, tags and priority included |
| `Ctrl+D` | Done, or not done again |
| `Ctrl+T` | Set when it's due: `today`, `tomorrow`, a weekday, `+3d`, `+2w` or `2026-11-20`, and optionally a time, `15:00`. Empty clears it |
| `Delete` | Delete the selected task, after confirming |
| `F1` | Show the keys |
| `Ctrl+Q` | Quit |

**Decision: reminders.** A task is due at its `at:` time, or at 09:00 if it has only a date. While Tasks is running, a task that falls due notifies once, titled with its text and saying `Due 15:00` or `Due today`, with two actions: **Done**, and **Snooze**, which reminds again in an hour by writing `snooze:`. When Tasks starts, tasks already past due get one notification between them, "3 tasks overdue", rather than one each; a single one gets its own, with its actions. Nothing notifies while Tasks isn't running: there's no background service in Phase 2. Inside Desk notifications go to the shell with `normal` urgency, so Do Not Disturb holds them as counts; outside it they're toasts.
- **Title:** `2 overdue, 3 today`, leaving out a zero, or nothing when neither.
- **Theme** as in Notes (§13.8).

### 13.10 Reader (2e)
`apps/Reader/`, the `tuireader` executable. It reads Markdown and text files where they already are, and sends passages to Notes.

**Decision: browsing folders.** Reader shows the files where they are, rather than in a folder of its own, so nothing has to be copied in first.
- **Where it starts:** `tuireader [path]`. A folder starts the list there, and a file opens straight away with the list in its folder. With no argument the list starts in the current folder, which inside Desk is the `apps.json` entry's `cwd`. A path that doesn't exist starts in the current folder and says so.
- **The list:** the folder's subfolders, then its `.md`, `.markdown` and `.txt` files, each group by name ignoring case, with `..` first unless the folder is `/`. Hidden files and folders aren't listed. Links are followed. The list's title is the folder, with the home folder as `~`. A folder that can't be read says why in the list.
- **Reading a file:** as UTF-8, with a byte order mark skipped and invalid bytes shown as `�`. A file over 10 MiB, or with a NUL byte in its first 8 KiB, isn't shown: the page says why. Tabs become spaces to the next multiple of 4. Line ends can be `\n` or `\r\n`.
- **Changed elsewhere:** the open file is read again when Reader gets the focus back in Desk and when it's selected again, keeping the highlighted block by its number.

**Decision: formatted Markdown, drawn by MiniTui.** Reader first formatted a CommonMark subset itself, as MiniTui had no Markdown widget (minitui#89); that code moved into MiniTui 2.4.0 as `Widget.Markdown` and `MarkdownState`, which Reader now uses, with the theme's `Heading`, `Code` and `Link` styles:

| Markdown | Shown as |
|---|---|
| `#` to `######` headings, and `===` and `---` underlines | The text in the heading style. Level 1 is underlined with `═`, level 2 with `─`. Levels 3 to 6 are bold. A blank row comes before and after each |
| Paragraphs | Wrapped to the page. Lines that follow on are joined; two trailing spaces or a trailing `\` break the line |
| `*a*` `_a_`, `**a**` `__a__`, `~~a~~` | Italic, bold, struck through, without the marks |
| `` `code` `` | In the code style, without the backticks |
| `[text](url)`, `<url>` | The text in the link style. The URL isn't shown |
| `![alt](url)` | `[image: alt]` in the muted style |
| `-` `*` `+` lists, `1.` `1)` lists | `•` or the number, with wrapped lines indented under the text. Nested lists indent by 2 more |
| `- [ ]`, `- [x]` | `☐` and `☑` in place of the bullet |
| `>` quotes | A `│` bar in the muted style before each row, nested for `> >` |
| Fenced (```` ``` ````, `~~~`) and indented code | As written in the code style, indented by 2, wrapped at the page's edge rather than at words |
| `---`, `***`, `___` on their own | A `─` rule across the page |
| Tables | In aligned columns with a rule under the header, the widest cells wrapped to fit; a table too narrow for its columns as written |
| HTML, link definitions, anything else | As written, wrapped like a paragraph |

- **The page** is wrapped to the pane's full width, leaving 2 columns on the left for the highlight bar. A `.txt` file is shown as written, wrapped, with blank lines between its paragraphs.
- A front matter block (`---` on the first line, up to the next `---`) isn't shown.

**Decision: a highlighted block is the passage.** Reader has to work with the keyboard alone. So one block is always highlighted, by `MarkdownState`'s block cursor, with a `▌` bar in the border color beside its rows (the focused border color while the page has focus): a heading, a paragraph, a list item, a quote paragraph, a code block, a rule or a table. `↑` and `↓` move the bar a block at a time, and the page scrolls to keep it in view. `Shift+↑` and `Shift+↓` stretch it over the blocks above or below. When the wheel or the scrollbar scrolls the bar out of view, it moves to the nearest block in view. A mouse drag still selects exact text, as in any MiniTui paragraph, and so does `F7`'s caret (MiniTui 2.4.0, minitui#90), which starts at the top of the highlight; while there's a selection it's the passage.

**Decision: one note per document.** `Ctrl+N` sends the passage to a note named after the file without its extension, such as `garden-plan` for `garden-plan.md`, made if it doesn't exist and added to the end if it does. A leading `.` is dropped from the name, and an empty name becomes `Reader`. The passage goes in as a Markdown quote of the source as written (a mouse selection: as shown), then a line with where it's from, `— ~/docs/garden-plan.md`, and a blank line where Notes puts the cursor to write:

```
> Tomatoes need six hours of sun
> and deep watering twice a week.

— ~/docs/garden-plan.md

```

- **Inside Desk** Reader sends `launch` for the `notes` entry with the arguments `--append <name> <text>`, and Desk brings that Notes to the front (§13.4). Desk ignores an id that isn't in `apps.json`, which Reader can't see, so Reader says "Sent to Notes" rather than that Notes opened, and its README says the entry's id must be `notes`.
- **Outside Desk** Reader suspends itself with MiniTui's `App.Suspend` and runs `tuinotes --append <name> <text>` in the same terminal: the one next to `tuireader` if it's there, otherwise from `PATH`. Reader comes back when Notes quits. If there's no `tuinotes`, Reader says so.
- **Size:** a passage over 16 KiB isn't sent, and Reader says so, which keeps `launch` well under the protocol's 64 KiB line (§8.2).
- `Ctrl+C` copies the passage, as shown, to the clipboard.

**Screen:** the list on the left and the page on the right, as in Notes, with a status bar showing the keys. Under 50 columns only the side being used shows. With no file open, the page says how to open one, or that the folder has nothing to read; an empty file says so.

| Key | Where | Does |
|---|---|---|
| `↑` `↓` `j` `k` `PgUp` `PgDn` `Home` `End` | List | Select a file or folder, and show a selected file |
| `/`, then typing | List | Filter by name. `Enter` keeps the filter and `Esc` clears it, and either gives the keys back to the list |
| `Enter`, `→`, `l` | List | Go into a folder, or read a file |
| `←`, `h`, `Backspace` | List | Up to the folder above (`←` and `Backspace` edit a filter being typed) |
| `h` | Page | Back to the list. With the caret showing, `h` and `l` move it |
| `Tab` | Both | Swap between the list and the page |
| Click, double-click | List | Select; go in or read |
| `↑` `↓` `j` `k` | Page | Move the highlight a block |
| `Shift+↑` `Shift+↓` | Page | Stretch the highlight over the block above or below |
| `PgUp` `PgDn` `Home` `End` | Page | Scroll a page, or to the top or bottom, and move the highlight with it |
| Click, drag, wheel | Page | Highlight a block; select text; scroll |
| `Ctrl+N` | Page | Send the passage to Notes |
| `Ctrl+C` | Page | Copy the passage |
| `F7` | Page | Show a caret: the arrows move it and `Shift` with them selects text. `F7` or `Esc` hides it |
| `Esc` | Page | Clear a selection, hide the caret, or back to the list |
| `F1` | Both | Show the keys |
| `Ctrl+Q` | Both | Quit |

- **Places:** Reader remembers the highlighted block of the last 200 files read, in `$XDG_DATA_HOME/tuireader/places.txt`, one file per line as `<block>\t<path>`, most recent first, and opens each file where you left it. The file is written when you leave a file or quit, as in Notes by renaming a temporary file over it. If it can't be read or written, Reader carries on without it.
- **Title:** the open file's name, or the folder when none is open.
- **Theme** as in Notes (§13.8).
- **Out of scope here:** finding text in the page, following links, images, syntax highlighting in code blocks, and other formats (HTML, PDF, EPUB).

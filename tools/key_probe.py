#!/usr/bin/env python3
"""Compare the bytes a program receives for each key, directly in tmux and hosted.

    dotnet build src/MiniTui.Desk.sln -c Release
    tools/key_probe.py          # hosted in the MiniTui.Vt example
    tools/key_probe.py --desk   # hosted in tuidesk, where Ctrl+Space is sent with Ctrl+S

The acceptance check for spec 12.9: the keys in docs/PRIOR-ART.md arrive unchanged. Uses a private tmux server, and
exits non-zero if any key differs in a way that isn't listed in EXPECTED_DIFFERENCES.
"""
import os
import shutil
import subprocess
import sys
import tempfile
import time

SOCKET = "minitui-vt-probe"
EXAMPLE = os.path.join(os.path.dirname(__file__), "..", "examples", "MiniTui.Vt.Example", "bin", "Release", "net10.0", "MiniTui.Vt.Example")
TUIDESK = os.path.join(os.path.dirname(__file__), "..", "src", "MiniTui.Desk", "bin", "Release", "net10.0", "tuidesk")
DESK = "--desk" in sys.argv

PROBE = r"""
import os, sys, tty
tty.setraw(0)
if sys.argv[2] == "kitty":
    os.write(1, b"\x1b[>1u")
if sys.argv[2] == "mouse":
    os.write(1, b"\x1b[?1000h\x1b[?1006h")
out = os.open(sys.argv[1], os.O_WRONLY | os.O_CREAT | os.O_APPEND)
os.write(out, b"READY\n")
while True:
    data = os.read(0, 1024)
    if not data:
        break
    os.write(out, repr(data).encode() + b"\n")
"""

# name, how tmux sends it. A hex string goes straight to whatever is in the pane, as a terminal's bytes would.
PRIOR_ART = [
    ("a", ["a"]),
    ("Esc", ["Escape"]),
    ("Ctrl+B", ["C-b"]),
    ("Up", ["Up"]),
    ("Ctrl+Space", ["C-Space"]),
    ("Alt+Left", ["M-Left"]),
    ("Ctrl+PageUp", ["C-PPage"]),
    ("Ctrl+Shift+Up", ["C-S-Up"]),
    ("Alt+Enter", ["M-Enter"]),
    ("F10", ["F10"]),
]

OTHERS = [(name, [key]) for name, key in [
    ("Enter", "Enter"), ("Tab", "Tab"), ("Shift+Tab", "BTab"), ("Backspace", "BSpace"), ("Space", "Space"),
    ("Down", "Down"), ("Left", "Left"), ("Right", "Right"), ("Home", "Home"), ("End", "End"),
    ("Insert", "IC"), ("Delete", "DC"), ("PageUp", "PPage"), ("PageDown", "NPage"),
    ("F1", "F1"), ("F2", "F2"), ("F3", "F3"), ("F4", "F4"), ("F5", "F5"), ("F6", "F6"),
    ("F7", "F7"), ("F8", "F8"), ("F9", "F9"), ("F11", "F11"), ("F12", "F12"),
    ("Ctrl+A", "C-a"), ("Ctrl+C", "C-c"), ("Ctrl+D", "C-d"), ("Ctrl+H", "C-h"), ("Ctrl+I", "C-i"), ("Ctrl+J", "C-j"),
    ("Ctrl+M", "C-m"), ("Ctrl+Z", "C-z"), ("Ctrl+\\", "C-\\"), ("Ctrl+]", "C-]"), ("Ctrl+_", "C-_"),
    ("Alt+A", "M-a"), ("Alt+Shift+A", "M-A"), ("Ctrl+Alt+A", "C-M-a"), ("Alt+Backspace", "M-BSpace"),
    ("Shift+Left", "S-Left"), ("Ctrl+Left", "C-Left"), ("Alt+Up", "M-Up"), ("Ctrl+Shift+Right", "C-S-Right"),
    ("Ctrl+Home", "C-Home"), ("Shift+F1", "S-F1"), ("Ctrl+F5", "C-F5"), ("Alt+F12", "M-F12"),
    ("Ctrl+Delete", "C-DC"),
]] + [("é", ["-l", "é"]), ("日", ["-l", "日"]), ("🎉", ["-l", "🎉"])]

# Forms a terminal with the kitty protocol sends, which a hosted program should see only if it asked for them.
KITTY = [
    ("Shift+Enter", "\x1b[13;2u", b"\r"),
    ("Ctrl+Enter", "\x1b[13;5u", b"\r"),
    ("Alt+Enter", "\x1b[13;3u", b"\x1b\r"),
    ("Ctrl+Space", "\x1b[32;5u", b"\x00"),
    ("Ctrl+A", "\x1b[97;5u", b"\x01"),
    ("Alt+A", "\x1b[97;3u", b"\x1ba"),
    ("Ctrl+Shift+A", "\x1b[97;6u", b"\x01"),
    ("Esc", "\x1b[27u", b"\x1b"),
    ("Ctrl+Backspace", "\x1b[127;5u", b"\x08"),
    ("Shift+Tab", "\x1b[9;2u", b"\x1b[Z"),
]

# Where bare tmux and Desk differ on purpose, with the reason. tmux's own keys follow its own TERM, and a hosted
# program has been told TERM=xterm-256color.
EXPECTED_DIFFERENCES = {
    "Home": "tmux's form is CSI 1~; xterm-256color's is CSI H",
    "End": "tmux's form is CSI 4~; xterm-256color's is CSI F",
}


def tmux(*args, check=True):
    return subprocess.run(["tmux", "-L", SOCKET, "-f", "/dev/null", *args], check=check, capture_output=True, text=True)


def read_lines(path):
    with open(path, "rb") as f:
        return f.read().decode().splitlines()


def run(command, mode, directory, name):
    """Start the probe in a pane, optionally hosted, and return a function that sends a key and returns the bytes."""
    log = os.path.join(directory, name)
    script = os.path.join(directory, "probe.py")
    with open(script, "w") as f:
        f.write(PROBE)
    probe = f"{sys.executable} -I {script} {log} {mode}"
    hosted = command != ""
    if hosted and DESK:
        wrapper = os.path.join(directory, name + ".sh")
        with open(wrapper, "w") as f:
            f.write(f"#!/bin/sh\nexec {probe}\n")
        os.chmod(wrapper, 0o755)
        line = f"env SHELL={wrapper} {command}"
    else:
        line = (command + " " + probe).strip()
    tmux("new-session", "-d", "-s", name, "-x", "120", "-y", "35", line)

    deadline = time.time() + 15
    while not (os.path.exists(log) and read_lines(log)):
        if time.time() > deadline:
            raise SystemExit(f"The probe didn't start in {name}: {tmux('capture-pane', '-t', name, '-p').stdout}")
        time.sleep(0.05)
    time.sleep(0.3)  # The host's own startup queries time out in this time.

    def send(args, raw=None):
        before = len(read_lines(log))
        if raw is not None:
            tmux("send-keys", "-t", name, "-H", *[f"{b:02x}" for b in raw.encode()])
            if hosted and DESK and raw == "\x1b[32;5u":
                tmux("send-keys", "-t", name, "C-s")
        else:
            tmux("send-keys", "-t", name, *args)
            # In tuidesk it's the prefix, and Ctrl+S in the launcher sends it on.
            if hosted and DESK and args == ["C-Space"]:
                tmux("send-keys", "-t", name, "C-s")
        # Long enough for the host's Esc timeout, and for a slow machine.
        time.sleep(0.15)
        lines = read_lines(log)[before:]
        return b"".join(eval(line) for line in lines)  # Our own repr() output, from the probe above.

    return send


def main():
    if not shutil.which("tmux"):
        raise SystemExit("tmux isn't installed")
    example = os.path.abspath(TUIDESK if DESK else EXAMPLE)
    if not os.path.exists(example):
        raise SystemExit("Build first: dotnet build src/MiniTui.Desk.sln -c Release")

    tmux("kill-server", check=False)
    failures = 0
    with tempfile.TemporaryDirectory() as directory:
        bare = run("", "plain", directory, "bare")
        hosted = run(example, "plain", directory, "hosted")
        bare_kitty = run("", "kitty", directory, "barekitty")
        hosted_kitty = run(example, "kitty", directory, "hostedkitty")
        hosted_mouse = run(example, "mouse", directory, "hostedmouse")

        print("## Keys in docs/PRIOR-ART.md, and others\n")
        print("| Key | Bare tmux | Hosted | |")
        print("|---|---|---|---|")
        for name, args in PRIOR_ART + OTHERS:
            a, b = bare(args), hosted(args)
            note = "same" if a == b else "**differs**"
            reason = EXPECTED_DIFFERENCES.get(name)
            if a != b:
                if reason:
                    note = reason
                else:
                    failures += 1
            print(f"| {name} | `{a!r}` | `{b!r}` | {note} |")

        print("\n## Kitty forms from the outer terminal\n")
        print("| Key | Sent | Program without kitty | Expected | Program with kitty | Expected | |")
        print("|---|---|---|---|---|---|---|")
        for name, sequence, legacy in KITTY:
            a = hosted([], raw=sequence)
            direct = bare_kitty([], raw=sequence)
            b = hosted_kitty([], raw=sequence)
            good = a == legacy and b == direct
            failures += 0 if good else 1
            print(f"| {name} | `{sequence!r}` | `{a!r}` | `{legacy!r}` | `{b!r}` | `{direct!r}` | {'ok' if good else '**differs**'} |")

        print("\n## Mouse and paste\n")
        print("| What | Sent | Received | Expected | |")
        print("|---|---|---|---|---|")
        # The example draws a border, so its pane starts at column 2, row 2. In tuidesk, the app starts at the corner,
        # and outside it is the status line.
        at = (lambda sequence: sequence) if DESK else (lambda sequence: sequence.replace(";5;5", ";4;4"))
        outside = "\x1b[<0;5;35M" if DESK else "\x1b[<0;115;5M"
        for name, send, sequence, expected in [
            ("Click, moved into the pane", hosted_mouse, "\x1b[<0;5;5M", at("\x1b[<0;5;5M").encode()),
            ("Release", hosted_mouse, "\x1b[<0;5;5m", at("\x1b[<0;5;5m").encode()),
            ("Wheel", hosted_mouse, "\x1b[<64;5;5M", at("\x1b[<64;5;5M").encode()),
            ("Click outside the pane", hosted_mouse, outside, b""),
            ("Click, program didn't ask", hosted, "\x1b[<0;5;5M", b""),
            ("Paste, line breaks become CR", hosted, "\x1b[200~a\nb\x1b[201~", b"a\rb"),
        ]:
            got = send([], raw=sequence)
            good = got == expected
            failures += 0 if good else 1
            print(f"| {name} | `{sequence!r}` | `{got!r}` | `{expected!r}` | {'ok' if good else '**differs**'} |")

    tmux("kill-server", check=False)
    print(f"\n{failures} unexpected difference(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())

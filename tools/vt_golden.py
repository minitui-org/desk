#!/usr/bin/env python3
"""Records terminal sessions and gets their expected screens from tmux, which is an independent
terminal, so VtScreen isn't only tested against one reading of the standards.

  vt_golden.py record nvim htop     Run the programs in a PTY, scripted, and save what they write.
  vt_golden.py record vttest-cursor vttest-screen    The same for vttest's menus, inside tmux.
  vt_golden.py fuzz --seed 1 ...    Generate hostile but well-formed control sequences instead.
  vt_golden.py golden               Replay every session through tmux and save the screens.

A session is a directory under tests/MiniTui.Vt.Tests/Sessions: session.bin holds the output,
session.txt the size and the offsets to check at, and NN.screen what tmux showed at each. The first
line of a screen is "cursor_x cursor_y cursor_visible", and the rest is `capture-pane -e -N`.

Needs tmux (3.4 made these), nvim, htop, vttest and util-linux's unshare, which keeps other processes out of
htop's list. Recordings are public: check them for anything personal before committing.
"""
import argparse, os, pty, random, select, shutil, struct, subprocess, sys, tempfile, time, fcntl, termios
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent / "tests/MiniTui.Vt.Tests/Sessions"
SOCKET = "minitui-vt-golden"
WIDTH, HEIGHT = 80, 24

FIXTURE = """-- A fixture for the recorded nvim session.
local function greet(name)
  print("héllo, " .. name .. " 日本語 ✓")
end

for i = 1, 40 do
  greet("line " .. i)
end
"""

# (keys, seconds to wait afterwards). A checkpoint is taken after each step.
SCRIPTS = {
    "nvim": (["nvim", "--clean", "fixture.lua"], [
        (b"", 2.0),
        (b":syntax on\r:set number\r", 0.5),
        (b"Ohello \xe6\x97\xa5\xe6\x9c\xac wide\x1b", 0.5),
        (b":split\r", 0.5),
        (b":vsplit\r", 0.5),
        (b"\x17o", 0.5),                # Ctrl+W o: back to one window
        (b"G", 0.3),
        (b"gg", 0.3),
        (b"\x04", 0.3),                 # Ctrl+D: scroll
        (b":help motion\r", 0.8),
        (b"\x06", 0.3),                 # Ctrl+F: page
        (b":q\r", 0.3),
        (b":%s/line/LINE/g\r", 0.5),
        (b":q!\r", 0.5),
    ]),
    "htop": (["unshare", "--user", "--map-root-user", "--pid", "--fork", "--mount-proc", "htop", "-d", "100"], [
        (b"", 1.5),
        (b"\x1b[B\x1b[B", 0.3),         # down
        (b"\x1bOQ", 0.5),               # F2: setup
        (b"\x1b", 0.5),
        (b"\x1bOP", 0.5),               # F1: help
        (b"\x1b", 0.5),
        (b"t", 0.5),                    # tree view
        (b"t", 0.5),
        (b"q", 0.5),
    ]),
}


# Menu choices and returns for vttest's "Test of cursor movements" and "Test of screen features", and
# the steps tmux gets wrong by vttest's own instructions, or that only make sense at 132 columns. They
# go in skipped.txt instead, for VttestTests to check against what vttest says should be on screen.
VTTEST = {
    "vttest-cursor": (["1"] + [""] * 7 + ["0"], {2, 3, 4}),
    "vttest-screen": (["2"] + [""] * 16 + ["0"], {3, 4, 11, 14}),
}


def record(name):
    command, steps = SCRIPTS[name]
    with tempfile.TemporaryDirectory() as home:
        Path(home, "fixture.lua").write_text(FIXTURE)
        master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", HEIGHT, WIDTH, 0, 0))
        env = {"PATH": os.environ["PATH"], "HOME": home, "TERM": "xterm-256color", "COLORTERM": "truecolor",
               "LANG": "C.UTF-8", "USER": "user", "HOSTNAME": "host", "XDG_CONFIG_HOME": home + "/c", "XDG_DATA_HOME": home + "/d",
               "XDG_STATE_HOME": home + "/s", "XDG_CACHE_HOME": home + "/h"}
        child = subprocess.Popen(command, stdin=slave, stdout=slave, stderr=slave, cwd=home, env=env, start_new_session=True,
                                 preexec_fn=lambda: fcntl.ioctl(0, termios.TIOCSCTTY, 0))
        os.close(slave)
        output, offsets = bytearray(), []
        for keys, wait in steps:
            if keys:
                os.write(master, keys)
            drain(master, output, wait)
            offsets.append(len(output))
        drain(master, output, 0.3)
        child.kill()
        child.wait()
        session = save(name, bytes(output), offsets)
    for needle in (os.environ.get("USER", "?"), os.uname().nodename, str(Path.home()), home):
        if needle.encode() in output:
            print(f"warning: {name} contains {needle!r}", file=sys.stderr)
    print(f"{session}: {len(output)} bytes, {len(offsets)} checkpoints")


def record_vttest(name, keys, skip):
    """vttest asks the terminal questions, so it runs inside tmux, which answers, with `script` keeping its output."""
    with tempfile.TemporaryDirectory() as temp:
        log = Path(temp, "log")
        tmux("start-server", ";", "set-option", "-g", "status", "off", ";",
             "new-session", "-d", "-s", "v", "-x", str(WIDTH), "-y", str(HEIGHT),
             f"LANG=C.UTF-8 script -qfc vttest {log}; sleep 60")
        time.sleep(1)
        offsets = [log.stat().st_size]
        for key in keys:
            tmux("send-keys", "-t", "v", "-l", key)
            tmux("send-keys", "-t", "v", "Enter")
            time.sleep(0.3)
            offsets.append(log.stat().st_size)
        data = log.read_bytes()
    subprocess.run(["tmux", "-L", SOCKET, "kill-server"], capture_output=True)
    session = save(name, data, [o for i, o in enumerate(offsets) if i not in skip])
    (session / "skipped.txt").write_text("".join(f"{offsets[i]}\n" for i in sorted(skip)))
    print(f"{session}: {len(data)} bytes, {len(offsets)} checkpoints")


def drain(fd, output, seconds):
    end = time.monotonic() + seconds
    while (left := end - time.monotonic()) > 0:
        ready, _, _ = select.select([fd], [], [], left)
        if ready:
            try:
                output += os.read(fd, 65536)
            except OSError:
                return


def save(name, data, offsets, width=WIDTH, height=HEIGHT):
    session = ROOT / name
    session.mkdir(parents=True, exist_ok=True)
    for old in session.glob("*.screen"):
        old.unlink()
    (session / "session.bin").write_bytes(data)
    (session / "session.txt").write_text(f"{width} {height}\n" + "".join(f"{o}\n" for o in offsets))
    return session


def fuzz(seed, count=4000, width=40, height=12, every=50):
    """Random text and control sequences that xterm-like terminals agree on. Where tmux knowingly differs
    from xterm, which is what TERM promises, the generator avoids the case and a unit test pins xterm's."""
    r = random.Random(seed)
    region = [1, height]
    alternate = [False]
    saved = [False]
    origin = [False]
    saved_origin = [False]

    # xterm goes to the region's first line when in origin mode. tmux always goes to the screen's.
    def set_region():
        origin[0] = False
        top, bottom = r.randint(1, height), r.randint(1, height)
        if top < bottom:
            region[:] = [top, bottom]
        return f"\x1b[?6l\x1b[{top};{bottom}r"

    def full_region():
        region[:] = [1, height]
        return "\x1b[r"

    # tmux's wrap scrolls in a default background, where xterm's uses the current one. So backgrounds
    # are only set around something that can't wrap, which also covers erasing and scrolling with one.
    def with_background():
        color = r.choice(["42", "104", "48;5;33", "48;2;1;2;3"])
        action = r.choice([f"\x1b[{r.randint(1, 3)}Xabc", f"\x1b[{r.randint(0, 2)}K", f"\x1b[{r.randint(0, 2)}J", "\n",
                           f"\x1b[{r.randint(1, 3)}S", f"\x1b[{r.randint(1, 3)}T"])
        return f"\x1b[{r.randint(1, width - 4)}G\x1b[{color}m{action}\x1b[49m"

    # While a wrap is pending, tmux counts from one column past the edge and doesn't clear the wrap on a
    # line feed. xterm counts from the last column and clears it.
    def at_column(sequence):
        return f"\x1b[{r.randint(1, width)}G{sequence}"

    # tmux leaves old text in the gap when the gap is wider than what moves right.
    def insert_cells():
        if not seed % 2:
            return ""
        count = r.randint(1, width // 2)
        return f"\x1b[{r.randint(1, width - 2 * count + 1)}G\x1b[{count}@"

    # Shifting half of a wide character leaves tmux's capture a column out, so a seed has one or the other.
    def delete_cells():
        return at_column(f"\x1b[{n()}P") if seed % 2 else ""

    def origin_on():
        origin[0] = True
        return "\x1b[?6h"

    def origin_off():
        origin[0] = False
        return "\x1b[?6l"

    # tmux shares one saved cursor between the screens, where xterm has one each, and its 1049 saves
    # elsewhere, where xterm's uses the same one. So only restore what the same screen just saved.
    def switch_screen():
        alternate[0] = not alternate[0]
        saved[0] = False
        pending = f"\x1b[?6l\x1b[{r.randint(1, width)}G"
        # xterm saves and restores origin mode with the cursor. tmux doesn't.
        origin[0] = saved_origin[0] = False
        return pending + ("\x1b[?1049h" if alternate[0] else "\x1b[?1049l")

    def save_cursor():
        saved[0] = True
        saved_origin[0] = origin[0]
        return f"\x1b[{r.randint(1, width)}G\x1b7"

    def restore_cursor():
        return "\x1b8" if saved[0] and saved_origin[0] == origin[0] else ""

    # tmux has no CSI I, and repeats a character only straight after it and only as far as the edge, so
    # the generator uses CSI b only that way.
    # xterm ignores these outside the region and goes to column 0 afterwards. tmux doesn't, in either case.
    def in_region(final):
        origin[0] = False
        return f"\x1b[?6l\x1b[{r.randint(*region)};{n()}H\x1b[{n()}{final}\x1b[H"

    n = lambda top=None: str(r.randint(0, top or max(width, height) + 3))
    text = ["a", "bc", "hello", "\u00e9", "\u2500", "\u2502", " ", "xyz", "0123456789"] + ([] if seed % 2 else ["\u65e5", "\u672c\u8a9e"] * 2)
    sgr = ["0", "1", "2", "3", "4", "7", "9", "22", "23", "24", "27", "29", "31", "39", "95", "38;5;" + n(255), "38;2;9;8;7"]
    makers = [
        lambda: r.choice(text), lambda: r.choice(text), lambda: r.choice(text), lambda: r.choice(text),
        lambda: "\r", lambda: at_column("\n"), lambda: at_column("\b"), lambda: at_column("\t"),
        lambda: f"\x1b[{n()}A", lambda: f"\x1b[{n()}B", lambda: f"\x1b[{n()}C", lambda: at_column(f"\x1b[{n()}D"),
        lambda: f"\x1b[{n()}E", lambda: f"\x1b[{n()}F", lambda: f"\x1b[{n()}G", lambda: f"\x1b[{n()};{n()}H",
        lambda: f"\x1b[{n()}d", lambda: at_column(f"\x1b[{n()}X"), insert_cells, delete_cells,
        lambda: in_region("L"), lambda: in_region("M"), lambda: f"\x1b[{n()}S", lambda: f"\x1b[{n()}T",
        lambda: at_column(f"\x1b[{r.randint(0, 2)}J"), lambda: at_column(f"\x1b[{r.randint(0, 2)}K"),
        set_region, full_region,
        lambda: f"\x1b[{r.choice(sgr)}m", lambda: f"\x1b[{r.choice(sgr)}m", with_background,
        save_cursor, restore_cursor, lambda: at_column("\x1bD"), lambda: "\x1bE", lambda: at_column("\x1bM"),
        lambda: "\x1bH", lambda: f"\x1b[{r.choice([0, 3])}g", lambda: at_column(f"\x1b[{n()}Z"),
        lambda: "\x1b(0lqkxmj\x1b(B", lambda: f"\x1b[{r.randint(1, width - 8)}Ga\x1b[{n(5)}b",
        origin_on, origin_off,
        switch_screen, lambda: "\x1b[?25l", lambda: "\x1b[?25h",
    ]
    chunks, offsets, size = [], [], 0
    for i in range(count):
        chunk = r.choice(makers)().encode()
        chunks.append(chunk)
        size += len(chunk)
        if i % every == every - 1:
            offsets.append(size)
    session = save(f"fuzz-{seed}", b"".join(chunks), offsets, width, height)
    print(f"{session}: {size} bytes, {len(offsets)} checkpoints")


def tmux(*args, **kwargs):
    return subprocess.run(["tmux", "-L", SOCKET, "-f", "/dev/null", *args], check=True, capture_output=True, **kwargs).stdout


def start_session(width, height, program):
    # Killing the last session stops the server, and starting a new one can lose that race.
    for attempt in range(5):
        try:
            tmux("start-server", ";", "set-option", "-g", "status", "off", ";",
                 "new-session", "-d", "-s", "g", "-x", str(width), "-y", str(height), program)
            return
        except subprocess.CalledProcessError:
            if attempt == 4:
                raise
            time.sleep(0.2)


def golden(session):
    lines = (session / "session.txt").read_text().split("\n")
    width, height = map(int, lines[0].split())
    offsets = [int(line) for line in lines[1:] if line]
    data = (session / "session.bin").read_bytes()
    for old in session.glob("*.screen"):
        old.unlink()
    with tempfile.TemporaryDirectory() as temp:
        prefix = Path(temp, "prefix")
        for index, offset in enumerate(offsets):
            prefix.write_bytes(data[:offset])
            channel = f"done-{os.getpid()}-{index}"
            program = f"stty raw -echo; cat {prefix}; tmux -L {SOCKET} wait-for -S {channel}; sleep 60"
            start_session(width, height, program)
            tmux("wait-for", channel)
            cursor = tmux("display-message", "-p", "-t", "g", "#{cursor_x} #{cursor_y} #{cursor_flag}")
            screen = tmux("capture-pane", "-p", "-e", "-N", "-t", "g")
            (session / f"{index:02}.screen").write_bytes(cursor + screen)
            tmux("kill-session", "-t", "g")
    print(f"{session}: {len(offsets)} screens")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("command", choices=["record", "fuzz", "golden"])
    parser.add_argument("names", nargs="*")
    parser.add_argument("--seed", type=int, action="append", default=[])
    parser.add_argument("--count", type=int, default=4000)
    parser.add_argument("--every", type=int, default=50)
    args = parser.parse_args()
    if args.command == "record":
        for name in args.names:
            if name in VTTEST:
                record_vttest(name, *VTTEST[name])
            else:
                record(name)
    elif args.command == "fuzz":
        for seed in args.seed:
            fuzz(seed, args.count, every=args.every)
    else:
        try:
            for session in sorted(p for p in ROOT.iterdir() if (p / "session.bin").exists()):
                if not args.names or session.name in args.names:
                    golden(session)
        finally:
            subprocess.run(["tmux", "-L", SOCKET, "kill-server"], capture_output=True)


if __name__ == "__main__":
    main()

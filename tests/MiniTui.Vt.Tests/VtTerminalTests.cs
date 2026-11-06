using System.Diagnostics;
using System.Text;

namespace MiniTui.Vt.Tests;

public class VtTerminalTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static VtTerminal Bash(string script, int width = 80, int height = 24) =>
        VtTerminal.Start(new VtStartInfo("bash") { Args = ["-c", script], Width = width, Height = height });

    private static string Text(VtTerminal terminal)
    {
        lock (terminal.Lock)
        {
            var lines = new List<string>();
            for (var y = 0; y < terminal.Screen.Height; y++)
            {
                var line = new StringBuilder();
                for (var x = 0; x < terminal.Screen.Width; x++) line.Append(terminal.Screen[x, y].Symbol);
                lines.Add(line.ToString().TrimEnd());
            }
            return string.Join("\n", lines).TrimEnd();
        }
    }

    private static void WaitFor(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > Timeout) throw new TimeoutException("Timed out waiting for " + what);
            Thread.Sleep(5);
        }
    }

    private static void WaitForText(VtTerminal terminal, string text) =>
        WaitFor(() => Text(terminal).Contains(text, StringComparison.Ordinal), $"'{text}' on screen, which shows:\n{Text(terminal)}");

    private static ConsoleKeyInfo Key(ConsoleKey key, char c, ConsoleModifiers mods = 0) =>
        new(c, key, (mods & ConsoleModifiers.Shift) != 0, (mods & ConsoleModifiers.Alt) != 0, (mods & ConsoleModifiers.Control) != 0);

    [Fact]
    public void Output_reaches_the_screen_and_changed_is_raised()
    {
        var changed = 0;
        using var terminal = Bash("read; printf hello; sleep 30");
        terminal.Changed += () => Interlocked.Increment(ref changed);
        terminal.HandleKey(Key(ConsoleKey.Enter, '\r'));
        WaitForText(terminal, "hello");
        WaitFor(() => Volatile.Read(ref changed) > 0, "a change");
        terminal.Kill();
    }

    [Fact]
    public void Changed_is_raised_after_the_last_change_of_a_burst()
    {
        // Whatever the timing, the last thing a handler sees has to be the finished screen, or it would stay stale.
        string seen = "";
        using var terminal = Bash("read; for i in 1 2 3 4 5 6 7 8 9 10; do printf x; done; printf END; sleep 30");
        terminal.Changed += () => Volatile.Write(ref seen, Text(terminal));
        terminal.HandleKey(Key(ConsoleKey.Enter, '\r'));
        WaitFor(() => Volatile.Read(ref seen).Contains("xxxxxxxxxxEND", StringComparison.Ordinal), "the finished screen");
        terminal.Kill();
    }

    [Fact]
    public void A_flood_of_output_raises_changed_a_bounded_number_of_times()
    {
        var changed = 0;
        using var terminal = Bash("yes");
        terminal.Changed += () => Interlocked.Increment(ref changed);
        Thread.Sleep(500);
        terminal.Kill();
        Assert.InRange(Volatile.Read(ref changed), 1, 120);
    }

    [Fact]
    public void Keys_reach_the_program()
    {
        using var terminal = Bash("stty raw -echo; head -c 7 | od -An -tx1; sleep 5");
        Thread.Sleep(100);
        terminal.HandleKey(Key(ConsoleKey.A, 'a'));
        terminal.HandleKey(Key(ConsoleKey.UpArrow, '\0'));
        terminal.HandleKey(Key(ConsoleKey.Spacebar, '\0', ConsoleModifiers.Control));
        terminal.HandleKey(Key(ConsoleKey.Z, 'z'));
        terminal.HandleKey(Key(ConsoleKey.B, 'b'));
        WaitForText(terminal, "61 1b 5b 41 00 7a 62");
        terminal.Kill();
    }

    [Fact]
    public void A_character_above_the_bmp_is_joined_from_its_two_keys()
    {
        using var terminal = Bash("stty raw -echo; head -c 4 | od -An -tx1; sleep 5");
        Thread.Sleep(100);
        var emoji = "🎉";
        Assert.True(terminal.HandleKey(new ConsoleKeyInfo(emoji[0], 0, false, false, false)));
        Assert.True(terminal.HandleKey(new ConsoleKeyInfo(emoji[1], 0, false, false, false)));
        WaitForText(terminal, "f0 9f 8e 89");
        terminal.Kill();
    }

    [Fact]
    public void A_lone_low_surrogate_is_refused()
    {
        using var terminal = Bash("sleep 5");
        Assert.False(terminal.HandleKey(new ConsoleKeyInfo('\uDF89', 0, false, false, false)));
        terminal.Kill();
    }

    [Fact]
    public void Keys_follow_the_modes_the_program_set()
    {
        using var terminal = Bash(@"stty raw -echo; printf '\e[?1h\e[>1u\e[?2004h'; printf ready; head -c 22 | od -An -tx1 -w64; sleep 5");
        WaitForText(terminal, "ready");
        terminal.HandleKey(Key(ConsoleKey.UpArrow, '\0'));
        terminal.HandleKey(Key(ConsoleKey.Enter, '\r', ConsoleModifiers.Shift));
        terminal.HandlePaste("x");
        WaitForText(terminal, "1b 4f 41 1b 5b 31 33 3b 32 75 1b 5b 32 30 30 7e 78 1b 5b 32 30 31");
        terminal.Kill();
    }

    [Fact]
    public void Paste_reaches_the_program_with_its_line_breaks()
    {
        using var terminal = Bash("stty raw -echo; head -c 3 | od -An -tx1; sleep 5");
        Thread.Sleep(100);
        terminal.HandlePaste("a\nb");
        WaitForText(terminal, "61 0d 62");
        terminal.Kill();
    }

    [Fact]
    public void Mouse_reaches_a_program_that_asked()
    {
        using var terminal = Bash(@"stty raw -echo; printf '\e[?1000h\e[?1006h'; printf ready; head -c 9 | od -An -tx1 -w64; sleep 5");
        WaitForText(terminal, "ready");
        Assert.True(terminal.HandleMouse(new MouseEvent(MouseAction.Press, MouseButton.Left, 2, 3)));
        WaitForText(terminal, "1b 5b 3c 30 3b 33 3b 34 4d");
        terminal.Kill();
    }

    [Fact]
    public void Mouse_is_refused_when_the_program_did_not_ask()
    {
        using var terminal = Bash("sleep 5");
        Assert.False(terminal.HandleMouse(new MouseEvent(MouseAction.Press, MouseButton.Left, 0, 0)));
        terminal.Kill();
    }

    [Fact]
    public void Focus_changes_reach_a_program_that_asked()
    {
        using var terminal = Bash(@"stty raw -echo; printf '\e[?1004h'; printf ready; head -c 6 | od -An -tx1; sleep 5");
        WaitForText(terminal, "ready");
        terminal.HandleFocus(true);
        terminal.HandleFocus(false);
        WaitForText(terminal, "1b 5b 49 1b 5b 4f");
        terminal.Kill();
    }

    [Fact]
    public void A_real_program_gets_replies_to_its_queries()
    {
        using var terminal = Bash(@"stty raw -echo; printf '\e[3;5H\e[6n'; IFS= read -rsd R pos; printf '\e[H'; printf 'got%s' ""${pos#*[}""; sleep 5");
        WaitForText(terminal, "got3;5");
        terminal.Kill();
    }

    [Fact]
    public async Task A_program_that_never_reads_does_not_block_the_terminal()
    {
        // It writes queries for ever and never reads a reply. The kernel's buffer for its input fills up, and its
        // output must keep flowing all the same. If that goes wrong it's a deadlock, so this has to be able to give up.
        using var terminal = Bash(@"stty raw -echo; n=0; while :; do printf '\e[c\e[6n\e[5n\e[H%d ' $((n++)); done");
        var body = Task.Run(() =>
        {
            WaitFor(() => Text(terminal).Length > 0, "output");

            var pressed = Stopwatch.StartNew();
            for (var i = 0; i < 1000; i++) terminal.HandleKey(Key(ConsoleKey.A, 'a'));
            Assert.True(pressed.Elapsed < TimeSpan.FromSeconds(2), $"keys took {pressed.Elapsed}");

            // Long enough to fill the buffer many times over.
            Thread.Sleep(1000);
            var first = Text(terminal);
            WaitFor(() => Text(terminal) != first, "more output");
        });
        await body.WaitAsync(Timeout); // A deadlock ends here, as a TimeoutException.
        terminal.Kill();
        WaitFor(() => terminal.HasExited, "exit");
    }

    [Fact]
    public void Replies_to_a_program_that_never_reads_are_limited()
    {
        using var terminal = Bash(@"stty raw -echo; yes $'\e[c'");
        WaitFor(() => terminal.Backlog >= VtTerminal.MaxReplyBacklog / 2, "the replies to pile up");
        WaitFor(() => terminal.Backlog + 16 > VtTerminal.MaxReplyBacklog, "the limit");
        Thread.Sleep(300);
        Assert.InRange(terminal.Backlog, 0, VtTerminal.MaxReplyBacklog);
        terminal.Kill();
    }

    [Fact]
    public void A_flood_can_be_interrupted()
    {
        using var terminal = VtTerminal.Start(new VtStartInfo("yes"));
        WaitForText(terminal, "y");
        var watch = Stopwatch.StartNew();
        terminal.HandleKey(Key(ConsoleKey.C, '\x03', ConsoleModifiers.Control));
        WaitFor(() => terminal.HasExited, "exit");
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(1500), $"took {watch.Elapsed}");
        Assert.Equal(new PtyExit(null, 2), terminal.Exit);
    }

    [Fact]
    public void Exit_is_reported_after_the_last_output()
    {
        using var terminal = Bash("printf last-words; exit 3");
        var exited = new ManualResetEventSlim();
        var textAtExit = "";
        terminal.Exited += () =>
        {
            textAtExit = Text(terminal);
            exited.Set();
        };
        Assert.True(exited.Wait(Timeout));
        Assert.Contains("last-words", textAtExit, StringComparison.Ordinal);
        Assert.True(terminal.HasExited);
        Assert.Equal(new PtyExit(3, null), terminal.Exit);
    }

    [Fact]
    public void An_exit_is_reported_to_a_handler_added_later()
    {
        using var terminal = Bash("exit 0");
        WaitFor(() => terminal.HasExited, "exit");
        var told = false;
        terminal.Exited += () => told = true;
        Assert.True(told);
    }

    [Fact]
    public void An_exit_is_reported_even_if_a_background_process_keeps_the_terminal_open()
    {
        using var terminal = Bash("sleep 30 & exit 4");
        WaitFor(() => terminal.HasExited, "exit");
        Assert.Equal(new PtyExit(4, null), terminal.Exit);
    }

    [Fact]
    public void Input_after_the_end_is_refused()
    {
        using var terminal = Bash("exit 0");
        WaitFor(() => terminal.HasExited, "exit");
        Assert.False(terminal.HandleKey(Key(ConsoleKey.A, 'a')));
        terminal.HandlePaste("x");
        terminal.HandleFocus(true);
    }

    [Fact]
    public void A_killed_program_says_so()
    {
        using var terminal = Bash("sleep 30");
        terminal.Kill();
        WaitFor(() => terminal.HasExited, "exit");
        Assert.Equal(new PtyExit(null, 9), terminal.Exit);
    }

    [Fact]
    public void Hangup_ends_a_program()
    {
        using var terminal = Bash("sleep 30");
        terminal.Hangup();
        WaitFor(() => terminal.HasExited, "exit");
        Assert.Equal(new PtyExit(null, 1), terminal.Exit);
    }

    [Fact]
    public void A_missing_program_throws_from_start() =>
        Assert.ThrowsAny<Exception>(() => VtTerminal.Start(new VtStartInfo("/no/such/program")));

    [Fact]
    public void Resize_reaches_the_screen_and_the_program()
    {
        using var terminal = Bash("trap 'stty size' WINCH; printf ready; while :; do sleep 0.01; done", 80, 24);
        WaitForText(terminal, "ready");
        terminal.Resize(50, 10);
        lock (terminal.Lock) Assert.Equal((50, 10), (terminal.Screen.Width, terminal.Screen.Height));
        WaitForText(terminal, "10 50");
        terminal.Kill();
    }

    [Fact]
    public void Bell_clipboard_and_notifications_become_events()
    {
        var bells = 0;
        var clipboard = new List<string>();
        var notes = new List<(string?, string)>();
        using var terminal = Bash(@"read; printf '\a\e]52;c;aGk=\a\e]777;notify;T;B\a'; printf done; sleep 5");
        terminal.Bell += () => Interlocked.Increment(ref bells);
        terminal.ClipboardSet += clipboard.Add;
        terminal.Notified += (t, b) => notes.Add((t, b));
        Thread.Sleep(50);
        terminal.HandleKey(Key(ConsoleKey.Enter, '\r'));
        WaitFor(() => notes.Count == 1, "the notification");
        Assert.Equal(1, bells);
        Assert.Equal(["hi"], clipboard);
        Assert.Equal(("T", "B"), notes[0]);
        terminal.Kill();
    }

    [Fact]
    public void Events_are_raised_without_the_lock_held()
    {
        var clear = false;
        using var terminal = Bash(@"read; printf '\a'; sleep 5");
        var told = new ManualResetEventSlim();
        terminal.Bell += () =>
        {
            clear = !terminal.Lock.IsHeldByCurrentThread;
            told.Set();
        };
        Thread.Sleep(50);
        terminal.HandleKey(Key(ConsoleKey.Enter, '\r'));
        Assert.True(told.Wait(Timeout));
        Assert.True(clear);
        terminal.Kill();
    }

    [Fact]
    public void Dispose_ends_the_threads_and_can_be_repeated()
    {
        var terminal = Bash("sleep 30");
        terminal.Dispose();
        terminal.Dispose();

        // Everything still works, in that nothing throws, and a draw shows the last screen.
        terminal.HandleKey(Key(ConsoleKey.A, 'a'));
        terminal.HandlePaste("x");
        terminal.Resize(10, 3);
        Draw(terminal, 10, 3);
        terminal.Kill();
    }

    // ---- The view ----

    private static string[] Draw(VtTerminal terminal, int width, int height, bool focused = true, BlockBuilder? block = null)
    {
        var buffer = new CellBuffer(width, height);
        var builder = new TerminalViewBuilder().Terminal(terminal).Focused(focused);
        if (block is not null) builder.Block(block);
        builder.Draw(buffer, new Rect(0, 0, width, height));
        LastCursor = buffer.CursorRequest;
        return buffer.ToLines();
    }

    private static (int X, int Y)? LastCursor { get; set; }

    [Fact]
    public void The_view_resizes_copies_and_places_the_cursor()
    {
        using var terminal = Bash("printf abc; sleep 5", 80, 24);
        WaitForText(terminal, "abc");
        var lines = Draw(terminal, 20, 5);
        Assert.Equal("abc", lines[0].TrimEnd());
        Assert.Equal(5, lines.Length);
        lock (terminal.Lock) Assert.Equal((20, 5), (terminal.Screen.Width, terminal.Screen.Height));
        terminal.Kill();
    }

    [Fact]
    public void The_view_has_a_cursor_only_when_focused()
    {
        using var terminal = Bash("printf abc; sleep 5", 20, 5);
        WaitForText(terminal, "abc");
        Draw(terminal, 20, 5);
        Assert.Equal((3, 0), LastCursor);
        Draw(terminal, 20, 5, focused: false);
        Assert.Null(LastCursor);
        terminal.Kill();
    }

    [Fact]
    public void The_view_has_no_cursor_when_the_program_hid_it()
    {
        using var terminal = Bash(@"printf '\e[?25labc'; sleep 5", 20, 5);
        WaitForText(terminal, "abc");
        Draw(terminal, 20, 5);
        Assert.Null(LastCursor);
        terminal.Kill();
    }

    [Fact]
    public void The_view_draws_inside_a_block()
    {
        using var terminal = Bash("printf abc; sleep 5", 20, 5);
        WaitForText(terminal, "abc");
        var lines = Draw(terminal, 20, 5, block: new BlockBuilder().Padding(new Margin(0)));
        Assert.Contains("abc", lines[1], StringComparison.Ordinal);
        Assert.Equal((4, 1), LastCursor);
        lock (terminal.Lock) Assert.Equal((18, 3), (terminal.Screen.Width, terminal.Screen.Height));
        terminal.Kill();
    }

    [Fact]
    public void The_view_survives_no_room()
    {
        using var terminal = Bash("sleep 5", 20, 5);
        var buffer = new CellBuffer(3, 3);
        new TerminalViewBuilder().Terminal(terminal).Draw(buffer, new Rect(0, 0, 0, 0));
        new TerminalViewBuilder().Draw(buffer, new Rect(0, 0, 3, 3));
        lock (terminal.Lock) Assert.Equal(20, terminal.Screen.Width);
        terminal.Kill();
    }

    [Fact]
    public void The_view_shows_the_last_complete_frame_until_the_next_is_finished()
    {
        using var terminal = Bash(@"printf old; read; printf '\e[?2026h\e[Hnew'; read; printf '\e[?2026l'; sleep 5", 20, 5);
        WaitForText(terminal, "old");
        terminal.HandleKey(Key(ConsoleKey.Enter, '\r'));
        WaitForText(terminal, "new");
        Assert.Equal("old", Draw(terminal, 20, 5)[0].TrimEnd());
        terminal.HandleKey(Key(ConsoleKey.Enter, '\r'));
        WaitFor(() => { lock (terminal.Lock) return !terminal.Screen.Modes.SynchronizedOutput; }, "the end of the frame");
        Assert.Equal("new", Draw(terminal, 20, 5)[0].TrimEnd());
        terminal.Kill();
    }

    [Fact]
    public void A_frame_that_never_finishes_is_shown_after_the_limit_and_says_so()
    {
        var changed = 0;
        // The program stays alive and quiet, so that only the limit passing can raise Changed.
        using var terminal = Bash(@"printf old; read; printf '\e[?2026h\e[Hnew'; sleep 60", 20, 5);
        WaitForText(terminal, "old");
        terminal.HandleKey(Key(ConsoleKey.Enter, '\r'));
        WaitForText(terminal, "new");
        Assert.Equal("old", Draw(terminal, 20, 5)[0].TrimEnd());

        Thread.Sleep(50);
        terminal.Changed += () => Interlocked.Increment(ref changed);
        WaitFor(() => Volatile.Read(ref changed) > 0, "a change when the limit passed");
        WaitFor(() => Draw(terminal, 20, 5)[0].TrimEnd() == "new", "the frame to show");
        terminal.Kill();
    }
}

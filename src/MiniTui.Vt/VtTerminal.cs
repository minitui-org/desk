using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;

namespace MiniTui.Vt;

/// <summary>
/// A program in a pseudo-terminal, with a <see cref="VtScreen"/> kept up to date from a thread of its own, and a
/// queue for what's sent to it, so that neither the UI nor the screen ever waits for the program to read its input.
/// </summary>
/// <remarks>
/// Events are raised on background threads and must not throw. Use <c>App.Post</c> or <c>App.RequestRedraw</c> to
/// get back to the UI thread. None of them are raised with <see cref="Lock"/> held.
/// </remarks>
public sealed class VtTerminal : IDisposable
{
    /// <summary>How long a frame is held while a program draws the next (mode 2026, spec §11.2).</summary>
    internal const int MaxHoldMilliseconds = 200;

    // At most about 120 Changed events a second, which is well above what a terminal can show.
    private static readonly TimeSpan ChangedInterval = TimeSpan.FromMilliseconds(8);

    // How long an exit waits for the end of the program's output. A background process it left behind can keep
    // the terminal open for as long as it likes, so this can't wait for the end of the output for ever.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromMilliseconds(250);

    // A program that asks questions without reading the answers can't make the queue grow without limit.
    internal const int MaxReplyBacklog = 1024 * 1024;

    private readonly PtyProcess _process;
    private readonly BlockingCollection<byte[]> _writes = [];
    private readonly TaskCompletionSource _outputEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _changedLock = new();
    private readonly List<Action> _pendingEvents = [];
    private readonly Timer _changedTimer;
    private readonly Timer _holdTimer;
    private long _backlog;
    private bool _changedWaiting;
    private bool _changedAgain;
    private Action? _exited;
    private PtyExit? _exit;
    private bool _exitPublished;
    private char _highSurrogate;
    private int _disposed;

    private VtTerminal(PtyProcess process, int width, int height)
    {
        _process = process;
        Screen = new VtScreen(width, height) { Host = new ScreenHost(this) };
        _changedTimer = new Timer(_ => ChangedTimerFired());
        _holdTimer = new Timer(_ => Changed?.Invoke());

        new Thread(ReadOutput) { IsBackground = true, Name = "VtTerminal read" }.Start();
        new Thread(WriteInput) { IsBackground = true, Name = "VtTerminal write" }.Start();
        _ = WatchExit();
    }

    /// <summary>The screen. Read it while holding <see cref="Lock"/>.</summary>
    public VtScreen Screen { get; }

    /// <summary>Hold this while reading <see cref="Screen"/>. The reader thread holds it for each block of output.</summary>
    public Lock Lock { get; } = new();

    /// <summary>The process id.</summary>
    public int Pid => _process.Pid;

    /// <summary>True once the program has ended and its output has reached the screen.</summary>
    public bool HasExited => Volatile.Read(ref _exitPublished);

    /// <summary>How the program ended, once <see cref="HasExited"/>.</summary>
    public PtyExit? Exit => HasExited ? _exit : null;

    /// <summary>The screen changed. Coalesced, so this says that there's something to draw and not how much.</summary>
    public event Action? Changed;

    /// <summary>The program ended. A handler added after that runs at once, so an exit can't be missed.</summary>
    public event Action? Exited
    {
        add
        {
            var run = false;
            lock (_changedLock)
            {
                if (HasExited) run = true;
                else _exited += value;
            }
            if (run) value?.Invoke();
        }
        remove
        {
            lock (_changedLock) _exited -= value;
        }
    }

    /// <summary>The program rang the bell.</summary>
    public event Action? Bell;

    /// <summary>The program asked to put text on the clipboard. To allow it, call <c>Clipboard.SetText</c> on the UI thread.</summary>
    public event Action<string>? ClipboardSet;

    /// <summary>The program sent a notification: the title, which can be null, and the text.</summary>
    public event Action<string?, string>? Notified;

    /// <summary>Start a program in a new terminal.</summary>
    /// <exception cref="Win32Exception">The terminal couldn't be created, or the program couldn't be started.</exception>
    /// <exception cref="PlatformNotSupportedException">Not running on Linux.</exception>
    public static VtTerminal Start(VtStartInfo info)
    {
        var process = PtyProcess.Start(info);
        return new VtTerminal(process, info.Width, info.Height);
    }

    /// <summary>Send a key to the program, encoded for the modes it has set.</summary>
    /// <returns>False if it has no encoding, or the program has ended.</returns>
    public bool HandleKey(ConsoleKeyInfo key)
    {
        if (_process.HasExited) return false;
        var modes = CurrentModes();
        var output = new ArrayBufferWriter<byte>(16);

        // MiniTui reports a character above U+FFFF as two keys.
        if (char.IsHighSurrogate(key.KeyChar))
        {
            _highSurrogate = key.KeyChar;
            return true;
        }
        if (char.IsLowSurrogate(key.KeyChar))
        {
            var high = _highSurrogate;
            _highSurrogate = '\0';
            if (high == '\0') return false;
            VtInput.EncodeRune(new Rune(high, key.KeyChar), key.Modifiers, modes, output);
        }
        else if (!VtInput.TryEncode(key, modes, output))
        {
            return false;
        }

        Send(output.WrittenSpan);
        return true;
    }

    /// <summary>Send a mouse event to the program. Positions are relative to its screen.</summary>
    /// <returns>False if the program didn't ask for this event, or has ended.</returns>
    public bool HandleMouse(MouseEvent mouse)
    {
        if (_process.HasExited) return false;
        var output = new ArrayBufferWriter<byte>(32);
        if (!VtInput.TryEncode(mouse, CurrentModes(), output)) return false;
        Send(output.WrittenSpan);
        return true;
    }

    /// <summary>Send a paste to the program, as <see cref="VtInput.EncodePaste"/> describes.</summary>
    public void HandlePaste(string text)
    {
        if (_process.HasExited) return;
        var output = new ArrayBufferWriter<byte>(text?.Length + 16 ?? 16);
        VtInput.EncodePaste(text!, CurrentModes(), output);
        Send(output.WrittenSpan);
    }

    /// <summary>Tell the program it gained or lost focus, if it asked to know (mode 1004).</summary>
    public void HandleFocus(bool focused)
    {
        if (_process.HasExited) return;
        var output = new ArrayBufferWriter<byte>(8);
        if (VtInput.TryEncodeFocus(focused, CurrentModes(), output)) Send(output.WrittenSpan);
    }

    /// <summary>Change the terminal's size. The program is told, and redraws. Does nothing if the size is the same.</summary>
    public void Resize(int width, int height)
    {
        lock (Lock)
        {
            if (width == Screen.Width && height == Screen.Height) return;
            Screen.Resize(width, height);
        }
        _process.Resize(width, height);
    }

    /// <summary>Send <c>SIGHUP</c>, the polite way to end a terminal program. Does nothing once it has ended.</summary>
    public void Hangup() => _process.Hangup();

    /// <summary>Send <c>SIGKILL</c>. Does nothing once it has ended.</summary>
    public void Kill() => _process.Kill();

    /// <summary>
    /// Close the terminal, which hangs up the program's foreground job, and stop raising events. A program that
    /// ignores the hangup keeps running, so call <see cref="Kill"/> first if it must end.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _writes.CompleteAdding();
        _process.Dispose();
        _changedTimer.Dispose();
        _holdTimer.Dispose();
    }

    /// <summary>Bytes waiting to reach the program.</summary>
    internal long Backlog => Interlocked.Read(ref _backlog);

    /// <summary>Draw the screen, or while a program is partway through a frame, the last complete one.</summary>
    internal void DrawTo(CellBuffer buffer, int left, int top, bool showCursor)
    {
        lock (Lock)
        {
            var held = Environment.TickCount64 - Screen.HoldStarted < MaxHoldMilliseconds;
            var cursor = Screen.CopyTo(buffer, left, top, held);
            if (showCursor && cursor is { } c) buffer.SetCursor(left + c.X, top + c.Y);
        }
    }

    private VtModes CurrentModes()
    {
        lock (Lock) return Screen.Modes;
    }

    private void Send(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;
        Interlocked.Add(ref _backlog, bytes.Length);
        try
        {
            _writes.Add(bytes.ToArray());
        }
        catch (InvalidOperationException)
        {
            // Disposed: the input has nowhere to go.
        }
    }

    private void ReadOutput()
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var count = _process.Read(buffer);
                if (count == 0) break;
                long holdLeft = -1;
                lock (Lock)
                {
                    Screen.Feed(buffer.AsSpan(0, count));
                    if (Screen.IsHolding) holdLeft = MaxHoldMilliseconds - (Environment.TickCount64 - Screen.HoldStarted);
                }
                RaisePending();
                if (holdLeft > 0) ArmHoldTimer(holdLeft);
                RaiseChanged();
            }
        }
        catch (Win32Exception)
        {
            // Reading can only fail if the terminal is gone, which is the end of the output too.
        }
        _outputEnded.TrySetResult();
    }

    private void WriteInput()
    {
        try
        {
            foreach (var bytes in _writes.GetConsumingEnumerable())
            {
                _process.Write(bytes);
                Interlocked.Add(ref _backlog, -bytes.Length);
            }
        }
        catch (Win32Exception)
        {
            // The terminal is gone, so the input has nowhere to go.
        }
    }

    private async Task WatchExit()
    {
        var exit = await _process.Exit.ConfigureAwait(false);
        await Task.WhenAny(_outputEnded.Task, Task.Delay(DrainTimeout)).ConfigureAwait(false);
        _writes.CompleteAdding();

        Action? exited;
        lock (_changedLock)
        {
            _exit = exit;
            Volatile.Write(ref _exitPublished, true);
            exited = _exited;
            _exited = null;
        }
        if (Volatile.Read(ref _disposed) != 0) return;
        RaiseChanged();
        exited?.Invoke();
    }

    private void RaisePending()
    {
        List<Action>? pending = null;
        lock (_pendingEvents)
        {
            if (_pendingEvents.Count > 0)
            {
                pending = [.. _pendingEvents];
                _pendingEvents.Clear();
            }
        }
        if (pending is null || Volatile.Read(ref _disposed) != 0) return;
        foreach (var action in pending) action();
    }

    private void ArmHoldTimer(long milliseconds)
    {
        try
        {
            _holdTimer.Change(milliseconds + 1, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // Disposed during the read: nothing is drawing any more.
        }
    }

    /// <summary>Raise now if that's allowed, and otherwise from the timer, so the last change is never lost.</summary>
    private void RaiseChanged()
    {
        lock (_changedLock)
        {
            if (_changedWaiting)
            {
                _changedAgain = true;
                return;
            }
            _changedWaiting = true;
        }
        StartChangedTimer();
        Changed?.Invoke();
    }

    private void ChangedTimerFired()
    {
        lock (_changedLock)
        {
            if (!_changedAgain)
            {
                _changedWaiting = false;
                return;
            }
            _changedAgain = false;
        }
        StartChangedTimer();
        Changed?.Invoke();
    }

    private void StartChangedTimer()
    {
        try
        {
            _changedTimer.Change(ChangedInterval, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Disposed during the read: nothing is drawing any more.
        }
    }

    /// <summary>
    /// Runs inside <see cref="VtScreen.Feed"/>, with the lock held. Replies are queued, as writing could wait on a
    /// program that's waiting on us. The rest is raised after the lock is released.
    /// </summary>
    private sealed class ScreenHost(VtTerminal terminal) : IVtHost
    {
        public void Reply(ReadOnlySpan<byte> bytes)
        {
            if (Interlocked.Read(ref terminal._backlog) + bytes.Length <= MaxReplyBacklog) terminal.Send(bytes);
        }

        public void SetClipboard(string text) => Later(() => terminal.ClipboardSet?.Invoke(text));

        public void Bell() => Later(() => terminal.Bell?.Invoke());

        public void Notify(string? title, string body) => Later(() => terminal.Notified?.Invoke(title, body));

        private void Later(Action action)
        {
            lock (terminal._pendingEvents)
            {
                terminal._pendingEvents.Add(action);
            }
        }
    }
}

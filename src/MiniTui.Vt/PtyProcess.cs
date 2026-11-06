using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MiniTui.Vt;

/// <summary>
/// A child process in a pseudo-terminal of its own (spec §11.1). The child is a session leader with the terminal as
/// its controlling terminal, default signal dispositions and an empty signal mask.
/// </summary>
public sealed partial class PtyProcess : IDisposable
{
    private const int EINTR = 4, EAGAIN = 11, EIO = 5;
    private const int O_RDWR = 2, O_NOCTTY = 0x100, O_NONBLOCK = 0x800, O_CLOEXEC = 0x80000;
    private const ulong TIOCSWINSZ = 0x5414;
    private const short POLLIN = 1, POLLOUT = 4;
    private const short POSIX_SPAWN_SETSIGDEF = 4, POSIX_SPAWN_SETSIGMASK = 8, POSIX_SPAWN_SETSID = 0x80;
    private const int P_PID = 1, WEXITED = 4, WNOWAIT = 0x01000000;
    private const int SIGHUP = 1, SIGKILL = 9;

    private readonly SafeFileHandle _master;
    private readonly SafeFileHandle _wakeRead;
    private readonly SafeFileHandle _wakeWrite;
    private readonly TaskCompletionSource<PtyExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _reap = new();
    private bool _reaped;
    private int _disposed;

    private PtyProcess(int pid, SafeFileHandle master, SafeFileHandle wakeRead, SafeFileHandle wakeWrite)
    {
        Pid = pid;
        _master = master;
        _wakeRead = wakeRead;
        _wakeWrite = wakeWrite;

        // waitpid for this child only, so it can't take other children's exits from System.Diagnostics.Process, or
        // they from it
        new Thread(WaitForExit) { IsBackground = true, Name = "PtyProcess wait" }.Start();
    }

    /// <summary>The child's process id.</summary>
    public int Pid { get; }

    /// <summary>Completes with how the child ended. Never faults.</summary>
    public Task<PtyExit> Exit => _exit.Task;

    /// <summary>True once the child has ended.</summary>
    public bool HasExited => _exit.Task.IsCompleted;

    /// <summary>Start a child in a new pseudo-terminal.</summary>
    /// <exception cref="Win32Exception">The terminal couldn't be created, or the program couldn't be started.</exception>
    /// <exception cref="PlatformNotSupportedException">Not running on Linux.</exception>
    public static unsafe PtyProcess Start(VtStartInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("PtyProcess runs on Linux.");
        ArgumentOutOfRangeException.ThrowIfLessThan(info.Width, 1, nameof(info));
        ArgumentOutOfRangeException.ThrowIfLessThan(info.Height, 1, nameof(info));

        var master = new SafeFileHandle(Check(posix_openpt(O_RDWR | O_NOCTTY | O_CLOEXEC | O_NONBLOCK), "posix_openpt"), ownsHandle: true);
        SafeFileHandle? wakeRead = null, wakeWrite = null;
        try
        {
            Check(grantpt(master), "grantpt");
            Check(unlockpt(master), "unlockpt");
            SetSize(master, info.Width, info.Height);

            var name = stackalloc byte[256];
            var error = ptsname_r(master, name, 256);
            if (error != 0) throw new Win32Exception(error, "ptsname_r");

            var pipe = stackalloc int[2];
            Check(pipe2(pipe, O_CLOEXEC | O_NONBLOCK), "pipe2");
            wakeRead = new SafeFileHandle(pipe[0], ownsHandle: true);
            wakeWrite = new SafeFileHandle(pipe[1], ownsHandle: true);

            int pid;
            // Kept open across the spawn so the terminal has a slave from the start: posix_spawn returns after the exec
            using (new SafeFileHandle(Check(open(name, O_RDWR | O_NOCTTY | O_CLOEXEC), "open"), ownsHandle: true))
                pid = Spawn(info, name);

            return new PtyProcess(pid, master, wakeRead, wakeWrite);
        }
        catch
        {
            master.Dispose();
            wakeRead?.Dispose();
            wakeWrite?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Read the child's output, waiting until there is some. Returns 0 at the end: every copy of the terminal is
    /// closed, or this object was disposed. Output written before the child ended is still returned.
    /// </summary>
    public int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return 0;
        using var master = Use(_master);
        using var wake = Use(_wakeRead);
        if (!master.Valid || !wake.Valid) return 0;

        while (true)
        {
            if (!WaitFor(master.Fd, POLLIN, wake.Fd)) return 0;
            var read = read_(master.Fd, buffer);
            if (read > 0) return (int)read;
            if (read == 0) return 0;
            var errno = Marshal.GetLastPInvokeError();
            if (errno is EINTR or EAGAIN) continue;
            if (errno == EIO) return 0; // Linux's way of saying that the last slave closed
            throw new Win32Exception(errno, "read");
        }
    }

    /// <summary>
    /// Write to the child's input, waiting while the terminal's buffer is full. Input is dropped once the child has
    /// ended or this object is disposed.
    /// </summary>
    public void Write(ReadOnlySpan<byte> bytes)
    {
        using var master = Use(_master);
        using var wake = Use(_wakeRead);
        if (!master.Valid || !wake.Valid) return;

        while (!bytes.IsEmpty)
        {
            if (!WaitFor(master.Fd, POLLOUT, wake.Fd)) return;
            var written = write_(master.Fd, bytes);
            if (written > 0)
            {
                bytes = bytes[(int)written..];
                continue;
            }
            var errno = Marshal.GetLastPInvokeError();
            if (errno is EINTR or EAGAIN) continue;
            if (errno == EIO) return;
            throw new Win32Exception(errno, "write");
        }
    }

    /// <summary>Change the terminal's size. The kernel sends <c>SIGWINCH</c> to the foreground job.</summary>
    public void Resize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        using var master = Use(_master);
        if (master.Valid) SetSize(_master, width, height);
    }

    /// <summary>Send <c>SIGHUP</c>, the polite way to end a terminal program. Does nothing once it has ended.</summary>
    public void Hangup() => Signal(SIGHUP);

    /// <summary>Send <c>SIGKILL</c>. Does nothing once it has ended.</summary>
    public void Kill() => Signal(SIGKILL);

    /// <summary>
    /// Close the terminal, which hangs up the child's foreground job, and end any <see cref="Read"/> or
    /// <see cref="Write"/> in progress. A child that ignores the hangup keeps running; <see cref="Exit"/> still
    /// reports when it ends.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        using (var wake = Use(_wakeWrite))
        {
            if (wake.Valid) write_(wake.Fd, [1]);
        }
        _master.Dispose();
        _wakeRead.Dispose();
        _wakeWrite.Dispose();
    }

    private void Signal(int signal)
    {
        // Under the lock, the child can't be reaped and its pid reused between the check and the kill
        lock (_reap)
        {
            if (!_reaped) kill(Pid, signal);
        }
    }

    private unsafe void WaitForExit()
    {
        // Wait without reaping, then reap under the lock (see Signal)
        var info = stackalloc byte[128];
        while (waitid(P_PID, Pid, info, WEXITED | WNOWAIT) != 0 && Marshal.GetLastPInvokeError() == EINTR) { }

        int status;
        int result;
        lock (_reap)
        {
            do result = waitpid(Pid, out status, 0);
            while (result < 0 && Marshal.GetLastPInvokeError() == EINTR);
            _reaped = true;
        }

        // Something else reaped it, such as a runtime set to reap all children: the status is gone
        if (result < 0) _exit.TrySetResult(new PtyExit(null, null));
        else if ((status & 0x7f) == 0) _exit.TrySetResult(new PtyExit((status >> 8) & 0xff, null));
        else _exit.TrySetResult(new PtyExit(null, status & 0x7f));
    }

    private static unsafe int Spawn(VtStartInfo info, byte* slave)
    {
        // Large enough for posix_spawn_file_actions_t, posix_spawnattr_t and sigset_t on any libc
        var actions = stackalloc byte[1024];
        var attributes = stackalloc byte[1024];
        var signals = stackalloc byte[256];

        var error = posix_spawn_file_actions_init(actions);
        if (error != 0) throw new Win32Exception(error, "posix_spawn_file_actions_init");
        error = posix_spawnattr_init(attributes);
        if (error != 0)
        {
            _ = posix_spawn_file_actions_destroy(actions);
            throw new Win32Exception(error, "posix_spawnattr_init");
        }

        var strings = new List<nint>();
        try
        {
            // Opened after setsid, without O_NOCTTY: this is what makes it the controlling terminal
            CheckSpawn(posix_spawn_file_actions_addopen(actions, 0, slave, O_RDWR, 0));
            CheckSpawn(posix_spawn_file_actions_adddup2(actions, 0, 1));
            CheckSpawn(posix_spawn_file_actions_adddup2(actions, 0, 2));
            if (info.WorkingDirectory is { } directory)
            {
                var path = Native(directory, strings);
                CheckSpawn(posix_spawn_file_actions_addchdir_np(actions, (byte*)path));
            }

            _ = sigfillset(signals);
            CheckSpawn(posix_spawnattr_setsigdefault(attributes, signals));
            _ = sigemptyset(signals);
            CheckSpawn(posix_spawnattr_setsigmask(attributes, signals));
            CheckSpawn(posix_spawnattr_setflags(attributes, POSIX_SPAWN_SETSID | POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK));

            var file = Native(info.FileName, strings);
            var args = new List<string>(info.Args.Count + 1) { info.FileName };
            args.AddRange(info.Args);
            var environment = Environment(info);

            fixed (nint* argv = NativeArray(args, strings))
            fixed (nint* envp = NativeArray(environment, strings))
            {
                error = posix_spawnp(out var pid, (byte*)file, actions, attributes, argv, envp);
                if (error != 0) throw new Win32Exception(error, "Couldn't start " + info.FileName);
                return pid;
            }
        }
        finally
        {
            _ = posix_spawnattr_destroy(attributes);
            _ = posix_spawn_file_actions_destroy(actions);
            foreach (var s in strings) Marshal.FreeCoTaskMem(s);
        }
    }

    private static List<string> Environment(VtStartInfo info)
    {
        var variables = new Dictionary<string, string?>();
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
            variables[(string)entry.Key] = (string?)entry.Value;
        variables["TERM"] = "xterm-256color";
        variables["COLORTERM"] = "truecolor";
        foreach (var (key, value) in info.Environment) variables[key] = value;
        return [.. variables.Where(v => v.Value is not null).Select(v => v.Key + "=" + v.Value)];
    }

    private static nint Native(string text, List<nint> strings)
    {
        var pointer = Marshal.StringToCoTaskMemUTF8(text);
        strings.Add(pointer);
        return pointer;
    }

    // The array for argv or envp: pointers, ending in a null
    private static nint[] NativeArray(List<string> items, List<nint> strings)
    {
        var array = new nint[items.Count + 1];
        for (var i = 0; i < items.Count; i++) array[i] = Native(items[i], strings);
        return array;
    }

    private static unsafe void SetSize(SafeFileHandle master, int width, int height)
    {
        var size = new WinSize { Rows = (ushort)Math.Min(height, ushort.MaxValue), Columns = (ushort)Math.Min(width, ushort.MaxValue) };
        Check(ioctl(master, TIOCSWINSZ, &size), "TIOCSWINSZ");
    }

    // True when the fd is ready (or in error, which the read or write reports); false when woken by Dispose
    private static unsafe bool WaitFor(int fd, short events, int wake)
    {
        var fds = stackalloc PollFd[2];
        fds[0] = new PollFd { Fd = fd, Events = events };
        fds[1] = new PollFd { Fd = wake, Events = POLLIN };
        while (true)
        {
            if (poll(fds, 2, -1) < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno == EINTR) continue;
                throw new Win32Exception(errno, "poll");
            }
            if (fds[1].Returned != 0) return false;
            if (fds[0].Returned != 0) return true;
        }
    }

    private static int Check(int result, string call) =>
        result < 0 ? throw new Win32Exception(Marshal.GetLastPInvokeError(), call) : result;

    private static void CheckSpawn(int error)
    {
        if (error != 0) throw new Win32Exception(error, "posix_spawn setup");
    }

    // Holds the handle open for the length of a call, so that Dispose can't close it and its number be reused
    private static HandleUse Use(SafeFileHandle handle)
    {
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
        }
        catch (ObjectDisposedException)
        {
            return default;
        }
        return new HandleUse(handle, (int)handle.DangerousGetHandle());
    }

    private readonly struct HandleUse(SafeFileHandle handle, int fd) : IDisposable
    {
        public bool Valid => handle is not null;
        public int Fd => fd;
        public void Dispose() => handle?.DangerousRelease();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinSize
    {
        public ushort Rows;
        public ushort Columns;
        public ushort XPixels;
        public ushort YPixels;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Returned;
    }

    private static nint read_(int fd, Span<byte> buffer) => read(fd, ref MemoryMarshal.GetReference(buffer), (nuint)buffer.Length);

    private static nint write_(int fd, ReadOnlySpan<byte> buffer) => write(fd, in MemoryMarshal.GetReference(buffer), (nuint)buffer.Length);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int posix_openpt(int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int grantpt(SafeFileHandle fd);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int unlockpt(SafeFileHandle fd);

    [LibraryImport("libc")]
    private static unsafe partial int ptsname_r(SafeFileHandle fd, byte* buffer, nuint length);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int open(byte* path, int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int pipe2(int* fds, int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int ioctl(SafeFileHandle fd, ulong request, WinSize* size);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int poll(PollFd* fds, nuint count, int timeout);

    [LibraryImport("libc", SetLastError = true)]
    private static partial nint read(int fd, ref byte buffer, nuint count);

    [LibraryImport("libc", SetLastError = true)]
    private static partial nint write(int fd, in byte buffer, nuint count);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int kill(int pid, int signal);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int waitid(int type, int id, byte* info, int options);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int waitpid(int pid, out int status, int options);

    [LibraryImport("libc")]
    private static unsafe partial int sigfillset(byte* set);

    [LibraryImport("libc")]
    private static unsafe partial int sigemptyset(byte* set);

    [LibraryImport("libc")]
    private static unsafe partial int posix_spawn_file_actions_init(byte* actions);

    [LibraryImport("libc")]
    private static unsafe partial int posix_spawn_file_actions_destroy(byte* actions);

    [LibraryImport("libc")]
    private static unsafe partial int posix_spawn_file_actions_addopen(byte* actions, int fd, byte* path, int flags, int mode);

    [LibraryImport("libc")]
    private static unsafe partial int posix_spawn_file_actions_adddup2(byte* actions, int fd, int newFd);

    [LibraryImport("libc")]
    private static unsafe partial int posix_spawn_file_actions_addchdir_np(byte* actions, byte* path);

    [LibraryImport("libc")]
    private static unsafe partial int posix_spawnattr_init(byte* attributes);

    [LibraryImport("libc")]
    private static unsafe partial int posix_spawnattr_destroy(byte* attributes);

    [LibraryImport("libc")]
    private static unsafe partial int posix_spawnattr_setflags(byte* attributes, short flags);

    [LibraryImport("libc")]
    private static unsafe partial int posix_spawnattr_setsigdefault(byte* attributes, byte* set);

    [LibraryImport("libc")]
    private static unsafe partial int posix_spawnattr_setsigmask(byte* attributes, byte* set);

    [LibraryImport("libc")]
    private static unsafe partial int posix_spawnp(out int pid, byte* file, byte* actions, byte* attributes, nint* argv, nint* envp);
}

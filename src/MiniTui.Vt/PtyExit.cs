using System.Globalization;

namespace MiniTui.Vt;

/// <summary>How a process ended: with an exit code, or killed by a signal.</summary>
/// <param name="Code">The exit code, or null when a signal killed it.</param>
/// <param name="Signal">The signal that killed it, or null when it exited.</param>
public readonly record struct PtyExit(int? Code, int? Signal)
{
    /// <summary>A short description, such as <c>exited 1</c> or <c>killed by SIGKILL</c>.</summary>
    public override string ToString() =>
        Signal is { } signal
            ? "killed by " + SignalName(signal)
            : string.Create(CultureInfo.InvariantCulture, $"exited {Code}");

    private static string SignalName(int signal) => signal switch
    {
        1 => "SIGHUP",
        2 => "SIGINT",
        3 => "SIGQUIT",
        4 => "SIGILL",
        6 => "SIGABRT",
        7 => "SIGBUS",
        8 => "SIGFPE",
        9 => "SIGKILL",
        11 => "SIGSEGV",
        13 => "SIGPIPE",
        15 => "SIGTERM",
        _ => string.Create(CultureInfo.InvariantCulture, $"signal {signal}"),
    };
}

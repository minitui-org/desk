using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using MiniTui.Vt;

[assembly: SupportedOSPlatform("linux")]

// Exits 0 when every check passes. The same behaviour as PtyProcessTests, for the AOT-compiled code.
var failures = 0;

var size = Run("stty size; ps -o sid= -p $$; echo pid=$$", new VtStartInfo("bash") { Width = 100, Height = 30 });
Check("size", size.Output.Contains("30 100", StringComparison.Ordinal));
Check("session leader", size.Output.Contains("pid=" + size.Pid, StringComparison.Ordinal) && size.Output.Contains(size.Pid + "\n", StringComparison.Ordinal));
Check("exit code", size.Exit == new PtyExit(0, null));

var directory = Run("pwd", new VtStartInfo("bash") { WorkingDirectory = "/" });
Check("working directory", directory.Output.Contains("/\n", StringComparison.Ordinal));

var environment = Run("echo $TERM $MINITUI_AOT", new VtStartInfo("bash") { Environment = new Dictionary<string, string?> { ["MINITUI_AOT"] = "yes" } });
Check("environment", environment.Output.Contains("xterm-256color yes", StringComparison.Ordinal));

var signals = Run("grep -E 'SigBlk|SigIgn' /proc/$$/status", new VtStartInfo("bash"));
Check("signals", signals.Output.Contains("SigBlk:\t0000000000000000", StringComparison.Ordinal) && signals.Output.Contains("SigIgn:\t0000000000000000", StringComparison.Ordinal));

var killed = Run("kill -9 $$", new VtStartInfo("bash"));
Check("signal status", killed.Exit == new PtyExit(null, 9));

var screen = new VtScreen(20, 4);
screen.Feed("\u001b[1;31mhe\u001b]2;t\u001b\\llo \u65e5e\u0301\u001b(0q\u001b(B\u001b[?2004h"u8);
Check("screen text", screen[0, 0].Symbol == "h" && screen[6, 0].Symbol == "\u65e5" && screen[7, 0].IsContinuation
    && screen[8, 0].Symbol == "e\u0301" && screen[9, 0].Symbol == "\u2500");
Check("screen style", screen[0, 0].Fg == MiniTui.Color.DarkRed && screen[0, 0].Modifiers == MiniTui.Modifiers.Bold);
Check("screen state", screen.Title == "t" && screen.Modes.BracketedPaste);

using (var ctrlC = PtyProcess.Start(new VtStartInfo("bash") { Args = ["-c", "trap 'exit 42' INT; trap 'stty size' WINCH; echo ready; while :; do sleep 0.01; done"] }))
{
    var output = new StringBuilder();
    var reader = new Thread(() =>
    {
        var buffer = new byte[1024];
        int read;
        while ((read = ctrlC.Read(buffer)) > 0)
        {
            lock (output) output.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }
    }) { IsBackground = true };
    reader.Start();
    Check("ready", Wait(() => Text(output).Contains("ready", StringComparison.Ordinal)));
    ctrlC.Resize(90, 20);
    Check("sigwinch", Wait(() => Text(output).Contains("20 90", StringComparison.Ordinal)));
    ctrlC.Write("\x03"u8);
    Check("ctrl+c", ctrlC.Exit.Wait(TimeSpan.FromSeconds(15)) && ctrlC.Exit.Result == new PtyExit(42, null));
}

using (var process = Process.Start(new ProcessStartInfo("bash", ["-c", "sleep 0.1; exit 6"]))!)
{
    var pty = Run("sleep 0.2; exit 5", new VtStartInfo("bash"));
    process.WaitForExit();
    Check("alongside Process", process.ExitCode == 6 && pty.Exit == new PtyExit(5, null));
}

try
{
    PtyProcess.Start(new VtStartInfo("no-such-program-here")).Dispose();
    Check("missing program", false);
}
catch (System.ComponentModel.Win32Exception)
{
    Check("missing program", true);
}

Console.WriteLine(failures == 0 ? "All passed" : $"{failures} failed");
return failures == 0 ? 0 : 1;

static string Text(StringBuilder output)
{
    lock (output) return output.ToString();
}

static bool Wait(Func<bool> condition)
{
    var deadline = DateTime.UtcNow.AddSeconds(15);
    while (!condition())
    {
        if (DateTime.UtcNow > deadline) return false;
        Thread.Sleep(10);
    }
    return true;
}

static (string Output, PtyExit Exit, int Pid) Run(string script, VtStartInfo info)
{
    using var process = PtyProcess.Start(info with { Args = ["--norc", "--noprofile", "-c", script] });
    var output = new StringBuilder();
    var buffer = new byte[4096];
    int read;
    while ((read = process.Read(buffer)) > 0) output.Append(Encoding.UTF8.GetString(buffer, 0, read));
    process.Exit.Wait();
    return (output.ToString().Replace("\r", "", StringComparison.Ordinal), process.Exit.Result, process.Pid);
}

void Check(string name, bool passed)
{
    Console.WriteLine((passed ? "ok    " : "FAIL  ") + name);
    if (!passed) failures++;
}

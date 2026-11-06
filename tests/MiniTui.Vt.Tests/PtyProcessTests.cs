using System.ComponentModel;
using System.Diagnostics;

namespace MiniTui.Vt.Tests;

public class PtyProcessTests
{
    [Fact]
    public void TheChild_IsASessionLeader_WithTheTerminalAsItsControllingTerminal()
    {
        using var pty = PtyHarness.Bash("ps -o sid=,tty= -p $$; echo pid=$$; tty; echo done");
        var output = pty.WaitForEnd();

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var columns = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(pty.Process.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture), columns[0]);
        Assert.StartsWith("pts/", columns[1], StringComparison.Ordinal);
        Assert.StartsWith("/dev/pts/", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void TheChild_StartsAtTheRequestedSize()
    {
        using var pty = PtyHarness.Bash("stty size", new VtStartInfo("bash") { Width = 132, Height = 43 });
        Assert.Contains("43 132", pty.WaitForEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void Resize_ChangesTheSize_AndTheKernelSendsSigwinch()
    {
        using var pty = PtyHarness.Bash("trap 'stty size; exit 0' WINCH; echo ready; while :; do sleep 0.01; done");
        pty.WaitForOutput("ready");

        pty.Process.Resize(120, 40);

        Assert.Contains("40 120", pty.WaitForEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void CtrlC_ReachesTheForegroundJob()
    {
        using var pty = PtyHarness.Bash("trap 'echo interrupted' INT; echo ready; sleep 30; echo after=$?");
        pty.WaitForOutput("ready");

        pty.Send("\x03");

        var output = pty.WaitForEnd();
        Assert.Contains("interrupted", output, StringComparison.Ordinal);
        Assert.Contains("after=130", output, StringComparison.Ordinal);
    }

    [Fact]
    public void CtrlZ_StopsAJob_InAnInteractiveShell()
    {
        using var pty = new PtyHarness(new VtStartInfo("bash") { Args = ["--norc", "--noprofile", "-i"] });

        pty.Send("sleep 30\n");
        Thread.Sleep(300);
        pty.Send("\x1a");
        pty.WaitForOutput("Stopped");
        pty.Send("kill %1; exit\n");
    }

    [Fact]
    public void TheChild_HasNoSignalsBlockedOrIgnored()
    {
        using var pty = PtyHarness.Bash("grep -E 'SigBlk|SigIgn' /proc/$$/status");
        var output = pty.WaitForEnd();
        Assert.Contains("SigBlk:\t0000000000000000", output, StringComparison.Ordinal);
        Assert.Contains("SigIgn:\t0000000000000000", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheChild_DoesntInheritTheTerminalMaster()
    {
        using var pty = PtyHarness.Bash("ls -l /proc/$$/fd");
        Assert.DoesNotContain("ptmx", pty.WaitForEnd(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("exit 0", 0)]
    [InlineData("exit 7", 7)]
    [InlineData("exit 130", 130)]
    public async Task Exit_ReportsTheExitCode(string script, int code)
    {
        using var pty = PtyHarness.Bash(script);
        Assert.Equal(new PtyExit(code, null), await pty.WaitForExit());
        Assert.True(pty.Process.HasExited);
    }

    [Theory]
    [InlineData("kill -9 $$", 9, "killed by SIGKILL")]
    [InlineData("kill -INT $$", 2, "killed by SIGINT")]
    [InlineData("kill -SEGV $$", 11, "killed by SIGSEGV")]
    public async Task Exit_ReportsTheSignal(string script, int signal, string text)
    {
        using var pty = PtyHarness.Bash("trap - INT; " + script);
        var exit = await pty.WaitForExit();
        Assert.Equal(new PtyExit(null, signal), exit);
        Assert.Equal(text, exit.ToString());
    }

    [Fact]
    public void Exit_DescribesAnExitCode() => Assert.Equal("exited 1", new PtyExit(1, null).ToString());

    [Fact]
    public void AnUnnamedSignal_IsDescribedByNumber() => Assert.Equal("killed by signal 40", new PtyExit(null, 40).ToString());

    [Fact]
    public void OutputWrittenBeforeExit_IsStillRead()
    {
        using var pty = PtyHarness.Bash("for i in $(seq 1 2000); do echo line$i; done; exit 3");
        var output = pty.WaitForEnd();
        Assert.Contains("line1\n", output, StringComparison.Ordinal);
        Assert.Contains("line2000\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkingDirectory_IsUsed()
    {
        var directory = Path.GetTempPath().TrimEnd('/');
        using var pty = PtyHarness.Bash("pwd -P", new VtStartInfo("bash") { WorkingDirectory = directory });
        Assert.Contains(new DirectoryInfo(directory).ResolveLinkTarget(true)?.FullName ?? directory, pty.WaitForEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingWorkingDirectory_Throws()
    {
        var info = new VtStartInfo("bash") { WorkingDirectory = "/no/such/directory" };
        Assert.Throws<Win32Exception>(() => PtyProcess.Start(info));
    }

    [Fact]
    public void AMissingProgram_Throws()
    {
        var error = Assert.Throws<Win32Exception>(() => PtyProcess.Start(new VtStartInfo("no-such-program-here")));
        Assert.Contains("no-such-program-here", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedStart_LeavesNothingOpen()
    {
        // Other tests run at the same time and open descriptors, so look for growth over many failures
        static int Open() => Directory.GetFiles("/proc/self/fd").Length;
        var before = Open();
        for (var i = 0; i < 200; i++)
            Assert.Throws<Win32Exception>(() => PtyProcess.Start(new VtStartInfo("/no/such/program")));
        Assert.True(Open() - before < 100, $"{before} descriptors before, {Open()} after");
    }

    [Fact]
    public void TheEnvironment_IsInherited_WithChanges()
    {
        var name = "MINITUI_VT_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "inherited");
        Environment.SetEnvironmentVariable(name + "_GONE", "inherited");
        try
        {
            var info = new VtStartInfo("bash")
            {
                Environment = new Dictionary<string, string?> { [name + "_NEW"] = "added", [name + "_GONE"] = null },
            };
            using var pty = PtyHarness.Bash($"echo a=${name}; echo b=${name}_NEW; echo c=${{{name}_GONE:-unset}}", info);
            var output = pty.WaitForEnd();
            Assert.Contains("a=inherited", output, StringComparison.Ordinal);
            Assert.Contains("b=added", output, StringComparison.Ordinal);
            Assert.Contains("c=unset", output, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
            Environment.SetEnvironmentVariable(name + "_GONE", null);
        }
    }

    [Fact]
    public void TermAndColorterm_AreSet_UnlessOverridden()
    {
        using (var pty = PtyHarness.Bash("echo $TERM $COLORTERM"))
            Assert.Contains("xterm-256color truecolor", pty.WaitForEnd(), StringComparison.Ordinal);

        var info = new VtStartInfo("bash") { Environment = new Dictionary<string, string?> { ["TERM"] = "vt100" } };
        using (var pty = PtyHarness.Bash("echo $TERM", info))
            Assert.Contains("vt100", pty.WaitForEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hangup_EndsAProgramThatDoesntIgnoreIt()
    {
        using var pty = PtyHarness.Bash("echo ready; sleep 30");
        pty.WaitForOutput("ready");
        pty.Process.Hangup();
        Assert.Equal(new PtyExit(null, 1), await pty.WaitForExit());
    }

    [Fact]
    public async Task Kill_EndsAProgramThatIgnoresHangup()
    {
        using var pty = PtyHarness.Bash("trap '' HUP; echo ready; while :; do sleep 0.05; done");
        pty.WaitForOutput("ready");
        pty.Process.Hangup();
        Thread.Sleep(100);
        Assert.False(pty.Process.HasExited);
        pty.Process.Kill();
        Assert.Equal(new PtyExit(null, 9), await pty.WaitForExit());
    }

    [Fact]
    public async Task SignalsAfterExit_DoNothing()
    {
        using var pty = PtyHarness.Bash("exit 0");
        await pty.WaitForExit();
        pty.Process.Hangup();
        pty.Process.Kill();
    }

    [Fact]
    public async Task WritingAfterExit_DoesNothing()
    {
        using var pty = PtyHarness.Bash("exit 0");
        pty.WaitForEnd();
        await pty.WaitForExit();
        pty.Send("ignored");
    }

    [Fact]
    public async Task Dispose_EndsABlockedRead_AndHangsUp()
    {
        var process = PtyProcess.Start(new VtStartInfo("sleep") { Args = ["30"] });
        var read = Task.Run(() => process.Read(new byte[16]));
        await Task.Delay(100);

        process.Dispose();

        Assert.Equal(0, await read.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(new PtyExit(null, 1), await process.Exit.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task AfterDispose_EverythingIsHarmless()
    {
        var process = PtyProcess.Start(new VtStartInfo("sleep") { Args = ["30"] });
        process.Dispose();
        process.Dispose();
        Assert.Equal(0, process.Read(new byte[16]));
        process.Write("x"u8);
        process.Resize(10, 10);
        process.Kill();
        await process.Exit.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Dispose_EndsAWriteBlockedOnAFullBuffer()
    {
        // Raw mode, because line mode throws away what doesn't fit instead of blocking. The child stops, reading nothing.
        using var pty = PtyHarness.Bash("stty raw -echo; echo ready; kill -STOP $$");
        pty.WaitForOutput("ready");
        var write = Task.Run(() =>
        {
            var chunk = new byte[4096];
            for (var i = 0; i < 1000; i++) pty.Process.Write(chunk);
        });
        await Task.Delay(300);
        Assert.False(write.IsCompleted);

        pty.Process.Dispose();

        await write.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void InvalidSizes_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PtyProcess.Start(new VtStartInfo("true") { Width = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PtyProcess.Start(new VtStartInfo("true") { Height = 0 }));
        using var pty = PtyHarness.Bash("sleep 5");
        Assert.Throws<ArgumentOutOfRangeException>(() => pty.Process.Resize(0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => pty.Process.Resize(10, 0));
    }

    [Fact]
    public async Task ReapingDoesntInterfere_WithSystemDiagnosticsProcess()
    {
        // Both ways of starting children, at once: each must see its own child's exit status
        using var pty = PtyHarness.Bash("sleep 0.2; exit 5");
        using var other = Process.Start(new ProcessStartInfo("bash", ["-c", "sleep 0.1; exit 6"]))!;
        using var second = PtyHarness.Bash("sleep 0.3; exit 7");
        await other.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(6, other.ExitCode);
        Assert.Equal(new PtyExit(5, null), await pty.WaitForExit());
        Assert.Equal(new PtyExit(7, null), await second.WaitForExit());
    }

    [Fact]
    public async Task ManyChildren_AllReportTheirOwnStatus()
    {
        var harnesses = Enumerable.Range(0, 20).Select(i => PtyHarness.Bash($"exit {i}")).ToList();
        for (var i = 0; i < harnesses.Count; i++)
        {
            Assert.Equal(new PtyExit(i, null), await harnesses[i].WaitForExit());
            harnesses[i].Dispose();
        }
    }
}

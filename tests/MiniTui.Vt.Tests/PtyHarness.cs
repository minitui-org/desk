using System.Text;

namespace MiniTui.Vt.Tests;

/// <summary>A child in a PTY with its output collected, for tests.</summary>
internal sealed class PtyHarness : IDisposable
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(15);
    private readonly StringBuilder _output = new();
    private readonly Thread _reader;

    public PtyHarness(VtStartInfo info)
    {
        Process = PtyProcess.Start(info);
        _reader = new Thread(ReadAll) { IsBackground = true };
        _reader.Start();
    }

    public PtyProcess Process { get; }

    public string Output
    {
        get
        {
            lock (_output) return _output.ToString().Replace("\r", "", StringComparison.Ordinal);
        }
    }

    public static PtyHarness Bash(string script, VtStartInfo? info = null) =>
        new((info ?? new VtStartInfo("bash")) with { FileName = "bash", Args = ["--norc", "--noprofile", "-c", script] });

    public void Send(string text) => Process.Write(Encoding.UTF8.GetBytes(text));

    public void WaitForOutput(string text)
    {
        var deadline = DateTime.UtcNow + s_timeout;
        while (!Output.Contains(text, StringComparison.Ordinal))
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"No \"{text}\" in the output:\n{Output}");
            Thread.Sleep(10);
        }
    }

    /// <summary>Sends <paramref name="text"/> again every 200 ms until <paramref name="expected"/> is in the output,
    /// for input that only works once the child is ready for it.</summary>
    public void SendUntil(string text, string expected)
    {
        var deadline = DateTime.UtcNow + s_timeout;
        while (!Output.Contains(expected, StringComparison.Ordinal))
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"No \"{expected}\" in the output:\n{Output}");
            Send(text);
            for (var i = 0; i < 20 && !Output.Contains(expected, StringComparison.Ordinal); i++) Thread.Sleep(10);
        }
    }

    public async Task<PtyExit> WaitForExit() => await Process.Exit.WaitAsync(s_timeout);

    public string WaitForEnd()
    {
        if (!_reader.Join(s_timeout)) Assert.Fail($"The output didn't end:\n{Output}");
        return Output;
    }

    public void Dispose()
    {
        Process.Kill();
        Process.Dispose();
    }

    private void ReadAll()
    {
        var buffer = new byte[4096];
        int read;
        while ((read = Process.Read(buffer)) > 0)
        {
            lock (_output) _output.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }
    }
}

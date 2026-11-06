namespace MiniTui.Desk.Tests;

/// <summary>Throwaway files for a test: scripts to host, and a config directory.</summary>
internal sealed class Scripts : IDisposable
{
    private readonly string _directory = System.IO.Directory.CreateTempSubdirectory("tuidesk-test-").FullName;

    public string Directory => _directory;

    public string Path(string name) => System.IO.Path.Combine(_directory, name);

    /// <summary>An executable shell script.</summary>
    public string Script(string body)
    {
        var path = Path("s" + Guid.NewGuid().ToString("N")[..7]);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    public void Dispose() => System.IO.Directory.Delete(_directory, recursive: true);
}

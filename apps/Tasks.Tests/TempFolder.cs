namespace MiniTui.Desk.Tasks.Tests;

/// <summary>A throwaway folder for a test.</summary>
internal sealed class TempFolder : IDisposable
{
    public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("tuitasks-test-").FullName;

    public string Path(string name) => System.IO.Path.Combine(Directory, name);

    public void Dispose()
    {
        // A test may have locked a folder up to make saving fail
        foreach (var folder in System.IO.Directory.EnumerateDirectories(Directory, "*", SearchOption.AllDirectories))
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        System.IO.Directory.Delete(Directory, recursive: true);
    }
}

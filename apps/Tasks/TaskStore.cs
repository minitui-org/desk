using System.Text;

namespace MiniTui.Desk.Tasks;

/// <summary>
/// The todo.txt file (spec §13.9). Every change reads it again and changes only its own line, then writes the whole
/// file by renaming a temporary file over it; every other line goes back byte for byte. Methods throw
/// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when the disk says no.
/// </summary>
internal sealed class TaskStore(string directory)
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public string Directory { get; } = directory;

    public string FilePath => Path.Combine(Directory, "todo.txt");

    /// <summary><c>$XDG_DATA_HOME/tuitasks</c>, which is normally <c>~/.local/share/tuitasks</c>.</summary>
    public static string DefaultDirectory()
    {
        var root = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (root is not { Length: > 0 } || !Path.IsPathRooted(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(root, "tuitasks");
    }

    /// <summary>When the file last changed, or null if it isn't there.</summary>
    public DateTime? Changed => File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : null;

    /// <summary>Every task, in the file's order. Blank lines aren't tasks. No file has none.</summary>
    public List<TodoTask> Read() => [.. Lines().Where(l => l.Trim().Length > 0).Select(l => TodoTask.Parse(Text(l)))];

    /// <summary>Add a task at the end, making the folder and the file if need be.</summary>
    public void Add(TodoTask task)
    {
        var lines = Lines();
        var ending = Ending(lines);
        if (lines is [.., ""]) lines.RemoveAt(lines.Count - 1); // the file's last line break
        lines.Add(task.Line + (ending == "\r\n" ? "\r" : ""));
        lines.Add("");
        Write(lines);
    }

    /// <summary>
    /// Replace the line <paramref name="was"/> with <paramref name="now"/>, or remove it if that's null. False, changing
    /// nothing, if no line is <paramref name="was"/> any more: it was changed or deleted elsewhere.
    /// </summary>
    public bool Change(TodoTask was, TodoTask? now)
    {
        var lines = Lines();
        var index = lines.FindIndex(l => Text(l) == was.Line);
        if (index < 0) return false;
        if (now is null) lines.RemoveAt(index);
        else lines[index] = now.Line + (lines[index].EndsWith('\r') ? "\r" : "");
        Write(lines);
        return true;
    }

    // The file split at '\n', each line keeping a '\r' before it, and an empty last one if it ends with a line break
    private List<string> Lines()
    {
        try
        {
            return [.. File.ReadAllText(FilePath, Utf8).Split('\n')];
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static string Text(string line) => line.TrimEnd('\r');

    private static string Ending(List<string> lines) => lines.Count > 1 && lines[0].EndsWith('\r') ? "\r\n" : "\n";

    private void Write(List<string> lines)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = FilePath;
        if (new FileInfo(path).LinkTarget is not null && File.ResolveLinkTarget(path, returnFinalTarget: true) is { } target)
            path = target.FullName;
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Environment.ProcessId}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                file.Write(Utf8.GetBytes(string.Join('\n', lines)));
                file.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows() && File.Exists(path)) File.SetUnixFileMode(temporary, File.GetUnixFileMode(path));
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }
}

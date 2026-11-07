using System.Text;

namespace MiniTui.Desk.Notes;

/// <summary>A note on disk: its name, without <c>.md</c>, and when it last changed.</summary>
internal readonly record struct NoteFile(string Name, DateTimeOffset Changed);

/// <summary>
/// The notes folder (spec §13.8): one <c>&lt;name&gt;.md</c> per note, written by renaming a temporary file over it.
/// Methods throw <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when the disk says no.
/// </summary>
internal sealed class NotesStore(string directory)
{
    private const string Extension = ".md";

    // A file name is at most 255 bytes, and the temporary file adds a dot, the extension and a pid
    private const int MaxNameBytes = 200;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public string Directory { get; } = directory;

    /// <summary><c>$XDG_DATA_HOME/tuinotes</c>, which is normally <c>~/.local/share/tuinotes</c>.</summary>
    public static string DefaultDirectory()
    {
        var root = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (root is not { Length: > 0 } || !Path.IsPathRooted(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(root, "tuinotes");
    }

    /// <summary>
    /// Every note, most recently changed first. A folder that isn't there yet has none. Files whose names couldn't be
    /// given to a note, with control characters for one, aren't notes.
    /// </summary>
    public List<NoteFile> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var notes = new List<NoteFile>();
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*" + Extension))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!Path.GetFileName(path).EndsWith(Extension, StringComparison.Ordinal) || Problem(name) is not null) continue;
            notes.Add(new NoteFile(name, File.GetLastWriteTimeUtc(path)));
        }
        notes.Sort((a, b) => b.Changed != a.Changed
            ? b.Changed.CompareTo(a.Changed)
            : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return notes;
    }

    public string PathOf(string name) => Path.Combine(Directory, name + Extension);

    /// <summary>The note's text, or null if it's gone.</summary>
    public string? Read(string name)
    {
        try
        {
            return File.ReadAllText(PathOf(name), Utf8);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Replace the note's text all at once, creating the folder and the note if need be. A note that's a link has its
    /// target replaced, and the file keeps its permissions.
    /// </summary>
    public void Write(string name, string text)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathOf(name);
        if (new FileInfo(path).LinkTarget is not null && File.ResolveLinkTarget(path, returnFinalTarget: true) is { } target)
            path = target.FullName;
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Environment.ProcessId}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                file.Write(Utf8.GetBytes(text));
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

    /// <summary>Make a new, empty note. Fails if the name is taken.</summary>
    public void Create(string name)
    {
        System.IO.Directory.CreateDirectory(Directory);
        using var _ = new FileStream(PathOf(name), FileMode.CreateNew, FileAccess.Write);
    }

    public void Rename(string from, string to)
    {
        // A change of case only is a rename on Linux, and Move refuses a target that's there on other systems
        File.Move(PathOf(from), PathOf(to), overwrite: false);
    }

    public void Delete(string name) => File.Delete(PathOf(name));

    /// <summary>Why <paramref name="name"/> can't be a note's name, or null if it can (spec §13.8).</summary>
    public static string? Problem(string name)
    {
        if (name.Trim().Length == 0) return "A note needs a name";
        if (name != name.Trim()) return "A name can't start or end with a space";
        if (name.StartsWith('.')) return "A name can't start with a dot";
        if (name.Contains('/') || name.Contains('\\')) return "A name can't contain a slash";
        if (name.Any(char.IsControl)) return "A name can't contain control characters";
        if (Encoding.UTF8.GetByteCount(name) > MaxNameBytes) return "That name is too long";
        return null;
    }

    /// <summary><paramref name="name"/>'s problem, including matching another note but <paramref name="except"/>.</summary>
    public string? Problem(string name, string? except)
    {
        if (Problem(name) is { } problem) return problem;
        var taken = List().Any(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(n.Name, except, StringComparison.Ordinal));
        return taken ? "There's already a note with that name" : null;
    }

    /// <summary>A name like <paramref name="name"/> that no note has: <c>name (conflict)</c>, then numbered.</summary>
    public string FreeName(string name, string suffix)
    {
        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? $"{name} ({suffix})" : $"{name} ({suffix} {n})";
            if (Problem(candidate, null) is null) return candidate;
        }
    }
}

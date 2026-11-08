using System.Text;

namespace MiniTui.Desk.Reader;

/// <summary>A row of the list: a folder, a file, or <c>..</c>.</summary>
internal sealed record Entry(string Name, string Path, bool IsFolder);

/// <summary>What Reader reads from disk (spec §13.10): folders' readable files, and files as text.</summary>
internal static class Files
{
    /// <summary>A file over this isn't shown.</summary>
    public const long MaxBytes = 10 * 1024 * 1024;

    private const int BinaryCheck = 8 * 1024;
    private static readonly string[] Extensions = [".md", ".markdown", ".txt"];

    public static bool IsMarkdown(string path) => !path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>..</c> unless it's <c>/</c>, then the folders, then the readable files, each by name ignoring case.</summary>
    /// <exception cref="IOException">The folder can't be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder can't be read.</exception>
    public static List<Entry> List(string folder)
    {
        var info = new DirectoryInfo(folder);
        var children = info.EnumerateFileSystemInfos().Where(i => !i.Name.StartsWith('.')).ToList();
        var folders = children.Where(IsFolder).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Name, StringComparer.Ordinal);
        var files = children.Where(i => !IsFolder(i) && Extensions.Any(e => i.Name.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Name, StringComparer.Ordinal);
        var entries = new List<Entry>();
        if (info.Parent is { } parent) entries.Add(new Entry("..", parent.FullName, true));
        entries.AddRange(folders.Select(f => new Entry(f.Name, f.FullName, true)));
        entries.AddRange(files.Select(f => new Entry(f.Name, f.FullName, false)));
        return entries;
    }

    // Links are followed: a link to a folder is a folder
    private static bool IsFolder(FileSystemInfo info)
    {
        if (info is DirectoryInfo) return true;
        if (info.LinkTarget is null) return false;
        try
        {
            return info.ResolveLinkTarget(returnFinalTarget: true) is DirectoryInfo;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// The file's lines, tabs as spaces, or why it isn't shown: too big, or not text. Invalid UTF-8 shows as <c>�</c>.
    /// </summary>
    /// <exception cref="IOException">The file can't be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file can't be read.</exception>
    public static (List<string>? Lines, string? Problem) Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxBytes) return (null, "This file is over 10 MiB, too big to show.");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        if (bytes.AsSpan(0, Math.Min(bytes.Length, BinaryCheck)).Contains((byte)0)) return (null, "This isn't a text file.");
        var text = new UTF8Encoding(false, false).GetString(bytes);
        if (text.StartsWith('﻿')) text = text[1..];
        var lines = text.Split('\n').Select(l => Untab(l.TrimEnd('\r'))).ToList();
        if (lines.Count > 1 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return (lines, null);
    }

    /// <summary>Tabs as spaces to the next multiple of 4, and other control characters dropped.</summary>
    public static string Untab(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (!line.Any(char.IsControl)) return line;
        var result = new StringBuilder();
        var column = 0;
        foreach (var grapheme in UnicodeWidth.Graphemes(line))
        {
            if (grapheme == "\t")
            {
                var spaces = 4 - column % 4;
                result.Append(' ', spaces);
                column += spaces;
            }
            else if (!grapheme.Any(char.IsControl))
            {
                result.Append(grapheme);
                column += UnicodeWidth.OfGrapheme(grapheme);
            }
        }
        return result.ToString();
    }

    /// <summary>A path for people: the home folder as <c>~</c>.</summary>
    public static string Pretty(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 1 && (path == home || path.StartsWith(home + "/", StringComparison.Ordinal))) return "~" + path[home.Length..];
        return path;
    }
}

/// <summary>
/// Where each file was left (spec §13.10): the highlighted block of the last 200 files read, in
/// <c>places.txt</c> as <c>&lt;block&gt;\t&lt;path&gt;</c>, most recent first. Problems reading or writing it are ignored.
/// </summary>
internal sealed class Places(string directory)
{
    public const int Kept = 200;

    private List<(int Block, string Path)>? _places;

    public string Directory { get; } = directory;

    public string FilePath => System.IO.Path.Combine(Directory, "places.txt");

    public static string DefaultDirectory()
    {
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(data) || !System.IO.Path.IsPathRooted(data))
            data = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return System.IO.Path.Combine(data, "tuireader");
    }

    /// <summary>The block <paramref name="path"/> was left at, or 0.</summary>
    public int Of(string path) => Load().FirstOrDefault(p => p.Path == path, (Block: 0, Path: "")).Block;

    /// <summary>Remember it, most recent first, and write the file. False if it couldn't be written.</summary>
    public bool Set(string path, int block)
    {
        var places = Load();
        places.RemoveAll(p => p.Path == path);
        places.Insert(0, (block, path));
        if (places.Count > Kept) places.RemoveRange(Kept, places.Count - Kept);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, string.Concat(places.Select(p => $"{p.Block}\t{p.Path}\n")));
            File.Move(temp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private List<(int Block, string Path)> Load()
    {
        if (_places is not null) return _places;
        _places = [];
        try
        {
            foreach (var line in File.ReadLines(FilePath))
                if (line.Split('\t', 2) is [var block, var path] && int.TryParse(block, System.Globalization.CultureInfo.InvariantCulture, out var b) && b >= 0
                    && path.Length > 0 && _places.All(p => p.Path != path))
                    _places.Add((b, path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // None yet, or unreadable: start without
        }
        return _places;
    }
}

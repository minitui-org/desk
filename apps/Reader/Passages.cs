namespace MiniTui.Desk.Reader;

/// <summary>What a passage looks like in Notes (spec §13.10).</summary>
internal static class Passages
{
    /// <summary>The note a file's passages go to: its name without the extension, without a leading dot.</summary>
    public static string NoteName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).TrimStart('.').Trim();
        return name.Length == 0 ? "Reader" : name;
    }

    /// <summary>The passage as a Markdown quote, where it's from, and a blank line to write on.</summary>
    public static string Quote(string passage, string from)
    {
        ArgumentNullException.ThrowIfNull(passage);
        var lines = passage.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        var quoted = string.Join('\n', lines.Select(l => l.Length == 0 ? ">" : "> " + l));
        return $"{quoted}\n\n— {from}\n\n";
    }

    /// <summary>Lines without the indentation they all share, so a nested list item isn't quoted as code.</summary>
    public static string Dedent(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var shared = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart(' ').Length).DefaultIfEmpty(0).Min();
        return string.Join('\n', lines.Select(l => l.Length >= shared ? l[shared..] : l.TrimStart(' ')));
    }
}

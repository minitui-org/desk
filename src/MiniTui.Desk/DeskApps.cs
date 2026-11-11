using System.Text.Json;
using System.Text.RegularExpressions;

namespace MiniTui.Desk;

/// <summary>What an app says in manifest mode (spec §16.4), as <c>MiniTui.Desk.Protocol</c> writes it.</summary>
internal sealed record ManifestReply
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Version { get; init; }
    public string? Description { get; init; }
    public string[]? Args { get; init; }
}

/// <summary>The launcher's apps beyond <c>apps.json</c> (spec §16.4): the ones added, and the first-party ones on <c>PATH</c>.</summary>
internal static partial class DeskApps
{
    /// <summary>The first-party apps, as their manifests say, found on <c>PATH</c> without being added.</summary>
    public static readonly IReadOnlyList<AppEntry> FirstParty =
    [
        new() { Id = "notes", Name = "Notes", Command = "tuinotes", Description = "Markdown notes in plain files" },
        new() { Id = "tasks", Name = "Tasks", Command = "tuitasks", Description = "A to-do list with due dates, in todo.txt" },
        new() { Id = "reader", Name = "Reader", Command = "tuireader", Description = "Markdown and text files, formatted to read" },
    ];

    /// <summary><c>$XDG_DATA_HOME/tuidesk/apps</c>, which is normally <c>~/.local/share/tuidesk/apps</c>.</summary>
    public static string DefaultDirectory()
    {
        var root = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(root)) root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(root, "tuidesk", "apps");
    }

    /// <summary>Lowercase letters, digits and <c>-</c>; <c>shell</c> is the shell's own.</summary>
    public static bool IsValidId(string? id) => id is { Length: > 0 } && id != DeskShell.ShellId && IdPattern().IsMatch(id);

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex IdPattern();

    public static string PathOf(string directory, string id) => Path.Combine(directory, id + ".json");

    /// <summary>The manifests <c>tuidesk add</c> saved, by id. A broken one is skipped, and said why in <paramref name="errors"/>.</summary>
    public static IReadOnlyList<AppEntry> LoadAdded(string? directory, List<string> errors)
    {
        if (directory is null || !Directory.Exists(directory)) return [];
        var added = new List<AppEntry>();
        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "*.json");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            errors.Add($"Added apps ignored: {e.Message}");
            return [];
        }
        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            if (DeskConfig.Read(file, ConfigJson.Default.AppEntry, errors) is not { } entry) continue;
            var id = Path.GetFileNameWithoutExtension(file);
            if (entry.Id != id || !IsValidId(id)) errors.Add($"{Path.GetFileName(file)} ignored: its id should be \"{id}\", in lowercase letters, digits and -");
            else if (string.IsNullOrWhiteSpace(entry.Command)) errors.Add($"{Path.GetFileName(file)} ignored: it has no command");
            else added.Add(entry with { Source = AppSource.Added });
        }
        return added;
    }

    /// <summary>The first-party apps whose command is in a folder of <paramref name="searchPath"/>.</summary>
    public static IReadOnlyList<AppEntry> Found(string? searchPath)
    {
        if (string.IsNullOrEmpty(searchPath)) return [];
        var folders = searchPath.Split(':', StringSplitOptions.RemoveEmptyEntries);
        return [.. FirstParty
            .Where(app => folders.Any(folder => File.Exists(Path.Combine(folder, app.Command!))))
            .Select(app => app with { Source = AppSource.Found })];
    }

    /// <summary><c>apps.json</c> first and as written, then each added or found app whose id isn't taken yet.</summary>
    public static IReadOnlyList<AppEntry> Merge(IReadOnlyList<AppEntry> appsJson, IReadOnlyList<AppEntry> added, IReadOnlyList<AppEntry> found)
    {
        var apps = appsJson.Select(a => a with { Source = AppSource.AppsJson }).ToList();
        var taken = new HashSet<string>(apps.Where(a => a.Id is not null).Select(a => a.Id!), StringComparer.Ordinal);
        foreach (var app in added.Concat(found))
        {
            if (taken.Add(app.Id!)) apps.Add(app);
        }
        return apps;
    }

    /// <summary>
    /// Parses manifest mode's answer: the first line with a JSON object, or null if there isn't one. In a terminal, .NET
    /// writes its keypad mode first, on the same line.
    /// </summary>
    public static ManifestReply? ParseAnswer(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            if (line.IndexOf('{', StringComparison.Ordinal) is not (>= 0 and var start)) continue;
            var text = line[start..].Trim();
            try
            {
                return JsonSerializer.Deserialize(text, ConfigJson.Default.ManifestReply);
            }
            catch (JsonException)
            {
                return null;
            }
        }
        return null;
    }
}

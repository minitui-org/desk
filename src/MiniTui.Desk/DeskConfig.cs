using System.Text.Json;
using System.Text.Json.Serialization;
using MiniTui.Vt;

namespace MiniTui.Desk;

/// <summary>An app that can be started: an entry in <c>apps.json</c>, or the shell.</summary>
internal sealed record AppEntry
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Command { get; init; }
    public string[]? Args { get; init; }
    public string? Cwd { get; init; }
    public Dictionary<string, string?>? Env { get; init; }
    public string? Description { get; init; }

    /// <summary>Where the entry came from, for <c>tuidesk apps</c>.</summary>
    [JsonIgnore]
    public AppSource Source { get; init; }

    [JsonIgnore]
    public string DisplayName => Name ?? Id ?? Command ?? "";
}

/// <summary>Where an <see cref="AppEntry"/> came from (spec §16.4).</summary>
internal enum AppSource
{
    /// <summary><c>apps.json</c>, written by hand.</summary>
    AppsJson,

    /// <summary>A manifest saved by <c>tuidesk add</c>.</summary>
    Added,

    /// <summary>A first-party app found on <c>PATH</c>.</summary>
    Found,
}

/// <summary>The contents of <c>config.json</c>.</summary>
internal sealed record ConfigFile
{
    public string? Prefix { get; init; }
    public string? NextApp { get; init; }
    public string? PreviousApp { get; init; }
    public string? OtherPane { get; init; }
    public bool? DoNotDisturb { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ConfigFile))]
[JsonSerializable(typeof(AppEntry))]
[JsonSerializable(typeof(AppEntry[]))]
[JsonSerializable(typeof(ManifestReply))]
internal sealed partial class ConfigJson : JsonSerializerContext;

/// <summary>What Desk starts with. Both files are optional, and a broken one is ignored and reported in <see cref="Errors"/>.</summary>
internal sealed record DeskConfig
{
    private const string DefaultPrefix = "ctrl+space";

    public KeyBinding Prefix { get; init; } = KeyBinding.Parse(DefaultPrefix);

    /// <summary>The prefix as a key press, to send to an app.</summary>
    public ConsoleKeyInfo PrefixKey => new(
        Prefix.Char ?? '\0',
        Prefix.Key,
        (Prefix.Modifiers & ConsoleModifiers.Shift) != 0,
        (Prefix.Modifiers & ConsoleModifiers.Alt) != 0,
        (Prefix.Modifiers & ConsoleModifiers.Control) != 0);

    /// <summary>The keys that go to the next and the previous running app, in the status line's order, or null for none.</summary>
    public KeyBinding? NextApp { get; init; } = KeyBinding.Parse("alt+l");
    public KeyBinding? PreviousApp { get; init; } = KeyBinding.Parse("alt+h");

    /// <summary>The key that focuses the other of two panes (spec §14.4), or null for none.</summary>
    public KeyBinding? OtherPane { get; init; } = KeyBinding.Parse("alt+j");

    public bool DoNotDisturb { get; init; } = true;
    public IReadOnlyList<AppEntry> Apps { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>The program for a new shell.</summary>
    public string ShellCommand { get; init; } = Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } shell ? shell : FallbackShell;

    /// <summary>Where the socket for native apps goes, or null for <see cref="DeskServer.DefaultDirectory"/>.</summary>
    public string? RuntimeDirectory { get; init; }

    /// <summary>Where <c>config.json</c> and <c>apps.json</c> are, or null for <see cref="DefaultDirectory"/>.</summary>
    public string? ConfigDirectory { get; init; }

    /// <summary>What's been seen of the way in (spec §15.6), or null to keep nothing and show none of it.</summary>
    public string? StatePath { get; init; }

    /// <summary>How long an app gets to end after a hangup before it's killed.</summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public const string FallbackShell = "/bin/sh";

    /// <summary><c>$XDG_CONFIG_HOME/tuidesk</c>, which is normally <c>~/.config/tuidesk</c>.</summary>
    public static string DefaultDirectory()
    {
        var root = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(root)) root = Path.Combine(Home(), ".config");
        return Path.Combine(root, "tuidesk");
    }

    /// <summary>
    /// From <paramref name="directory"/>, with the apps <c>tuidesk add</c> saved in <paramref name="addedDirectory"/>
    /// and the first-party apps on <paramref name="searchPath"/> (spec §16.4). Without those, only the two files.
    /// </summary>
    public static DeskConfig Load(string directory, string? addedDirectory = null, string? searchPath = null)
    {
        var errors = new List<string>();
        var config = new DeskConfig();

        if (Read(Path.Combine(directory, "config.json"), ConfigJson.Default.ConfigFile, errors) is { } file)
        {
            var key = file.Prefix;
            try
            {
                var prefix = key is null ? config.Prefix : KeyBinding.Parse(key);
                var next = Key(key = file.NextApp, config.NextApp);
                var previous = Key(key = file.PreviousApp, config.PreviousApp);
                var other = Key(key = file.OtherPane, config.OtherPane);
                config = config with { Prefix = prefix, NextApp = next, PreviousApp = previous, OtherPane = other, DoNotDisturb = file.DoNotDisturb ?? config.DoNotDisturb };
            }
            catch (FormatException)
            {
                errors.Add($"config.json ignored: \"{key}\" isn't a key");
            }
        }

        if (Read(Path.Combine(directory, "apps.json"), ConfigJson.Default.AppEntryArray, errors) is { } apps)
        {
            if (Array.FindIndex(apps, a => string.IsNullOrWhiteSpace(a?.Command)) is >= 0 and var bad)
                errors.Add($"apps.json ignored: entry {bad + 1} has no command");
            else
                config = config with { Apps = apps };
        }
        config = config with { Apps = DeskApps.Merge(config.Apps, DeskApps.LoadAdded(addedDirectory, errors), DeskApps.Found(searchPath)) };

        return config with { Errors = errors, ConfigDirectory = directory };
    }

    /// <summary>
    /// How to start <paramref name="entry"/>, at this size, as <paramref name="appId"/> on the protocol at
    /// <paramref name="socket"/>. Without a socket, the variables are removed, so a Desk inside Desk doesn't pass on the outer one's.
    /// </summary>
    public static VtStartInfo StartInfo(AppEntry entry, int width, int height, string? socket = null, string? appId = null)
    {
        var native = socket is not null && appId is not null;
        var environment = new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "tuidesk",
            ["TERM_PROGRAM_VERSION"] = null, // it would describe the terminal outside
            ["MINITUI_DESK_SOCKET"] = native ? socket : null,
            ["MINITUI_DESK_APP_ID"] = native ? appId : null,
        };
        foreach (var (name, value) in entry.Env ?? []) environment[name] = value;

        return new VtStartInfo(entry.Command ?? FallbackShell)
        {
            Args = entry.Args ?? [],
            Environment = environment,
            WorkingDirectory = entry.Cwd is { } cwd ? ExpandHome(cwd) : null,
            Width = width,
            Height = height,
        };
    }

    // A key from config.json: left out it's the default, and "" is no key
    private static KeyBinding? Key(string? name, KeyBinding? fallback) =>
        name is null ? fallback : name.Trim().Length == 0 ? null : KeyBinding.Parse(name);

    internal static T? Read<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, List<string> errors) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            var value = JsonSerializer.Deserialize(File.ReadAllText(path), type);
            if (value is null) errors.Add($"{Path.GetFileName(path)} ignored: it's empty");
            return value;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            errors.Add($"{Path.GetFileName(path)} ignored: {e.Message}");
            return null;
        }
    }

    /// <summary><paramref name="path"/> with <c>~</c> for the home directory, to show.</summary>
    public static string ShortenHome(string path)
    {
        var home = Home().TrimEnd('/');
        return home.Length > 0 && (path == home || path.StartsWith(home + "/", StringComparison.Ordinal)) ? "~" + path[home.Length..] : path;
    }

    private static string ExpandHome(string path) =>
        path == "~" ? Home()
        : path.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(Home(), path[2..])
        : path;

    private static string Home() =>
        Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home ? home : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}

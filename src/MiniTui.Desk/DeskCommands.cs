using System.ComponentModel;
using System.Text;
using System.Text.Json;
using MiniTui.Vt;

namespace MiniTui.Desk;

/// <summary>
/// <c>tuidesk add</c>, <c>remove</c> and <c>apps</c> (spec §16.5): each says one thing and exits, with no terminal
/// and no shell.
/// </summary>
/// <param name="configDirectory">Where <c>config.json</c> and <c>apps.json</c> are.</param>
/// <param name="addedDirectory">Where <c>add</c> saves manifests.</param>
/// <param name="searchPath">Where the first-party apps are looked for: <c>PATH</c>.</param>
internal sealed class DeskCommands(string configDirectory, string addedDirectory, string? searchPath, TextWriter output, TextWriter error)
{
    public const string Usage = """
        Usage: tuidesk                        start Desk
               tuidesk add <command> [--id <id>] [--name <name>] [-- <args>...]
               tuidesk remove <id>
               tuidesk apps
               tuidesk --version
        """;

    /// <summary>How long a program gets to say its manifest.</summary>
    public TimeSpan AnswerTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public static bool Handles(string[] args) => args is ["add", ..] or ["remove", ..] or ["apps", ..];

    public int Run(string[] args) => args switch
    {
        ["add", .. var rest] => Add(rest),
        ["remove", var id] => Remove(id),
        ["apps"] => Apps(),
        _ => Fail(Usage),
    };

    private int Add(string[] args)
    {
        string? command = null, id = null, name = null;
        string[]? appArgs = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--" when command is not null:
                    appArgs = args[(i + 1)..];
                    i = args.Length;
                    break;
                case "--id" when i + 1 < args.Length:
                    id = args[++i];
                    break;
                case "--name" when i + 1 < args.Length:
                    name = args[++i];
                    break;
                case var word when command is null && !word.StartsWith('-'):
                    command = word;
                    break;
                default:
                    return Fail(Usage);
            }
        }
        if (command is null) return Fail(Usage);

        ManifestReply? answer;
        try
        {
            answer = Ask(command);
        }
        catch (Win32Exception e)
        {
            return Fail($"Can't run {command}: {e.Message}");
        }
        id ??= answer?.Id;
        name ??= answer?.Name;
        if (answer is null && (id is null || name is null))
            return Fail($"{command} didn't say its manifest, so it needs --id and --name, such as: tuidesk add {command} --id {Suggest(command)} --name {Path.GetFileName(command)}");
        if (id is null) return Fail($"{command} has no id in its manifest, so it needs --id, such as: tuidesk add {command} --id {Suggest(command)}");
        if (!DeskApps.IsValidId(id)) return Fail($"\"{id}\" can't be an id: use lowercase letters, digits and -, and not \"shell\"");

        var entry = new AppEntry
        {
            Id = id,
            Name = name ?? id,
            Command = command,
            Args = appArgs ?? answer?.Args,
            Description = answer?.Description,
        };
        var path = DeskApps.PathOf(addedDirectory, id);
        var replacing = File.Exists(path);
        try
        {
            Directory.CreateDirectory(addedDirectory);
            File.WriteAllText(path, JsonSerializer.Serialize(entry, ConfigJson.Default.AppEntry) + "\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Fail($"Can't save {DeskConfig.ShortenHome(path)}: {e.Message}");
        }
        output.WriteLine($"{(replacing ? "Replaced" : "Added")} {id}: {entry.Name} ({Describe(entry)}). Desk shows it from its next start.");
        return 0;
    }

    private int Remove(string id)
    {
        var path = DeskApps.PathOf(addedDirectory, id);
        if (DeskApps.IsValidId(id) && File.Exists(path))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Fail($"Can't remove {DeskConfig.ShortenHome(path)}: {e.Message}");
            }
            output.WriteLine($"Removed {id}. Desk stops showing it from its next start.");
            return 0;
        }

        var config = Load();
        return config.Apps.FirstOrDefault(a => a.Id == id) switch
        {
            { Source: AppSource.AppsJson } => Fail($"{id} is in {DeskConfig.ShortenHome(Path.Combine(configDirectory, "apps.json"))}: edit that to remove it"),
            { Source: AppSource.Found } found => Fail($"{id} is {found.Command}, found on PATH, so it's always there; an app added or in apps.json with the id {id} replaces it"),
            _ => Fail($"No app {id}. tuidesk apps lists them"),
        };
    }

    private int Apps()
    {
        var config = Load();
        foreach (var problem in config.Errors) error.WriteLine(problem);
        if (config.Apps.Count == 0)
        {
            output.WriteLine("No apps besides the shell. tuidesk add <command> adds one.");
            return 0;
        }

        var rows = config.Apps.Select(a => (Id: a.Id ?? "-", Name: a.DisplayName, Command: Describe(a), From: From(a))).ToList();
        var idWidth = Math.Max(2, rows.Max(r => r.Id.Length));
        var nameWidth = Math.Max(4, rows.Max(r => r.Name.Length));
        var commandWidth = Math.Max(7, rows.Max(r => r.Command.Length));
        output.WriteLine($"{"ID".PadRight(idWidth)}  {"NAME".PadRight(nameWidth)}  {"COMMAND".PadRight(commandWidth)}  FROM");
        foreach (var row in rows) output.WriteLine($"{row.Id.PadRight(idWidth)}  {row.Name.PadRight(nameWidth)}  {row.Command.PadRight(commandWidth)}  {row.From}");
        return 0;
    }

    private DeskConfig Load() => DeskConfig.Load(configDirectory, addedDirectory, searchPath);

    /// <summary>
    /// Runs <paramref name="command"/> in manifest mode, in a terminal of its own so a program that isn't a native app
    /// can't draw on this one, and returns what it said, or null if it said nothing it could mean.
    /// </summary>
    private ManifestReply? Ask(string command)
    {
        using var process = PtyProcess.Start(new VtStartInfo(command)
        {
            Environment = new Dictionary<string, string?>
            {
                ["MINITUI_DESK_MANIFEST"] = "1",
                ["MINITUI_DESK_SOCKET"] = null,
                ["MINITUI_DESK_APP_ID"] = null,
            },
        });
        var said = new StringBuilder();
        var reader = new Thread(() =>
        {
            var buffer = new byte[4096];
            int count;
            while ((count = process.Read(buffer)) > 0)
            {
                lock (said) said.Append(Encoding.UTF8.GetString(buffer, 0, count));
            }
        }) { IsBackground = true };
        reader.Start();

        var ended = process.Exit.Wait(AnswerTimeout) && process.Exit.Result is { Code: 0 };
        if (!process.HasExited) process.Kill();
        reader.Join(TimeSpan.FromSeconds(1));
        if (!ended) return null;
        lock (said) return DeskApps.ParseAnswer(said.ToString());
    }

    private static string Describe(AppEntry app) =>
        app.Args is { Length: > 0 } args ? $"{app.Command} {string.Join(' ', args)}" : app.Command ?? "";

    private static string From(AppEntry app) => app.Source switch
    {
        AppSource.Added => "added",
        AppSource.Found => "found on PATH",
        _ => "apps.json",
    };

    // An id from a command's name: k9s, or htop from /usr/bin/htop
    private static string Suggest(string command)
    {
        var name = Path.GetFileName(command).ToLowerInvariant();
        var id = new string([.. name.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]).Trim('-');
        return DeskApps.IsValidId(id) ? id : "my-app";
    }

    private int Fail(string message)
    {
        error.WriteLine(message);
        return message == Usage ? 2 : 1;
    }
}

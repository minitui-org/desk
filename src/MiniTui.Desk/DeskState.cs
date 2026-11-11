using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiniTui.Desk;

/// <summary>What someone has already seen of the way in (spec §15.6), kept between runs.</summary>
internal sealed class DeskState
{
    private readonly string? _path;

    private DeskState(string? path, StateFile file)
    {
        _path = path;
        Welcomed = file.Welcomed;
        PrefixUsed = file.PrefixUsed;
    }

    /// <summary>The welcome (§15.4) has been shown.</summary>
    public bool Welcomed { get; private set; }

    /// <summary>The prefix has been pressed, so the status line's hint (§15.3) is no longer needed.</summary>
    public bool PrefixUsed { get; private set; }

    /// <summary><c>$XDG_STATE_HOME/tuidesk/state.json</c>, which is normally <c>~/.local/state/tuidesk/state.json</c>.</summary>
    public static string DefaultPath()
    {
        var root = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (string.IsNullOrEmpty(root)) root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
        return Path.Combine(root, "tuidesk", "state.json");
    }

    /// <summary>
    /// From <paramref name="path"/>, where a missing or broken file is nothing seen. With no path, everything counts as
    /// seen and nothing is kept: for a Desk that isn't the user's own, as in tests.
    /// </summary>
    public static DeskState Load(string? path)
    {
        if (path is null) return new DeskState(null, new StateFile { Welcomed = true, PrefixUsed = true });
        try
        {
            var file = JsonSerializer.Deserialize(File.ReadAllText(path), StateJson.Default.StateFile);
            return new DeskState(path, file ?? new StateFile());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new DeskState(path, new StateFile());
        }
    }

    public void MarkWelcomed()
    {
        if (Welcomed) return;
        Welcomed = true;
        Save();
    }

    public void MarkPrefixUsed()
    {
        if (PrefixUsed) return;
        PrefixUsed = true;
        Save();
    }

    // A write that fails only means the welcome or the hint shows again next time
    private void Save()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new StateFile { Welcomed = Welcomed, PrefixUsed = PrefixUsed }, StateJson.Default.StateFile));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>The contents of <c>state.json</c>.</summary>
internal sealed record StateFile
{
    public bool Welcomed { get; init; }
    public bool PrefixUsed { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StateFile))]
internal sealed partial class StateJson : JsonSerializerContext;

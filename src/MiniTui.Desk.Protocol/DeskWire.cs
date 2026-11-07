using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiniTui.Desk.Protocol;

/// <summary>One line of the protocol (spec §8.3). Which fields mean anything depends on <see cref="Type"/>.</summary>
internal sealed record DeskMessage
{
    public string? Type { get; init; }
    public string? AppId { get; init; }
    public string? Name { get; init; }
    public string? Version { get; init; }
    public int? Protocol { get; init; }
    public string? Text { get; init; }
    public string? Id { get; init; }
    public string? Title { get; init; }
    public string? Body { get; init; }
    public string? Urgency { get; init; }
    public DeskAction[]? Actions { get; init; }
    public string[]? Args { get; init; }
    public string? Theme { get; init; }
    public bool? Focused { get; init; }
    public string? Action { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DeskMessage))]
internal sealed partial class DeskJson : JsonSerializerContext;

/// <summary>The protocol's format, shared by the client and the shell (spec §8.2).</summary>
internal static class DeskWire
{
    /// <summary>The highest version this code speaks.</summary>
    public const int ProtocolVersion = 0;

    /// <summary>The longest line, not counting its newline.</summary>
    public const int MaxLine = 64 * 1024;

    public const string Hello = "hello";
    public const string Title = "title";
    public const string Notify = "notify";
    public const string Launch = "launch";
    public const string QuitDeclined = "quitDeclined";
    public const string Bye = "bye";
    public const string Welcome = "welcome";
    public const string Focus = "focus";
    public const string NotificationAction = "notificationAction";
    public const string QuitRequest = "quitRequest";

    /// <summary>The message as a line, or null if it would be too long to send.</summary>
    public static byte[]? Encode(DeskMessage message)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, DeskJson.Default.DeskMessage);
        if (json.Length > MaxLine) return null;
        var line = new byte[json.Length + 1];
        json.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        return line;
    }

    /// <summary>The message on a line, or null if there's nothing usable there, which is ignored rather than an error.</summary>
    public static DeskMessage? Decode(ReadOnlySpan<byte> line)
    {
        try
        {
            return JsonSerializer.Deserialize(line, DeskJson.Default.DeskMessage) is { Type: not null } message ? message : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string UrgencyName(DeskUrgency urgency) => urgency switch
    {
        DeskUrgency.Low => "low",
        DeskUrgency.High => "high",
        _ => "normal",
    };
}

/// <summary>Splits a stream of bytes into lines, however they arrive.</summary>
internal sealed class DeskLineFramer
{
    private readonly byte[] _pending = new byte[DeskWire.MaxLine];
    private int _count;

    /// <summary>Hand each complete line to <paramref name="line"/>. False if a line is too long, which ends the connection.</summary>
    public bool Push(ReadOnlySpan<byte> data, Action<byte[]> line)
    {
        ArgumentNullException.ThrowIfNull(line);
        while (true)
        {
            var end = data.IndexOf((byte)'\n');
            var part = end >= 0 ? data[..end] : data;
            if (_count + part.Length > _pending.Length) return false;
            part.CopyTo(_pending.AsSpan(_count));
            _count += part.Length;
            if (end < 0) return true;

            line(_pending.AsSpan(0, _count).ToArray());
            _count = 0;
            data = data[(end + 1)..];
        }
    }
}

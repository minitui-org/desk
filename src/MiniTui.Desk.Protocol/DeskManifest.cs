namespace MiniTui.Desk.Protocol;

/// <summary>
/// How Desk lists an app and how other apps open it (spec §16.4). Passed to
/// <see cref="DeskAppExtensions.WithDesk(App, string, out DeskClient, string?, DeskManifest?)"/>, and read from the
/// app by <c>tuidesk add</c>.
/// </summary>
public sealed record DeskManifest
{
    /// <summary>
    /// What other apps open it by with <see cref="DeskClient.Launch"/>: lowercase letters, digits and <c>-</c>, such as
    /// <c>notes</c>.
    /// </summary>
    public string? Id { get; init; }

    /// <summary>One line on what the app is for.</summary>
    public string? Description { get; init; }

    /// <summary>Arguments Desk starts it with, before any that a <c>launch</c> adds.</summary>
    public IReadOnlyList<string>? Args { get; init; }
}

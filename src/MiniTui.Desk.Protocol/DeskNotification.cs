namespace MiniTui.Desk.Protocol;

/// <summary>How much a notification matters. The shell decides what that means, and Do Not Disturb wins.</summary>
public enum DeskUrgency
{
    /// <summary>Only ever counted.</summary>
    Low,

    /// <summary>The default.</summary>
    Normal,

    /// <summary>Important. It still doesn't interrupt Do Not Disturb.</summary>
    High,
}

/// <summary>Something the user can do from a notification.</summary>
/// <param name="Id">Reported back through <see cref="DeskClient.NotificationAction"/>.</param>
/// <param name="Label">What the user sees.</param>
public sealed record DeskAction(string Id, string Label);

/// <summary>A notification for the shell to queue.</summary>
/// <param name="Id">Identifies it within this app. Sending the same id again replaces the earlier one.</param>
/// <param name="Title">The heading.</param>
/// <param name="Body">The text.</param>
public sealed record DeskNotification(string Id, string Title, string Body)
{
    /// <summary>How much it matters.</summary>
    public DeskUrgency Urgency { get; init; } = DeskUrgency.Normal;

    /// <summary>What the user can do from it, if anything.</summary>
    public IReadOnlyList<DeskAction>? Actions { get; init; }
}

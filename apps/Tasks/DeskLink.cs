using MiniTui.Desk.Protocol;

namespace MiniTui.Desk.Tasks;

/// <summary>What Tasks does with Desk (spec §13.9). Outside Desk none of it happens.</summary>
internal static class DeskLink
{
    /// <summary>
    /// Show the file as it is on the way back to the screen, and do what a notification's action asks. Every change is
    /// already in the file, so a request to quit quits.
    /// </summary>
    public static void Join(TasksScreen tasks, DeskClient desk)
    {
        desk.FocusChanged += focused =>
        {
            if (focused) tasks.Refresh();
        };
        desk.NotificationAction += tasks.Act;
    }
}

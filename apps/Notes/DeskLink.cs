using MiniTui.Desk.Protocol;

namespace MiniTui.Desk.Notes;

/// <summary>What Notes does with Desk (spec §13.8). Outside Desk none of it happens.</summary>
internal static class DeskLink
{
    /// <summary>
    /// Save on the way out of the screen, show what changed meanwhile on the way back, and on a request to quit,
    /// quit if the note saves and stay otherwise, so the text isn't lost.
    /// </summary>
    public static void Join(NotesScreen notes, DeskClient desk)
    {
        desk.FocusChanged += focused =>
        {
            if (focused) notes.Refresh();
            else notes.Save();
        };
        desk.QuitRequested += () =>
        {
            if (!notes.Quit()) desk.DeclineQuit();
        };
    }
}

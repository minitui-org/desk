using MiniTui.Desk.Protocol;

namespace MiniTui.Desk.Reader;

/// <summary>What Reader does with Desk (spec §13.10). Outside Desk none of it happens.</summary>
internal static class DeskLink
{
    /// <summary>Show what changed meanwhile on the way back to the screen.</summary>
    public static void Join(ReaderScreen reader, DeskClient desk) => desk.FocusChanged += focused =>
    {
        if (focused) reader.Refresh();
    };
}

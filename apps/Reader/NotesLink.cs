using System.Diagnostics;
using MiniTui.Desk.Protocol;

namespace MiniTui.Desk.Reader;

/// <summary>How a passage gets to Notes (spec §13.10): <c>launch</c> inside Desk, or tuinotes in this terminal.</summary>
internal static class NotesLink
{
    /// <summary>The sender for <see cref="ReaderScreen"/>: a note's name and text in, what to say out.</summary>
    public static Func<string, string, string?> For(App app, DeskClient desk) =>
        (name, text) => desk.IsActive ? InDesk(desk, name, text) : Outside(app, name, text);

    // Desk starts the `notes` entry with these after its own arguments, and brings it to the front (spec §13.4)
    private static string InDesk(DeskClient desk, string name, string text)
    {
        desk.Launch("notes", "--append", name, text);
        return "Sent to Notes";
    }

    // Notes in this terminal until it quits
    private static string? Outside(App app, string name, string text)
    {
        if (Program() is not { } notes) return "Can't find tuinotes, next to tuireader or on PATH";
        string? problem = null;
        app.Suspend(() =>
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(notes) { ArgumentList = { "--append", name, text }, UseShellExecute = false });
                process?.WaitForExit();
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                problem = $"Can't start tuinotes: {e.Message}";
            }
        });
        return problem;
    }

    /// <summary>tuinotes beside tuireader, or on <c>PATH</c>.</summary>
    internal static string? Program()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "tuinotes");
        if (File.Exists(beside)) return beside;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(folder, "tuinotes");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

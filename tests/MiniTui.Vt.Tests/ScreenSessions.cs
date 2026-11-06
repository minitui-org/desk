using System.Text;
using MiniTui;

namespace MiniTui.Vt.Tests;

/// <summary>A recorded session, and what tmux showed at each checkpoint. See tools/vt_golden.py.</summary>
internal sealed record ScreenSession(string Name, int Width, int Height, byte[] Output, int[] Offsets, string Directory)
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "Sessions");

    public static IEnumerable<string> Names(string? prefix = null) =>
        System.IO.Directory.GetDirectories(Root).Select(Path.GetFileName).Where(n => prefix is null || n!.StartsWith(prefix, StringComparison.Ordinal)).Order().ToArray()!;

    public static ScreenSession Load(string name)
    {
        var directory = Path.Combine(Root, name);
        var lines = File.ReadAllLines(Path.Combine(directory, "session.txt"));
        var size = lines[0].Split(' ');
        return new ScreenSession(name, int.Parse(size[0]), int.Parse(size[1]),
            File.ReadAllBytes(Path.Combine(directory, "session.bin")),
            lines.Skip(1).Select(int.Parse).ToArray(), directory);
    }

    /// <summary>
    /// The screen tmux showed, rebuilt by replaying what it captured, and its cursor. tmux reports
    /// one column past the edge while a wrap is pending, which VtScreen shows as the last column.
    /// </summary>
    public (VtScreen Screen, (int X, int Y) Cursor, bool CursorVisible) Expected(int checkpoint)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Directory, $"{checkpoint:00}.screen"));
        var firstLine = Array.IndexOf(bytes, (byte)'\n');
        var cursor = Encoding.ASCII.GetString(bytes, 0, firstLine).Split(' ');
        var capture = bytes.AsSpan(firstLine + 1);
        var screen = new VtScreen(Width, Height);
        // tmux captures line drawing as SO and SI and assumes G1 is set up for it. It also pads a
        // line holding wide characters past the edge, which mustn't wrap.
        screen.Feed("\u001b)0\u001b[?7l"u8);
        var y = 0;
        while (!capture.IsEmpty && y < Height)
        {
            var end = capture.IndexOf((byte)'\n');
            if (end < 0) end = capture.Length;
            screen.Feed(Encoding.ASCII.GetBytes($"\u001b[{y + 1};1H"));
            screen.Feed(capture[..end]);
            capture = end < capture.Length ? capture[(end + 1)..] : default;
            y++;
        }
        return (screen, (Math.Min(int.Parse(cursor[0]), Width - 1), int.Parse(cursor[1])), cursor[2] == "1");
    }
}

internal static class ScreenAssert
{
    /// <summary>Compare what a person would see, so a blank's foreground color doesn't count.</summary>
    public static void Same(VtScreen expected, VtScreen actual, string context)
    {
        for (var y = 0; y < expected.Height; y++)
        {
            for (var x = 0; x < expected.Width; x++)
            {
                if (!LooksSame(expected[x, y], actual[x, y]))
                {
                    Assert.Fail($"{context}: cell {x},{y}: expected {expected[x, y]}, got {actual[x, y]}\n--- expected\n{Text(expected)}\n--- actual\n{Text(actual)}");
                }
            }
        }
    }

    private static bool LooksSame(Cell a, Cell b)
    {
        if (a == b) return true;
        if (a.Symbol != b.Symbol) return false;
        if (a.Symbol is not (" " or "")) return false;
        const Modifiers Visible = Modifiers.Underlined | Modifiers.CrossedOut | Modifiers.Reversed;
        if ((a.Modifiers & Visible) != (b.Modifiers & Visible)) return false;
        return (a.Modifiers & Modifiers.Reversed) != 0 ? a.Fg == b.Fg && a.Bg == b.Bg : a.Bg == b.Bg;
    }

    public static string Text(VtScreen screen)
    {
        var text = new StringBuilder();
        for (var y = 0; y < screen.Height; y++)
        {
            for (var x = 0; x < screen.Width; x++) text.Append(screen[x, y].Symbol);
            text.Append('\n');
        }
        return text.ToString();
    }

    /// <summary>The text with trailing blanks and lines removed, which reads better in a test.</summary>
    public static string Trimmed(VtScreen screen) =>
        string.Join('\n', Text(screen).Split('\n').Select(line => line.TrimEnd())).TrimEnd('\n');

    /// <summary>What must hold after any input whatsoever.</summary>
    public static void Consistent(VtScreen screen)
    {
        if (screen.Cursor is { } cursor)
        {
            Assert.InRange(cursor.X, 0, screen.Width - 1);
            Assert.InRange(cursor.Y, 0, screen.Height - 1);
        }
        for (var y = 0; y < screen.Height; y++)
        {
            for (var x = 0; x < screen.Width; x++)
            {
                var cell = screen[x, y];
                var previousIsWide = x > 0 && screen[x - 1, y].Width == 2;
                Assert.True(cell.IsContinuation == previousIsWide, $"cell {x},{y} '{cell.Symbol}' after a {(previousIsWide ? "wide" : "narrow")} cell");
                if (cell.Width == 2) Assert.True(x < screen.Width - 1, $"wide cell at the right edge, line {y}");
            }
        }
    }
}

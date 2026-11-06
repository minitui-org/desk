namespace MiniTui.Vt.Tests;

/// <summary>
/// The steps of vttest where tmux can't be the reference, checked against what vttest itself says
/// should be on the screen. See docs/VTTEST.md.
/// </summary>
public sealed class VttestTests
{
    private static VtScreen ReplayToSkipped(string name, int index)
    {
        var session = ScreenSession.Load(name);
        var offset = int.Parse(File.ReadAllLines(Path.Combine(session.Directory, "skipped.txt"))[index]);
        var screen = new VtScreen(session.Width, session.Height);
        screen.Feed(session.Output.AsSpan(0, offset));
        return screen;
    }

    [Fact]
    public void Autowrap_LettersRunDownBothMargins()
    {
        // "The left/right margins should have letters in order". The test starts at I at 24 lines.
        var screen = ReplayToSkipped("vttest-cursor", 1);
        for (var row = 0; row < 18; row++)
        {
            Assert.Equal(((char)('I' + row)).ToString(), screen[0, row + 2].Symbol);
            Assert.Equal(((char)('i' + row)).ToString(), screen[79, row + 2].Symbol);
        }
    }

    [Fact]
    public void LightBackground_ReversesTheWholeScreen()
    {
        var screen = ReplayToSkipped("vttest-screen", 0);
        Assert.Contains("light background", ScreenAssert.Text(screen), StringComparison.Ordinal);
        Assert.Equal(Modifiers.Reversed, screen[79, 23].Modifiers);
    }

    [Fact]
    public void OriginMode_TheLineIsAtTheBottom()
    {
        // "Origin mode test. This line should be at the bottom of the screen."
        var lines = ScreenAssert.Trimmed(ReplayToSkipped("vttest-screen", 2)).Split('\n');
        Assert.Equal(24, lines.Length);
        Assert.StartsWith("Origin mode test.", lines[23], StringComparison.Ordinal);
        Assert.StartsWith("This line should be the one above the bottom", lines[22], StringComparison.Ordinal);
    }

    [Fact]
    public void GraphicRendition_LightBackgroundReversesAndNegativeFlipsBack()
    {
        var screen = ReplayToSkipped("vttest-screen", 3);
        var text = ScreenAssert.Text(screen);
        Assert.Contains("Light background", text, StringComparison.Ordinal);
        Assert.Equal(Modifiers.Reversed, screen[79, 0].Modifiers);

        var negativeRow = ScreenAssert.Trimmed(screen).Split('\n').ToList().FindIndex(l => l.StartsWith("negative", StringComparison.Ordinal));
        Assert.Equal(Modifiers.None, screen[0, negativeRow].Modifiers);
    }
}

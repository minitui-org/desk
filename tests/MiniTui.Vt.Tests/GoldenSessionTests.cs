namespace MiniTui.Vt.Tests;

/// <summary>Real programs' output and generated sequences, against the screens tmux showed.</summary>
public sealed class GoldenSessionTests
{
    public static TheoryData<string> Sessions() => new(ScreenSession.Names());

    [Theory]
    [MemberData(nameof(Sessions))]
    public void Replay_MatchesTheScreenTmuxShowed(string name)
    {
        var session = ScreenSession.Load(name);
        var screen = new VtScreen(session.Width, session.Height);
        var position = 0;
        for (var checkpoint = 0; checkpoint < session.Offsets.Length; checkpoint++)
        {
            var offset = session.Offsets[checkpoint];
            screen.Feed(session.Output.AsSpan(position, offset - position));
            position = offset;

            var (expected, cursor, visible) = session.Expected(checkpoint);
            ScreenAssert.Same(expected, screen, $"{name} checkpoint {checkpoint} (offset {offset})");
            Assert.True(cursor == screen.CursorPosition, $"{name} checkpoint {checkpoint} (offset {offset}): cursor expected {cursor}, got {screen.CursorPosition}\n{ScreenAssert.Text(screen)}");
            Assert.Equal(visible, screen.Cursor is not null);
            ScreenAssert.Consistent(screen);
        }
    }
}

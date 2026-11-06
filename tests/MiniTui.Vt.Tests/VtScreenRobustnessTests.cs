using System.Text;

namespace MiniTui.Vt.Tests;

/// <summary>What must hold for any input: no exceptions, a sound grid, and no effect from where it's split.</summary>
public sealed class VtScreenRobustnessTests
{
    private static readonly byte[] Interesting =
        "\u001b\u001b[\u001b]\u001bP\u001b_\u001b(\u001b#;:?><=!$ 0123456789mHJKLMPSTXr@bcdhlnsu\r\n\t\b\x07\x0e\x0f\x18\x1a日é́‍️\U0001F468"u8.ToArray();

    public static TheoryData<string> Sessions() => new(ScreenSession.Names());

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void RandomBytes_NeverThrowAndLeaveASoundGrid(int seed)
    {
        var random = new Random(seed);
        var screen = new VtScreen(30, 8);
        for (var round = 0; round < 300; round++)
        {
            var bytes = new byte[random.Next(1, 400)];
            random.NextBytes(bytes);
            screen.Feed(bytes);
            ScreenAssert.Consistent(screen);
            _ = screen.Modes;
            _ = screen.Title;
            if (round % 10 == 0) screen.Resize(random.Next(1, 40), random.Next(1, 12));
        }
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    public void RandomControlSequences_NeverThrowAndLeaveASoundGrid(int seed)
    {
        // Pure random bytes rarely form a sequence, so this draws from the bytes that do.
        var random = new Random(seed);
        var screen = new VtScreen(30, 8);
        for (var round = 0; round < 300; round++)
        {
            var bytes = new byte[random.Next(1, 400)];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = Interesting[random.Next(Interesting.Length)];
            screen.Feed(bytes);
            ScreenAssert.Consistent(screen);
            if (round % 10 == 0) screen.Resize(random.Next(1, 40), random.Next(1, 12));
        }
    }

    [Fact]
    public void HugeParameters_DoNotOverflow()
    {
        var screen = new VtScreen(10, 5);
        foreach (var final in "ABCDEFGHIJKLMPSTXZ@abdefgr")
        {
            screen.Feed(Encoding.ASCII.GetBytes($"\u001b[99999999999999999999;99999999999999999999{final}"));
            ScreenAssert.Consistent(screen);
        }
    }

    [Theory]
    [MemberData(nameof(Sessions))]
    public void SplittingAtEveryByte_GivesTheSameScreen(string name)
    {
        var session = ScreenSession.Load(name);
        var whole = new VtScreen(session.Width, session.Height);
        whole.Feed(session.Output);

        var split = new VtScreen(session.Width, session.Height);
        for (var i = 0; i < session.Output.Length; i++) split.Feed(session.Output.AsSpan(i, 1));

        ScreenAssert.Same(whole, split, name);
        Assert.Equal(whole.CursorPosition, split.CursorPosition);
    }

    [Theory]
    [MemberData(nameof(Sessions))]
    public void SplittingAnywhere_GivesTheSameScreen(string name)
    {
        var session = ScreenSession.Load(name);
        var whole = new VtScreen(session.Width, session.Height);
        whole.Feed(session.Output);

        var random = new Random(42);
        for (var round = 0; round < 10; round++)
        {
            var split = new VtScreen(session.Width, session.Height);
            var position = 0;
            while (position < session.Output.Length)
            {
                var length = Math.Min(random.Next(1, 200), session.Output.Length - position);
                split.Feed(session.Output.AsSpan(position, length));
                position += length;
            }
            ScreenAssert.Same(whole, split, name);
            Assert.Equal(whole.CursorPosition, split.CursorPosition);
        }
    }

    [Fact]
    public void SplittingAnyPairOfSequences_GivesTheSameScreen()
    {
        // Every split point of input that has every kind of state the parser can be left in.
        var input = "a\u001b[1;31mb\u001b]2;tïtle\u001b\\日本\u001b(0q\u001b(Bé\U0001F600\u001bPdcs\u001b\\\u001b[?1049h\u001b[38:2::1:2:3mx"u8.ToArray();
        var whole = new VtScreen(20, 4);
        whole.Feed(input);
        for (var cut = 1; cut < input.Length; cut++)
        {
            var split = new VtScreen(20, 4);
            split.Feed(input.AsSpan(0, cut));
            split.Feed(input.AsSpan(cut));
            ScreenAssert.Same(whole, split, $"cut at {cut}");
            Assert.Equal(whole.Title, split.Title);
            Assert.Equal(whole.Modes, split.Modes);
        }
    }
}

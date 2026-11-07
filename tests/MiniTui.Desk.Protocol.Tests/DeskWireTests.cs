using System.Text;

namespace MiniTui.Desk.Protocol.Tests;

public class DeskWireTests
{
    public static TheoryData<string> EveryMessage => new()
    {
        """{"type":"hello","appId":"notes-1","name":"Notes","version":"0.1.0","protocol":0}""",
        """{"type":"title","text":"Today"}""",
        """{"type":"notify","id":"sync","title":"Synced","body":"12 notes","urgency":"low"}""",
        """{"type":"notify","id":"a","title":"t","body":"b","urgency":"high","actions":[{"id":"done","label":"Done"}]}""",
        """{"type":"launch","appId":"notes","args":["a","b"]}""",
        """{"type":"quitDeclined"}""",
        """{"type":"bye"}""",
        """{"type":"welcome","protocol":0,"theme":"dark"}""",
        """{"type":"focus","focused":false}""",
        """{"type":"notificationAction","id":"a","action":"done"}""",
        """{"type":"quitRequest"}""",
    };

    [Theory]
    [MemberData(nameof(EveryMessage))]
    public void Every_message_round_trips_exactly(string json)
    {
        var message = DeskWire.Decode(Encoding.UTF8.GetBytes(json));
        Assert.NotNull(message);
        Assert.Equal(json + "\n", Encoding.UTF8.GetString(DeskWire.Encode(message!)!));
    }

    [Fact]
    public void Unknown_fields_are_ignored()
    {
        var message = DeskWire.Decode("""{"type":"title","text":"t","later":{"a":[1]}}"""u8);
        Assert.Equal("t", message!.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("[1]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"type":1}""")]
    [InlineData("""{"type":"focus","focused":"yes"}""")]
    [InlineData("""{"type":"welcome","protocol":99999999999}""")]
    public void A_line_with_nothing_usable_is_ignored(string line) => Assert.Null(DeskWire.Decode(Encoding.UTF8.GetBytes(line)));

    [Fact]
    public void Invalid_utf8_is_ignored() => Assert.Null(DeskWire.Decode([(byte)'"', 0xFF, (byte)'"']));

    [Theory]
    [InlineData(DeskUrgency.Low, "low")]
    [InlineData(DeskUrgency.Normal, "normal")]
    [InlineData(DeskUrgency.High, "high")]
    public void Urgency_has_the_documented_names(DeskUrgency urgency, string name) => Assert.Equal(name, DeskWire.UrgencyName(urgency));

    [Fact]
    public void A_message_that_would_be_too_long_cannot_be_encoded()
    {
        const int Overhead = 26; // {"type":"title","text":""}
        var fits = new DeskMessage { Type = "title", Text = new string('x', DeskWire.MaxLine - Overhead) };
        var over = fits with { Text = fits.Text + "x" };
        var line = DeskWire.Encode(fits);
        Assert.NotNull(line);
        Assert.Equal(DeskWire.MaxLine, line!.Length - 1);
        Assert.Null(DeskWire.Encode(over));
    }

    [Fact]
    public void Lines_are_found_however_the_bytes_arrive()
    {
        var framer = new DeskLineFramer();
        var lines = new List<string>();
        void Add(byte[] line) => lines.Add(Encoding.UTF8.GetString(line));

        Assert.True(framer.Push("ab"u8, Add));
        Assert.Empty(lines);
        Assert.True(framer.Push("c\nd\n\ne"u8, Add));
        Assert.Equal(["abc", "d", ""], lines);
        Assert.True(framer.Push("\n"u8, Add));
        Assert.Equal("e", lines[^1]);
    }

    [Fact]
    public void A_line_of_exactly_the_limit_is_fine_and_one_more_byte_is_not()
    {
        var count = 0;
        var framer = new DeskLineFramer();
        var line = new byte[DeskWire.MaxLine + 1];
        line.AsSpan().Fill((byte)'x');
        line[^1] = (byte)'\n';
        Assert.True(framer.Push(line, _ => count++));
        Assert.Equal(1, count);

        line[^1] = (byte)'x';
        Assert.False(new DeskLineFramer().Push(line, _ => count++));
    }
}

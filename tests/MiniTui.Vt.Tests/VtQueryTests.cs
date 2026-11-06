using System.Text;

namespace MiniTui.Vt.Tests;

public class VtQueryTests
{
    private sealed class Recorder : IVtHost
    {
        public StringBuilder Replies { get; } = new();
        public List<string> Clipboard { get; } = [];
        public List<(string? Title, string Body)> Notifications { get; } = [];
        public int Bells { get; set; }

        public void Reply(ReadOnlySpan<byte> bytes) => Replies.Append(Encoding.UTF8.GetString(bytes));
        public void SetClipboard(string text) => Clipboard.Add(text);
        public void Bell() => Bells++;
        public void Notify(string? title, string body) => Notifications.Add((title, body));
    }

    private static (VtScreen Screen, Recorder Host) New(int width = 80, int height = 24)
    {
        var host = new Recorder();
        return (new VtScreen(width, height) { Host = host }, host);
    }

    private static string ReplyTo(string sequence)
    {
        var (screen, host) = New();
        screen.Feed(Encoding.UTF8.GetBytes(sequence));
        return host.Replies.ToString();
    }

    [Theory]
    [InlineData("\e[c", "\e[?62;22c")]
    [InlineData("\e[0c", "\e[?62;22c")]
    [InlineData("\e[>c", "\e[>1;10;0c")]
    [InlineData("\e[>0c", "\e[>1;10;0c")]
    [InlineData("\e[5n", "\e[0n")]
    [InlineData("\e[6n", "\e[1;1R")]
    [InlineData("\e[3;7H\e[6n", "\e[3;7R")]
    [InlineData("\e[?u", "\e[?0u")]
    [InlineData("\e[>5u\e[?u", "\e[?5u")]
    public void Queries_get_their_replies(string query, string reply) => Assert.Equal(reply, ReplyTo(query));

    [Fact]
    public void Cursor_report_counts_from_the_scroll_region_in_origin_mode() =>
        Assert.Equal("\e[2;3R", ReplyTo("\e[5;10r\e[?6h\e[2;3H\e[6n"));

    [Theory]
    [InlineData("\e[?1$p", "\e[?1;2$y")]
    [InlineData("\e[?1h\e[?1$p", "\e[?1;1$y")]
    [InlineData("\e[?25$p", "\e[?25;1$y")]
    [InlineData("\e[?2004h\e[?2004$p", "\e[?2004;1$y")]
    [InlineData("\e[?2026$p", "\e[?2026;2$y")]
    [InlineData("\e[?2026h\e[?2026$p", "\e[?2026;1$y")]
    [InlineData("\e[?1049h\e[?1049$p", "\e[?1049;1$y")]
    [InlineData("\e[?1000h\e[?1000$p", "\e[?1000;1$y")]
    [InlineData("\e[?1000h\e[?1003$p", "\e[?1003;2$y")]
    [InlineData("\e[?1004h\e[?1004$p", "\e[?1004;1$y")]
    [InlineData("\e[?1006h\e[?1006$p", "\e[?1006;1$y")]
    [InlineData("\e[?3$p", "\e[?3;4$y")]
    [InlineData("\e[?9999$p", "\e[?9999;0$y")]
    [InlineData("\e[4h\e[4$p", "\e[4;1$y")]
    [InlineData("\e[20$p", "\e[20;2$y")]
    [InlineData("\e[99$p", "\e[99;0$y")]
    public void Modes_are_reported(string query, string reply) => Assert.Equal(reply, ReplyTo(query));

    [Fact]
    public void Colors_are_reported_with_the_terminator_they_came_with()
    {
        var (screen, host) = New();
        screen.DefaultForeground = Color.Rgb(0x11, 0x22, 0x33);
        screen.DefaultBackground = Color.Rgb(0xAA, 0xBB, 0xCC);
        screen.Feed("\e]10;?\e\\"u8);
        Assert.Equal("\e]10;rgb:1111/2222/3333\e\\", host.Replies.ToString());
        host.Replies.Clear();
        screen.Feed("\e]11;?\a"u8);
        Assert.Equal("\e]11;rgb:aaaa/bbbb/cccc\a", host.Replies.ToString());
    }

    [Fact]
    public void Colors_that_are_not_rgb_report_the_fallback()
    {
        var (screen, host) = New();
        screen.DefaultBackground = Color.Reset;
        screen.Feed("\e]11;?\a"u8);
        Assert.Equal("\e]11;rgb:0000/0000/0000\a", host.Replies.ToString());
    }

    [Fact]
    public void Setting_colors_does_nothing() => Assert.Equal("", ReplyTo("\e]10;#ff0000\a\e]11;rgb:00/00/00\a"));

    [Theory]
    [InlineData("\e[>0q")]        // XTVERSION
    [InlineData("\e[=c")]         // Tertiary attributes
    [InlineData("\e[14t")]        // Window size in pixels
    [InlineData("\eP$qm\e\\")]    // DECRQSS
    [InlineData("\eP+q544e\e\\")] // XTGETTCAP
    [InlineData("\e]4;1;?\a")]    // Palette
    [InlineData("\e[1n")]
    public void Anything_else_gets_no_reply(string query) => Assert.Equal("", ReplyTo(query));

    [Fact]
    public void Clipboard_writes_reach_the_host()
    {
        var (screen, host) = New();
        var text = Convert.ToBase64String(Encoding.UTF8.GetBytes("héllo 日本"));
        screen.Feed(Encoding.ASCII.GetBytes($"\e]52;c;{text}\a"));
        Assert.Equal(["héllo 日本"], host.Clipboard);
        Assert.Equal("", host.Replies.ToString());
    }

    [Fact]
    public void Clipboard_reads_are_refused_with_an_empty_reply()
    {
        var (screen, host) = New();
        screen.Feed("\e]52;c;aGVsbG8=\a\e]52;c;?\a"u8);
        Assert.Equal("\e]52;c;\a", host.Replies.ToString());
        Assert.DoesNotContain("aGVsbG8", host.Replies.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_clipboard_data_is_ignored()
    {
        var (screen, host) = New();
        screen.Feed("\e]52;c;!!!not base64\a\e]52;c\a\e]52;\a"u8);
        Assert.Empty(host.Clipboard);
    }

    [Fact]
    public void Notifications_reach_the_host()
    {
        var (screen, host) = New();
        screen.Feed("\e]9;build done\a\e]777;notify;CI;passed; all 3\e\\"u8);
        Assert.Equal([(null, "build done"), ("CI", "passed; all 3")], host.Notifications);
    }

    [Fact]
    public void Progress_and_other_uses_of_osc_9_are_not_notifications()
    {
        var (screen, host) = New();
        screen.Feed("\e]9;4;1;50\a\e]9;9;/home\a\e]777;other;x\a\e]9;\a"u8);
        Assert.Empty(host.Notifications);
    }

    [Fact]
    public void Notifications_have_control_characters_removed()
    {
        var (screen, host) = New();
        screen.Feed("\e]9;a\u0085b\a"u8);
        Assert.Equal([(null, "ab")], host.Notifications);
    }

    [Fact]
    public void Bell_reaches_the_host_but_not_when_it_ends_a_string()
    {
        var (screen, host) = New();
        screen.Feed("\a\e]0;title\a"u8);
        Assert.Equal(1, host.Bells);
    }

    [Fact]
    public void Queries_split_at_every_byte_get_the_same_reply()
    {
        const string input = "\e[6n\e[?2026$p\e]11;?\e\\\e]52;c;?\a\e[c";
        var expected = ReplyTo(input);
        var bytes = Encoding.UTF8.GetBytes(input);
        for (var split = 0; split <= bytes.Length; split++)
        {
            var (screen, host) = New();
            screen.Feed(bytes.AsSpan(0, split));
            screen.Feed(bytes.AsSpan(split));
            Assert.Equal(expected, host.Replies.ToString());
        }
    }

    [Fact]
    public void Without_a_host_queries_do_nothing()
    {
        var screen = new VtScreen(10, 2);
        screen.Feed("\e[c\e[6n\a\e]52;c;?\a\e]9;x\a"u8);
        Assert.Equal(" ", screen[0, 0].Symbol);
    }

    [Fact]
    public void Random_bytes_with_a_host_never_throw()
    {
        var random = new Random(7);
        var (screen, _) = New(20, 5);
        var buffer = new byte[4096];
        string[] prefixes = ["\e]52;", "\e]9;", "\e]777;notify;", "\e]10;", "\e[?", "\e[$", "\e[>"];
        for (var i = 0; i < 300; i++)
        {
            random.NextBytes(buffer);
            screen.Feed(Encoding.UTF8.GetBytes(prefixes[i % prefixes.Length]));
            screen.Feed(buffer);
        }
    }

    [Fact]
    public void A_full_reset_clears_focus_reporting_and_any_held_frame()
    {
        var (screen, _) = New();
        screen.Feed("\e[?1004h\e[?2026h"u8);
        Assert.True(screen.Modes.FocusReporting);
        Assert.True(screen.IsHolding);
        screen.Feed("\ec"u8);
        Assert.False(screen.Modes.FocusReporting);
        Assert.False(screen.IsHolding);
    }

    [Fact]
    public void A_held_frame_is_what_was_on_screen_when_it_began()
    {
        var (screen, _) = New(10, 2);
        screen.Feed("old"u8);
        screen.Feed("\e[?2026h\e[Hnew"u8);
        var buffer = new CellBuffer(10, 2);

        var cursor = screen.CopyTo(buffer, 0, 0, held: true);
        Assert.Equal("old", buffer.ToLines()[0].TrimEnd());
        Assert.Equal((3, 0), cursor);

        screen.CopyTo(buffer, 0, 0, held: false);
        Assert.Equal("new", buffer.ToLines()[0].TrimEnd());

        screen.Feed("\e[?2026l"u8);
        screen.CopyTo(buffer, 0, 0, held: true);
        Assert.Equal("new", buffer.ToLines()[0].TrimEnd());
    }

    [Fact]
    public void A_resize_lets_go_of_a_held_frame()
    {
        var (screen, _) = New(10, 2);
        screen.Feed("\e[?2026h"u8);
        screen.Resize(5, 2);
        Assert.False(screen.IsHolding);
        var buffer = new CellBuffer(5, 2);
        screen.CopyTo(buffer, 0, 0, held: true);
    }
}

namespace MiniTui.Desk.Reader.Tests;

/// <summary>Markdown as Reader formats it (spec §13.10): the rows shown, and the blocks the highlight moves by.</summary>
public sealed class MarkdownTests
{
    private static readonly PageStyles Styles = PageStyles.From(Theme.Dark);

    private static List<Block> Blocks(string markdown) => Markdown.Parse(markdown.Split('\n'));

    private static Page Laid(string markdown, int width = 40) => Page.Layout(Blocks(markdown), width, Styles);

    private static string Shown(string markdown, int width = 40)
    {
        var page = Laid(markdown, width);
        return string.Join('\n', page.Rows.Select(r => string.Concat(r.Spans.Select(s => s.Content)).TrimEnd()));
    }

    [Fact]
    public void Headings_lose_their_marks_and_the_first_two_levels_are_underlined()
    {
        Assert.Equal("Garden plan\n═══════════\n\nBeds\n────\n\nSoil", Shown("# Garden plan\n## Beds ##\n### Soil"));
        Assert.Equal("Plan\n════\n\nBeds\n────", Shown("Plan\n===\nBeds\n---"));
        Assert.Equal("#hashtag", Shown("#hashtag"));
    }

    [Fact]
    public void Paragraph_lines_join_and_wrap_and_two_spaces_or_a_backslash_break_them()
    {
        Assert.Equal("Tomatoes need six hours\nof sun.", Shown("Tomatoes need\nsix hours of sun.", width: 24));
        Assert.Equal("one\ntwo\nthree four", Shown("one  \ntwo\\\nthree\nfour"));
    }

    [Fact]
    public void A_word_wider_than_the_page_is_broken()
    {
        Assert.Equal("abcdefgh\nijkl", Shown("abcdefghijkl", width: 8));
    }

    [Fact]
    public void Emphasis_code_and_links_lose_their_marks_and_keep_their_styles()
    {
        var spans = Inline.Parse("a **bold** *it* ~~gone~~ `x*y` [guide](https://x.org) <https://y.org> ![cat](c.png)", default, Styles);
        Assert.Equal("a bold it gone x*y guide https://y.org [image: cat]", string.Concat(spans.Select(s => s.Content)));
        Assert.Equal(Modifiers.Bold, spans.Single(s => s.Content == "bold").Style.Add & Modifiers.Bold);
        Assert.Equal(Modifiers.Italic, spans.Single(s => s.Content == "it").Style.Add & Modifiers.Italic);
        Assert.Equal(Modifiers.CrossedOut, spans.Single(s => s.Content == "gone").Style.Add & Modifiers.CrossedOut);
        Assert.Equal(Styles.Code, spans.Single(s => s.Content == "x*y").Style);
        Assert.Equal(Styles.Link, spans.Single(s => s.Content == "guide").Style);
        Assert.Equal(Styles.Link, spans.Single(s => s.Content == "https://y.org").Style);
        Assert.Equal(Styles.Muted, spans.Single(s => s.Content == "[image: cat]").Style);
    }

    [Theory]
    [InlineData("snake_case_name", "snake_case_name")]
    [InlineData("2 * 3 * 4", "2 * 3 * 4")]
    [InlineData("**unclosed", "**unclosed")]
    [InlineData(@"\*not\* emphasis", "*not* emphasis")]
    [InlineData("[not a link]", "[not a link]")]
    [InlineData("`unclosed code", "`unclosed code")]
    [InlineData("***both***", "both")]
    [InlineData("*a **b** c*", "a b c")]
    public void Marks_with_nothing_to_close_them_are_text(string markdown, string shown) =>
        Assert.Equal(shown, string.Concat(Inline.Parse(markdown, default, Styles).Select(s => s.Content)));

    [Fact]
    public void Lists_get_bullets_numbers_and_boxes_and_wrapped_lines_go_under_the_text()
    {
        Assert.Equal(
            "• milk\n• eggs, the large\n  ones\n  • nested\n☐ open\n☑ done\n\n1. First\n2. Second",
            Shown("- milk\n* eggs, the large ones\n  - nested\n+ [ ] open\n- [x] done\n\n1. First\n2. Second", width: 17));
    }

    [Fact]
    public void A_list_item_runs_on_over_a_line_that_is_not_indented()
    {
        Assert.Equal("• one two\n• three", Shown("- one\ntwo\n- three"));
    }

    [Fact]
    public void Quotes_get_a_bar_per_level_and_blank_rows_between_their_blocks_keep_it()
    {
        Assert.Equal("│ A quote that\n│ wraps.\n│\n│ │ nested", Shown("> A quote that wraps.\n>\n> > nested", width: 14));
    }

    [Fact]
    public void Code_is_shown_as_written_indented_and_broken_at_the_edge()
    {
        Assert.Equal("  if (x)\n    y();\n\n  four spaces", Shown("```cs\nif (x)\n  y();\n```\n\n    four spaces"));
        Assert.Equal("  abcdef\n  gh", Shown("```\nabcdefgh\n```", width: 8));
        Assert.Equal("  # not a heading", Shown("~~~\n# not a heading"));
    }

    [Fact]
    public void Rules_tables_and_front_matter()
    {
        Assert.Equal("a\n\n──────────\n\nb", Shown("a\n\n---\n\nb", width: 10));
        Assert.Equal("| a | b |\n|---|---|", Shown("| a | b |\n|---|---|"));
        Assert.Equal("Text", Shown("---\ntitle: x\n---\nText"));
    }

    [Fact]
    public void Each_block_knows_its_lines_in_the_file_for_quoting()
    {
        var blocks = Blocks("# Title\n\nOne\ntwo\n\n- a\n  - b\n\n```\ncode\n```");
        Assert.Equal(
            [(BlockKind.Heading, 0, 0), (BlockKind.Paragraph, 2, 3), (BlockKind.Paragraph, 5, 5), (BlockKind.Paragraph, 6, 6), (BlockKind.Code, 8, 10)],
            blocks.Select(b => (b.Kind, b.FirstLine, b.LastLine)));
    }

    [Fact]
    public void Each_row_knows_its_block_and_blank_rows_belong_to_none()
    {
        var page = Laid("# Title\n\nOne\n\n- a\n- b");
        Assert.Equal([0, 0, -1, 1, -1, 2, 3], page.RowBlock);
        Assert.Equal((5, 5), page.BlockRows[2]);
        Assert.Equal("• a\n• b", page.Shown(2, 3));
    }

    [Fact]
    public void A_text_file_is_its_paragraphs_as_written()
    {
        var page = Page.Layout(Markdown.PlainText(["  indented # not markdown", "next line", "", "", "second paragraph"]), 40, Styles);
        Assert.Equal(["  indented # not markdown", "next line", "", "second paragraph"],
            page.Rows.Select(r => string.Concat(r.Spans.Select(s => s.Content))));
        Assert.Equal((0, 1), (Markdown.PlainText(["a", "b", "", "c"])[0].FirstLine, Markdown.PlainText(["a", "b", "", "c"])[0].LastLine));
    }

    [Fact]
    public void Text_lines_break_at_the_last_space_that_fits()
    {
        Assert.Equal(["one two", "three"], Wrap.Text("one two three", 9));
        Assert.Equal(["abcde", "fgh"], Wrap.Text("abcdefgh", 5));
        Assert.Equal(["日本", "語"], Wrap.Characters("日本語", 5));
    }

    [Fact]
    public void Nothing_in_the_file_is_lost()
    {
        // Odd input still shows every word
        var markdown = "<div>html</div>\n\n[ref]: https://x.org\n\n-\n\n1)\n\n>\n\n####### seven";
        var shown = Shown(markdown);
        foreach (var word in new[] { "<div>html</div>", "[ref]:", "#######", "seven" }) Assert.Contains(word, shown, StringComparison.Ordinal);
        Assert.Contains("•", shown, StringComparison.Ordinal);
    }
}

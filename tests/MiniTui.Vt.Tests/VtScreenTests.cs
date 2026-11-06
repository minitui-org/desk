using System.Text;

namespace MiniTui.Vt.Tests;

public sealed class VtScreenTests
{
    private static VtScreen Screen(string input, int width = 20, int height = 5)
    {
        var screen = new VtScreen(width, height);
        Feed(screen, input);
        return screen;
    }

    private static void Feed(VtScreen screen, string input) => screen.Feed(Encoding.UTF8.GetBytes(input));

    private static string Text(string input, int width = 20, int height = 5) =>
        ScreenAssert.Trimmed(Screen(input, width, height));

    // ----- Text and wrapping

    [Fact]
    public void Text_LandsAtTheCursor() => Assert.Equal("hello", Text("hello"));

    [Fact]
    public void CarriageReturnAndLineFeed_MoveSeparately() => Assert.Equal("cd\n  e", Text("ab\rcd\ne"));

    [Fact]
    public void Wrap_WaitsForTheNextCharacter()
    {
        var screen = Screen("abcde", width: 5);
        Assert.Equal((4, 0), screen.Cursor);
        Feed(screen, "f");
        Assert.Equal("abcde\nf", ScreenAssert.Trimmed(screen));
        Assert.Equal((1, 1), screen.Cursor);
    }

    [Fact]
    public void Wrap_AtTheBottomScrolls() => Assert.Equal("cd\nef", Text("abcdef", width: 2, height: 2));

    [Fact]
    public void AutoWrapOff_OverwritesTheLastColumn() => Assert.Equal("abcdg", Text("\u001b[?7labcdefg", width: 5));

    [Fact]
    public void CursorMovement_CancelsAPendingWrap()
    {
        var screen = Screen("abcde\u001b[2DX", width: 5);
        Assert.Equal("abXde", ScreenAssert.Trimmed(screen));
    }

    [Fact]
    public void Backspace_FromAPendingWrap_CountsFromTheLastColumn() =>
        Assert.Equal("abcXe", Text("abcde\bX", width: 5));

    [Fact]
    public void Erase_FromAPendingWrap_ErasesTheLastColumn() => Assert.Equal("abcd", Text("abcde\u001b[K", width: 5));

    [Fact]
    public void LineFeed_CancelsAPendingWrap()
    {
        var screen = Screen("abcde\nX", width: 5);
        Assert.Equal("abcde\n    X", ScreenAssert.Trimmed(screen));
    }

    // ----- Cursor

    [Theory]
    [InlineData("\u001b[3;4H", 3, 2)]
    [InlineData("\u001b[H", 0, 0)]
    [InlineData("\u001b[;4H", 3, 0)]
    [InlineData("\u001b[99;99H", 19, 4)]
    [InlineData("\u001b[0;0H", 0, 0)]
    [InlineData("\u001b[3;3H\u001b[A", 2, 1)]
    [InlineData("\u001b[3;3H\u001b[2B", 2, 4)]
    [InlineData("\u001b[3;3H\u001b[C", 3, 2)]
    [InlineData("\u001b[3;3H\u001b[0D", 1, 2)]
    [InlineData("\u001b[3;3H\u001b[E", 0, 3)]
    [InlineData("\u001b[3;3H\u001b[F", 0, 1)]
    [InlineData("\u001b[3;3H\u001b[5G", 4, 2)]
    [InlineData("\u001b[3;3H\u001b[5`", 4, 2)]
    [InlineData("\u001b[3;3H\u001b[2a", 4, 2)]
    [InlineData("\u001b[3;3H\u001b[4d", 2, 3)]
    [InlineData("\u001b[3;3H\u001b[e", 2, 3)]
    public void Cursor_Moves(string input, int x, int y) => Assert.Equal((x, y), Screen(input).Cursor);

    [Fact]
    public void Cursor_HiddenIsNull()
    {
        var screen = Screen("\u001b[?25l");
        Assert.Null(screen.Cursor);
        Feed(screen, "\u001b[?25h");
        Assert.NotNull(screen.Cursor);
    }

    [Fact]
    public void SavedCursor_KeepsPositionAndAttributes()
    {
        var screen = Screen("\u001b[3;4H\u001b[31m\u001b7\u001b[H\u001b[0m\u001b8X");
        Assert.Equal((4, 2), screen.Cursor);
        Assert.Equal(Color.DarkRed, screen[3, 2].Fg);
    }

    [Fact]
    public void SavedCursor_RestoresOriginMode_AsXtermDoes()
    {
        var screen = Screen("\u001b[2;4r\u001b[?6h\u001b7\u001b[?6l\u001b8\u001b[H");
        Assert.Equal((0, 1), screen.Cursor);
    }

    [Fact]
    public void SavedCursor_IsPerScreen_AsInXterm()
    {
        var screen = Screen("\u001b[3;3H\u001b7\u001b[?47h\u001b[2;2H\u001b8");
        Assert.Equal((0, 0), screen.Cursor);
    }

    [Fact]
    public void RestoringWithNothingSaved_GoesHome() => Assert.Equal((0, 0), Screen("\u001b[3;3H\u001b8").Cursor);

    // ----- Tabs

    [Fact]
    public void Tab_GoesToEveryEighthColumn() => Assert.Equal((16, 0), Screen("\t\t").Cursor);

    [Fact]
    public void Tab_StopsAtTheEdge() => Assert.Equal((19, 0), Screen("\t\t\t\t").Cursor);

    [Fact]
    public void TabStops_CanBeSetAndCleared()
    {
        Assert.Equal((3, 0), Screen("\u001b[4G\u001bH\r\t").Cursor);
        Assert.Equal((16, 0), Screen("\u001b[9G\u001b[g\r\t").Cursor);
        Assert.Equal((19, 0), Screen("\u001b[3g\t").Cursor);
    }

    [Fact]
    public void TabForwardAndBack_TakeACount()
    {
        Assert.Equal((16, 0), Screen("\u001b[2I").Cursor);
        Assert.Equal((16, 0), Screen("\u001b[19G\u001b[Z").Cursor);
        Assert.Equal((0, 0), Screen("\u001b[19G\u001b[9Z").Cursor);
    }

    // ----- Erase, insert and delete

    [Theory]
    [InlineData("\u001b[1;3H\u001b[K", "ab\nefgh\nijkl")]
    [InlineData("\u001b[1;3H\u001b[1K", "   d\nefgh\nijkl")]
    [InlineData("\u001b[1;3H\u001b[2K", "\nefgh\nijkl")]
    [InlineData("\u001b[2;3H\u001b[J", "abcd\nef")]
    [InlineData("\u001b[2;3H\u001b[1J", "\n   h\nijkl")]
    [InlineData("\u001b[2;3H\u001b[2J", "")]
    [InlineData("\u001b[2;3H\u001b[3J", "abcd\nefgh\nijkl")]
    [InlineData("\u001b[1;2H\u001b[2X", "a  d\nefgh\nijkl")]
    [InlineData("\u001b[1;2H\u001b[99X", "a\nefgh\nijkl")]
    [InlineData("\u001b[1;2H\u001b[@", "a bc\nefgh\nijkl")]
    [InlineData("\u001b[1;2H\u001b[99@", "a\nefgh\nijkl")]
    [InlineData("\u001b[1;2H\u001b[P", "acd\nefgh\nijkl")]
    [InlineData("\u001b[1;2H\u001b[99P", "a\nefgh\nijkl")]
    [InlineData("\u001b[2;2H\u001b[L", "abcd\n\nefgh")]
    [InlineData("\u001b[2;2H\u001b[M", "abcd\nijkl")]
    [InlineData("\u001b[S", "efgh\nijkl")]
    [InlineData("\u001b[T", "\nabcd\nefgh")]
    public void EditingFunctions(string input, string expected) =>
        Assert.Equal(expected, Text("abcd\r\nefgh\r\nijkl" + input, width: 4, height: 3));

    [Fact]
    public void GapInsertedAcrossMostOfALine_IsBlank_UnlikeTmux() =>
        Assert.Equal("                   a", Text("aaaaaa\r\u001b[19@", width: 20).Split('\n')[0]);

    [Fact]
    public void Erase_UsesTheCurrentBackground()
    {
        var screen = Screen("\u001b[41m\u001b[K");
        Assert.Equal(Color.DarkRed, screen[5, 0].Bg);
    }

    [Fact]
    public void InsertMode_PushesTextRight() => Assert.Equal("aXbc", Text("abc\r\u001b[C\u001b[4hX\u001b[4l"));

    [Fact]
    public void InsertMode_KeepsEveryCharacterAcrossAWrap_UnlikeTmux() =>
        Assert.Equal("abc\ndef", Text("\u001b[4habcdef", width: 3));

    [Fact]
    public void Repeat_RepeatsTheLastCharacter() => Assert.Equal("abbbb", Text("ab\u001b[3b"));

    [Fact]
    public void Repeat_WorksForAnyCharacterAndAfterOtherSequences_UnlikeTmux() =>
        Assert.Equal("ééé", Text("é\u001b[31m\u001b[2b"));

    [Fact]
    public void Repeat_WrapsLikePrinting_UnlikeTmux() => Assert.Equal("aaa\naa", Text("a\u001b[4b", width: 3));

    [Fact]
    public void Repeat_WithNothingPrinted_DoesNothing() => Assert.Equal("", Text("\u001b[5b"));

    [Fact]
    public void Repeat_IsLimitedToAScreenful()
    {
        var screen = new VtScreen(4, 2);
        Feed(screen, "a\u001b[65535b");
        Assert.Equal("aaaa\na", ScreenAssert.Trimmed(screen));
    }

    [Fact]
    public void AlignmentTest_FillsTheScreen() => Assert.Equal("EE\nEE", Text("ab\u001b#8", width: 2, height: 2));

    // ----- Scroll regions

    private const string Lines = "1\r\n2\r\n3\r\n4\r\n5";

    [Fact]
    public void Region_LineFeedAtItsBottomScrollsOnlyIt() =>
        Assert.Equal("1\n3\n4\n\n5", Text(Lines + "\u001b[2;4r\u001b[4;1H\n"));

    [Fact]
    public void Region_ReverseIndexAtItsTopScrollsOnlyIt() =>
        Assert.Equal("1\n\n2\n3\n5", Text(Lines + "\u001b[2;4r\u001b[2;1H\u001bM"));

    [Fact]
    public void Region_LineFeedBelowItDoesNotScroll() =>
        Assert.Equal("1\n2\n3\n4\n5", Text(Lines + "\u001b[2;3r\u001b[5;1H\n"));

    [Fact]
    public void Region_InsertAndDeleteLinesStayInIt()
    {
        Assert.Equal("1\n\n2\n3\n5", Text(Lines + "\u001b[2;4r\u001b[2;1H\u001b[L"));
        Assert.Equal("1\n3\n4\n\n5", Text(Lines + "\u001b[2;4r\u001b[2;1H\u001b[M"));
    }

    [Fact]
    public void Region_InsertAndDeleteLinesOutsideItAreIgnored_UnlikeTmux()
    {
        Assert.Equal("1\n2\n3\n4\n5", Text(Lines + "\u001b[2;3r\u001b[5;1H\u001b[L"));
        Assert.Equal("1\n2\n3\n4\n5", Text(Lines + "\u001b[2;3r\u001b[1;1H\u001b[M"));
    }

    [Fact]
    public void InsertAndDeleteLines_GoToColumnZero_UnlikeTmux()
    {
        Assert.Equal((0, 1), Screen("\u001b[2;5H\u001b[L").Cursor);
        Assert.Equal((0, 1), Screen("\u001b[2;5H\u001b[M").Cursor);
    }

    [Fact]
    public void Region_ScrollFunctionsStayInIt()
    {
        Assert.Equal("1\n3\n4\n\n5", Text(Lines + "\u001b[2;4r\u001b[S"));
        Assert.Equal("1\n\n2\n3\n5", Text(Lines + "\u001b[2;4r\u001b[T"));
    }

    [Fact]
    public void Region_ZeroMeansTheLastLine() =>
        Assert.Equal("1\n3\n4\n5", Text(Lines + "\u001b[2;0r\u001b[5;1H\n"));

    [Fact]
    public void Region_SettingItHomesTheCursor() => Assert.Equal((0, 0), Screen("\u001b[3;3H\u001b[2;4r").Cursor);

    [Fact]
    public void Region_ThatIsEmptyIsIgnored() => Assert.Equal((2, 2), Screen("\u001b[3;3H\u001b[4;2r").Cursor);

    [Fact]
    public void Region_CursorUpAndDownStopAtItsEdges()
    {
        Assert.Equal((0, 1), Screen("\u001b[2;4r\u001b[3;1H\u001b[9A").Cursor);
        Assert.Equal((0, 3), Screen("\u001b[2;4r\u001b[3;1H\u001b[9B").Cursor);
        Assert.Equal((0, 0), Screen("\u001b[2;4r\u001b[1;1H\u001b[9A").Cursor);
        Assert.Equal((0, 4), Screen("\u001b[2;4r\u001b[5;1H\u001b[9B").Cursor);
    }

    [Fact]
    public void OriginMode_CountsFromTheRegionAndStaysInIt()
    {
        Assert.Equal((0, 1), Screen("\u001b[2;4r\u001b[?6h").Cursor);
        Assert.Equal((1, 2), Screen("\u001b[2;4r\u001b[?6h\u001b[2;2H").Cursor);
        Assert.Equal((0, 3), Screen("\u001b[2;4r\u001b[?6h\u001b[9;1H").Cursor);
    }

    [Fact]
    public void OriginMode_SettingARegionGoesToItsFirstLine_UnlikeTmux() =>
        Assert.Equal((0, 2), Screen("\u001b[?6h\u001b[3;5r").Cursor);

    // ----- Wide characters and marks

    [Fact]
    public void Wide_TakesTwoColumns()
    {
        var screen = Screen("日x");
        Assert.Equal("日", screen[0, 0].Symbol);
        Assert.True(screen[1, 0].IsContinuation);
        Assert.Equal("x", screen[2, 0].Symbol);
        Assert.Equal((3, 0), screen.Cursor);
    }

    [Fact]
    public void Wide_ThatDoesNotFitWraps()
    {
        var screen = Screen("abc日", width: 4);
        Assert.Equal(" ", screen[3, 0].Symbol);
        Assert.Equal("日", screen[0, 1].Symbol);
    }

    [Fact]
    public void Wide_WithNoWrapAndNoRoomIsLost() => Assert.Equal("abc", Text("\u001b[?7labc日", width: 4));

    [Fact]
    public void Wide_InAOneColumnScreenBecomesASpace()
    {
        var screen = Screen("日", width: 1, height: 1);
        ScreenAssert.Consistent(screen);
        Assert.Equal(" ", screen[0, 0].Symbol);
    }

    [Theory]
    [InlineData("日本\u001b[1;1Hx", "x 本")]    // over the first half
    [InlineData("日本\u001b[1;2Hx", " x本")]    // over the second half
    [InlineData("日本\u001b[1;2H\u001b[K", "")]   // erased from the second half
    [InlineData("日本\u001b[1;2H\u001b[1X", "  本")]
    [InlineData("日本\u001b[1;2H\u001b[P", " 本")]
    [InlineData("日本\u001b[1;2H\u001b[@", "   本")]
    [InlineData("日本\u001b[1;3H\u001b[1P", "日")]
    public void Wide_CutInTwoLeavesNoHalf(string input, string expected)
    {
        var screen = Screen(input);
        ScreenAssert.Consistent(screen);
        Assert.Equal(expected, ScreenAssert.Trimmed(screen));
    }

    [Fact]
    public void Wide_PushedOffTheEdgeLeavesNoHalf()
    {
        var screen = Screen("abc日\u001b[1;1H\u001b[@", width: 5);
        ScreenAssert.Consistent(screen);
        Assert.Equal(" abc", ScreenAssert.Trimmed(screen));
    }

    [Fact]
    public void CombiningMark_JoinsTheCharacterBefore()
    {
        var screen = Screen("éx");
        Assert.Equal("é", screen[0, 0].Symbol);
        Assert.Equal("x", screen[1, 0].Symbol);
        Assert.Equal((2, 0), screen.Cursor);
    }

    [Fact]
    public void CombiningMark_JoinsAcrossAPendingWrap()
    {
        var screen = Screen("abé", width: 3);
        Assert.Equal("é", screen[2, 0].Symbol);
        Assert.Equal((2, 0), screen.Cursor);
    }

    [Fact]
    public void CombiningMark_WithNothingBeforeIsDropped() => Assert.Equal(" ", Screen("́")[0, 0].Symbol);

    [Fact]
    public void CombiningMark_AfterTheCursorMovedIsDropped() => Assert.Equal("a", Screen("a\u001b[3Ǵ")[0, 0].Symbol);

    [Fact]
    public void CombiningMarks_AreBounded()
    {
        var screen = Screen("a" + string.Concat(Enumerable.Repeat("́", 1000)));
        Assert.InRange(screen[0, 0].Symbol.Length, 1, 32);
    }

    [Fact]
    public void VariationSelector_WidensAndTakesTheNextColumn()
    {
        var screen = Screen("❤️x");
        Assert.Equal("❤️", screen[0, 0].Symbol);
        Assert.Equal(2, screen[0, 0].Width);
        Assert.True(screen[1, 0].IsContinuation);
        Assert.Equal("x", screen[2, 0].Symbol);
        ScreenAssert.Consistent(screen);
    }

    [Fact]
    public void VariationSelector_AtTheEdgeIsDropped()
    {
        var screen = Screen("ab❤️", width: 3);
        Assert.Equal("❤", screen[2, 0].Symbol);
        ScreenAssert.Consistent(screen);
    }

    [Theory]
    [InlineData("\U0001F468‍\U0001F469‍\U0001F467")]  // family, joined with ZWJ
    [InlineData("\U0001F44D\U0001F3FD")]                        // thumbs up, skin tone
    [InlineData("\U0001F1FA\U0001F1F8")]                        // flag: two regional indicators
    public void EmojiSequence_IsOneCell(string emoji)
    {
        var screen = Screen(emoji + "x");
        Assert.Equal(emoji, screen[0, 0].Symbol);
        Assert.True(screen[1, 0].IsContinuation);
        Assert.Equal("x", screen[2, 0].Symbol);
        ScreenAssert.Consistent(screen);
    }

    [Fact]
    public void ThreeRegionalIndicators_MakeAFlagAndOneLeftOver()
    {
        var screen = Screen("\U0001F1FA\U0001F1F8\U0001F1FA");
        Assert.Equal("\U0001F1FA\U0001F1F8", screen[0, 0].Symbol);
        Assert.Equal("\U0001F1FA", screen[2, 0].Symbol);
    }

    // ----- Styles

    [Theory]
    [InlineData("1", Modifiers.Bold)]
    [InlineData("2", Modifiers.Dim)]
    [InlineData("3", Modifiers.Italic)]
    [InlineData("4", Modifiers.Underlined)]
    [InlineData("4:3", Modifiers.Underlined)]
    [InlineData("21", Modifiers.Underlined)]
    [InlineData("7", Modifiers.Reversed)]
    [InlineData("9", Modifiers.CrossedOut)]
    [InlineData("1;3", Modifiers.Bold | Modifiers.Italic)]
    [InlineData("4;4:0", Modifiers.None)]
    [InlineData("1;2;22", Modifiers.None)]
    [InlineData("3;23", Modifiers.None)]
    [InlineData("4;24", Modifiers.None)]
    [InlineData("7;27", Modifiers.None)]
    [InlineData("9;29", Modifiers.None)]
    [InlineData("1;0", Modifiers.None)]
    [InlineData("1;", Modifiers.None)]
    [InlineData("5", Modifiers.None)]
    [InlineData("53", Modifiers.None)]
    [InlineData("999", Modifiers.None)]
    public void Modifiers_(string sgr, Modifiers expected) =>
        Assert.Equal(expected, Screen($"\u001b[{sgr}mx")[0, 0].Modifiers);

    public static TheoryData<string, Color> Foregrounds => new()
    {
        { "31", Color.DarkRed },
        { "37", Color.Gray },
        { "90", Color.DarkGray },
        { "97", Color.White },
        { "38;5;200", Color.Indexed(200) },
        { "38:5:200", Color.Indexed(200) },
        { "38;2;1;2;3", Color.Rgb(1, 2, 3) },
        { "38:2:1:2:3", Color.Rgb(1, 2, 3) },
        { "38:2::1:2:3", Color.Rgb(1, 2, 3) },
        { "31;39", Color.Reset },
        { "38;5;999", Color.Reset },
        { "38;2;999;2;3", Color.Reset },
        { "38;5", Color.Reset },
        { "38;2;1;2", Color.Reset },
        { "38;7", Color.Reset },
        { "38:9", Color.Reset },
    };

    [Theory]
    [MemberData(nameof(Foregrounds))]
    public void Foreground(string sgr, Color expected) => Assert.Equal(expected, Screen($"\u001b[{sgr}mx")[0, 0].Fg);

    [Theory]
    [InlineData("41", 1)]
    [InlineData("47", 7)]
    [InlineData("100", 8)]
    [InlineData("107", 15)]
    public void Background_Named(string sgr, int index)
    {
        var expected = Screen($"\u001b[{(index < 8 ? 30 + index : 82 + index)}mx")[0, 0].Fg;
        Assert.Equal(expected, Screen($"\u001b[{sgr}mx")[0, 0].Bg);
    }

    [Fact]
    public void Background_Extended()
    {
        Assert.Equal(Color.Indexed(9), Screen("\u001b[48;5;9mx")[0, 0].Bg);
        Assert.Equal(Color.Rgb(1, 2, 3), Screen("\u001b[48;2;1;2;3mx")[0, 0].Bg);
        Assert.Equal(Color.Reset, Screen("\u001b[41;49mx")[0, 0].Bg);
    }

    [Fact]
    public void ColorSequence_DoesNotEatWhatFollows()
    {
        var cell = Screen("\u001b[38;5;9;1;48:5:3;4mx")[0, 0];
        Assert.Equal(Color.Indexed(9), cell.Fg);
        Assert.Equal(Color.Indexed(3), cell.Bg);
        Assert.Equal(Modifiers.Bold | Modifiers.Underlined, cell.Modifiers);
    }

    [Fact]
    public void Concealed_ShowsSpaces_EvenForWideCharacters()
    {
        var screen = Screen("\u001b[8mab日\u001b[28mc");
        Assert.Equal("    c", ScreenAssert.Trimmed(screen));
        ScreenAssert.Consistent(screen);
    }

    [Fact]
    public void ReverseVideo_FlipsEveryCellAsItIsRead()
    {
        var screen = Screen("\u001b[7ma\u001b[0mb\u001b[?5h");
        Assert.Equal(Modifiers.None, screen[0, 0].Modifiers);
        Assert.Equal(Modifiers.Reversed, screen[1, 0].Modifiers);
        Feed(screen, "\u001b[?5l");
        Assert.Equal(Modifiers.Reversed, screen[0, 0].Modifiers);
    }

    // ----- Character sets

    [Fact]
    public void LineDrawing_ThroughG0() => Assert.Equal("┌─┐x", Text("\u001b(0lqk\u001b(Bx"));

    [Fact]
    public void LineDrawing_ThroughG1AndShiftOut() => Assert.Equal("q─q", Text("\u001b)0q\x0eq\x0fq"));

    [Fact]
    public void LineDrawing_CoversTheWholeSet() =>
        Assert.Equal("◆▒␉␌␍␊°±␤␋┘┐┌└┼⎺⎻─⎼⎽├┤┴┬│≤≥π≠£·", Text("\u001b(0`abcdefghijklmnopqrstuvwxyz{|}~", width: 40));

    [Fact]
    public void UkCharset_ChangesOnlyThePoundSign() => Assert.Equal("£a", Text("\u001b(A#a"));

    // ----- Alternate screen

    [Fact]
    public void AlternateScreen_1049_KeepsTheMainScreenAndCursor()
    {
        var screen = Screen("main\u001b[?1049h");
        Assert.True(screen.Modes.AlternateScreen);
        Assert.Equal("", ScreenAssert.Trimmed(screen));
        Feed(screen, "alt\u001b[?1049l");
        Assert.False(screen.Modes.AlternateScreen);
        Assert.Equal("main", ScreenAssert.Trimmed(screen));
        Assert.Equal((4, 0), screen.Cursor);
    }

    [Fact]
    public void AlternateScreen_1049_ClearsOnEntry()
    {
        var screen = Screen("\u001b[?1049hold\u001b[?1049l\u001b[?1049h");
        Assert.Equal("", ScreenAssert.Trimmed(screen));
    }

    [Fact]
    public void AlternateScreen_47_KeepsWhatWasThere()
    {
        var screen = Screen("\u001b[?47hold\u001b[?47l\u001b[?47h");
        Assert.Equal("old", ScreenAssert.Trimmed(screen));
    }

    [Fact]
    public void AlternateScreen_1047_ClearsOnLeaving()
    {
        var screen = Screen("\u001b[?1047hold\u001b[?1047l\u001b[?1047h");
        Assert.Equal("", ScreenAssert.Trimmed(screen));
    }

    [Fact]
    public void Mode1048_SavesTheCursor() => Assert.Equal((2, 2), Screen("\u001b[3;3H\u001b[?1048h\u001b[H\u001b[?1048l").Cursor);

    [Fact]
    public void AlternateScreen_RepeatedEntryKeepsTheFirstSave()
    {
        var screen = Screen("\u001b[3;3H\u001b[?1049h\u001b[H\u001b[?1049h\u001b[?1049l");
        Assert.Equal((2, 2), screen.Cursor);
    }

    // ----- Modes

    [Fact]
    public void Modes_FollowWhatTheProgramSets()
    {
        var screen = Screen("\u001b[?1h\u001b=\u001b[?2004h\u001b[?1002h\u001b[?1006h\u001b[?2026h");
        Assert.Equal(new VtModes(false, true, true, true, VtMouseMode.ButtonEvent, VtMouseEncoding.Sgr, 0, true, false), screen.Modes);
        Feed(screen, "\u001b[?1l\u001b>\u001b[?2004l\u001b[?1002l\u001b[?1006l\u001b[?2026l");
        Assert.Equal(default, screen.Modes);
    }

    [Theory]
    [InlineData(9, VtMouseMode.X10)]
    [InlineData(1000, VtMouseMode.Normal)]
    [InlineData(1002, VtMouseMode.ButtonEvent)]
    [InlineData(1003, VtMouseMode.AnyEvent)]
    public void Mouse_Modes(int mode, VtMouseMode expected)
    {
        var screen = Screen($"\u001b[?{mode}h");
        Assert.Equal(expected, screen.Modes.Mouse);
        Feed(screen, $"\u001b[?{mode}l");
        Assert.Equal(VtMouseMode.None, screen.Modes.Mouse);
    }

    [Fact]
    public void Mouse_ResettingAnotherModeLeavesItOn() =>
        Assert.Equal(VtMouseMode.AnyEvent, Screen("\u001b[?1003h\u001b[?1000l").Modes.Mouse);

    [Theory]
    [InlineData(1005, VtMouseEncoding.Utf8)]
    [InlineData(1006, VtMouseEncoding.Sgr)]
    [InlineData(1015, VtMouseEncoding.Urxvt)]
    public void Mouse_Encodings(int mode, VtMouseEncoding expected) =>
        Assert.Equal(expected, Screen($"\u001b[?{mode}h").Modes.MouseEncoding);

    [Fact]
    public void ManyModes_InOneSequence() =>
        Assert.Equal(new VtModes(false, true, false, true, VtMouseMode.None, VtMouseEncoding.Default, 0, false, false), Screen("\u001b[?1;2004h").Modes);

    [Fact]
    public void NewLineMode_LineFeedAlsoReturns() => Assert.Equal("a\nb", Text("\u001b[20ha\nb"));

    [Fact]
    public void KeyboardFlags_PushPopAndSet()
    {
        var screen = Screen("\u001b[>1u");
        Assert.Equal(1, screen.Modes.KeyboardFlags);
        Feed(screen, "\u001b[>3u");
        Assert.Equal(3, screen.Modes.KeyboardFlags);
        Feed(screen, "\u001b[<u");
        Assert.Equal(1, screen.Modes.KeyboardFlags);
        Feed(screen, "\u001b[=4;2u");
        Assert.Equal(5, screen.Modes.KeyboardFlags);
        Feed(screen, "\u001b[=1;3u");
        Assert.Equal(4, screen.Modes.KeyboardFlags);
        Feed(screen, "\u001b[=9u");
        Assert.Equal(9, screen.Modes.KeyboardFlags);
        Feed(screen, "\u001b[<9u");
        Assert.Equal(0, screen.Modes.KeyboardFlags);
    }

    [Fact]
    public void KeyboardFlags_AreKeptForEachScreen()
    {
        var screen = Screen("\u001b[>1u\u001b[?1049h");
        Assert.Equal(0, screen.Modes.KeyboardFlags);
        Feed(screen, "\u001b[>2u");
        Assert.Equal(2, screen.Modes.KeyboardFlags);
        Feed(screen, "\u001b[?1049l");
        Assert.Equal(1, screen.Modes.KeyboardFlags);
    }

    [Fact]
    public void KeyboardFlags_StackIsBounded()
    {
        var screen = new VtScreen(10, 2);
        for (var i = 0; i < 100; i++) Feed(screen, "\u001b[>1u");
        Feed(screen, "\u001b[<16u");
        Assert.Equal(0, screen.Modes.KeyboardFlags);
    }

    // ----- Title

    [Theory]
    [InlineData("\u001b]0;hello\x07", "hello")]
    [InlineData("\u001b]2;hello\u001b\\", "hello")]
    [InlineData("\u001b]2;日本語\x07", "日本語")]
    [InlineData("\u001b]2;a;b\x07", "a;b")]
    [InlineData("\u001b]2;x\x07\u001b]2;\x07", null)]
    [InlineData("\u001b]1;icon\x07", null)]
    [InlineData("\u001b]2;a\tb\x07", "ab")]
    public void Title_(string input, string? expected) => Assert.Equal(expected, Screen(input).Title);

    [Fact]
    public void OversizedOsc_IsDiscarded()
    {
        var screen = Screen("\u001b]2;" + new string('x', 2 << 20) + "\u0007after");
        Assert.Null(screen.Title);
        Assert.Equal("after", ScreenAssert.Trimmed(screen));
    }

    // ----- Parser

    [Theory]
    [InlineData("\u001bPignored\u001b\\x", "x")]
    [InlineData("\u001b_ignored\u001b\\x", "x")]
    [InlineData("\u001b^ignored\u001b\\x", "x")]
    [InlineData("\u001bXignored\u001b\\x", "x")]
    [InlineData("\u001b]999;ignored\x07x", "x")]
    [InlineData("\u001b[?999hx", "x")]
    [InlineData("\u001b[99999zx", "x")]
    [InlineData("\u001b[>4;2mx", "x")]
    [InlineData("\u001b[3 qx", "x")]
    [InlineData("\u001b[1;2;3;4;5;6;7;8;9;10;11;12;13;14;15;16;17;18;19;20;21;22;23;24;25;26;27;28;29;30;31;32;33;34;35;36;37mx", "x")]
    [InlineData("\u001b[1\x18x", "x")]
    [InlineData("\u001b[1\x1ax", "x")]
    [InlineData("\u001b]2;a\x18x", "x")]
    [InlineData("\u001b\u001bcx", "x")]
    [InlineData("\u001b#3x", "x")]
    [InlineData("\u001b[1?9zx", "x")]
    public void Sequences_ItDoesNotUnderstand_LeaveNoMark(string input, string expected) =>
        Assert.Equal(expected, Text(input));

    [Fact]
    public void ControlCharacter_InsideASequence_ActsAndTheSequenceContinues()
    {
        Assert.Equal("abX", Text("abc\u001b[\b1;3HX"));
        Assert.Equal("a\nb", Text("a\u001b[\n\r1Gb"));
    }

    [Fact]
    public void Osc_EndedByAnotherEscapeSequence_RunsIt()
    {
        var screen = Screen("\u001b]2;t\u001b[3;3H");
        Assert.Equal("t", screen.Title);
        Assert.Equal((2, 2), screen.Cursor);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF }, "�")]
    [InlineData(new byte[] { 0xC0, 0xAF }, "��")]
    [InlineData(new byte[] { 0xE2, 0x82 }, "")]                  // still waiting for the last byte
    [InlineData(new byte[] { 0xE2, 0x82, 0x41 }, "�A")]
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 }, "���")]
    [InlineData(new byte[] { 0xF4, 0x90, 0x80, 0x80 }, "����")]
    [InlineData(new byte[] { 0xC2, 0x9B, 0x41 }, "A")]           // a C1 control in UTF-8 is dropped
    public void Utf8_Malformed(byte[] input, string expected)
    {
        var screen = new VtScreen(20, 2);
        screen.Feed(input);
        Assert.Equal(expected, ScreenAssert.Trimmed(screen));
    }

    [Fact]
    public void Utf8_CutOffByAnEscape_StillRunsTheEscape()
    {
        var screen = new VtScreen(20, 2);
        screen.Feed([0xE2, 0x82, 0x1B, (byte)'[', (byte)'2', (byte)'G', (byte)'x']);
        Assert.Equal("�x", ScreenAssert.Trimmed(screen));
    }

    // ----- Resets

    [Fact]
    public void FullReset_ClearsScreenAndModes()
    {
        var screen = Screen("\u001b[31mx\u001b[?1h\u001b[>1u\u001b[2;3r\u001b[?1049h\u001b[?25l\u001bc");
        Assert.Equal("", ScreenAssert.Trimmed(screen));
        Assert.Equal(default, screen.Modes);
        Assert.Equal((0, 0), screen.Cursor);
        Feed(screen, "y");
        Assert.Equal(Color.Reset, screen[0, 0].Fg);
        Assert.Equal("", ScreenAssert.Trimmed(Reentered(screen)));

        static VtScreen Reentered(VtScreen s)
        {
            s.Feed("\u001b[?47h"u8);
            return s;
        }
    }

    [Fact]
    public void SoftReset_KeepsTheScreen()
    {
        var screen = Screen("x\u001b[31m\u001b[2;3r\u001b[?25l\u001b[4h\u001b[!p");
        Assert.Equal("x", ScreenAssert.Trimmed(screen));
        Assert.NotNull(screen.Cursor);
        Feed(screen, "\u001b[Hy");
        Assert.Equal(Color.Reset, screen[0, 0].Fg);
        Assert.Equal((1, 0), screen.Cursor);
    }

    // ----- Resize

    [Fact]
    public void Resize_ReportsTheNewSize()
    {
        var screen = new VtScreen(10, 5);
        screen.Resize(20, 3);
        Assert.Equal((20, 3), (screen.Width, screen.Height));
        ScreenAssert.Consistent(screen);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(5, 0)]
    [InlineData(-1, 5)]
    public void InvalidSizes_Throw(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VtScreen(width, height));
        var screen = new VtScreen(5, 5);
        Assert.Throws<ArgumentOutOfRangeException>(() => screen.Resize(width, height));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(5, 0)]
    [InlineData(0, 5)]
    public void Indexer_OffTheScreenThrows(int x, int y) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new VtScreen(5, 5)[x, y]);

    [Fact]
    public void Resize_Wider_KeepsTextAndDoesNotReflow()
    {
        var screen = Screen("abcdef", width: 3);
        screen.Resize(6, 5);
        Assert.Equal("abc\ndef", ScreenAssert.Trimmed(screen));
    }

    [Fact]
    public void Resize_Narrower_CutsLines()
    {
        var screen = Screen("abcdef");
        screen.Resize(3, 5);
        Assert.Equal("abc", ScreenAssert.Trimmed(screen));
        Assert.Equal((2, 0), screen.Cursor);
    }

    [Fact]
    public void Resize_Narrower_LeavesNoHalfOfAWideCharacter()
    {
        var screen = Screen("ab日");
        screen.Resize(3, 5);
        ScreenAssert.Consistent(screen);
        Assert.Equal("ab", ScreenAssert.Trimmed(screen));
    }

    [Fact]
    public void Resize_Shorter_DropsBlankLinesBelowTheCursorFirst()
    {
        var screen = Screen("a\r\nb");
        screen.Resize(20, 2);
        Assert.Equal("a\nb", ScreenAssert.Trimmed(screen));
        Assert.Equal((1, 1), screen.Cursor);
    }

    [Fact]
    public void Resize_Shorter_KeepsTheCursorOnScreen()
    {
        var screen = Screen("1\r\n2\r\n3\r\n4\r\n5");
        screen.Resize(20, 2);
        Assert.Equal("4\n5", ScreenAssert.Trimmed(screen));
        Assert.Equal((1, 1), screen.Cursor);
    }

    [Fact]
    public void Resize_Taller_AddsBlankLinesBelow()
    {
        var screen = Screen("a", height: 2);
        screen.Resize(20, 4);
        Assert.Equal("a", ScreenAssert.Trimmed(screen));
        Feed(screen, "\u001b[4;1Hz");
        Assert.Equal("a\n\n\nz", ScreenAssert.Trimmed(screen));
    }

    [Fact]
    public void Resize_ResetsTheScrollRegion()
    {
        var screen = Screen("\u001b[2;3r", height: 5);
        screen.Resize(20, 4);
        Feed(screen, "\u001b[4;1Ha\nb");
        Assert.Equal("\n\na\n b", ScreenAssert.Trimmed(screen));
    }

    [Fact]
    public void Resize_CancelsAPendingWrap()
    {
        var screen = Screen("abc", width: 3);
        screen.Resize(5, 5);
        Feed(screen, "d");
        Assert.Equal("abd", ScreenAssert.Trimmed(screen));
    }

    [Fact]
    public void Resize_ExtendsTabStops()
    {
        var screen = new VtScreen(8, 2);
        screen.Resize(20, 2);
        Feed(screen, "\t\t");
        Assert.Equal((16, 0), screen.Cursor);
    }

    [Fact]
    public void Resize_KeepsTheOtherScreenAndItsSavedCursor()
    {
        var screen = Screen("1\r\n2\r\n3\r\n4\r\n5\u001b[?1049halt");
        screen.Resize(20, 2);
        Feed(screen, "\u001b[?1049l");
        Assert.Equal("4\n5", ScreenAssert.Trimmed(screen));
        Assert.Equal((1, 1), screen.Cursor);
    }

    [Fact]
    public void Resize_AfterwardsAMarkDoesNotReachForAnOldCell()
    {
        var screen = Screen("abc", width: 3);
        screen.Resize(2, 2);
        Feed(screen, "́");
        ScreenAssert.Consistent(screen);
    }
}

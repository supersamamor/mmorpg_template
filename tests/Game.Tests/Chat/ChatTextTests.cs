using Game.Server.Chat;
using Game.Shared.Vocabulary;

namespace Game.Tests.Chat;

/// <summary>T-901's text rules (<see cref="ChatText"/>): what is removed, what is refused, and the tricks that must not survive.</summary>
public sealed class ChatTextTests
{
    private static CleanText Clean(string text, int max = 120, int marks = 3) => ChatText.Clean(text, max, marks);

    [Theory]
    [InlineData("  hello  ", "hello")]
    [InlineData("hello\nworld", "hello world")]
    [InlineData("hello\r\n\tworld", "hello world")]
    [InlineData("a    b", "a b")]
    [InlineData("a\u00A0\u2003\u3000b", "a b")]                 // other spaces become one plain space
    [InlineData("a\u2028b\u2029c", "a b c")]                    // line and paragraph separators
    [InlineData("a\u0007b\u001Bc", "a b c")]                     // control characters
    public void Whitespace_and_controls_become_one_space_and_the_ends_are_trimmed(string raw, string expected) => Clean(raw).Text.ShouldBe(expected);

    [Theory]
    [InlineData("evil\u202Etxt.exe", "eviltxt.exe")]             // right-to-left override
    [InlineData("\u202Bab\u202C", "ab")]                          // embedding and pop
    [InlineData("\u2067ab\u2069", "ab")]                          // isolate and pop
    [InlineData("a\u200Eb\u200Fc\u061Cd", "abcd")]               // the direction marks, Arabic letter mark included
    [InlineData("a\u200Bb\u200Cc\u200Dd\u2060e\uFEFFf", "abcdef")] // zero-width space, non-joiner, joiner, word joiner, BOM
    [InlineData("soft\u00ADhyphen", "softhyphen")]
    [InlineData("tag\U000E0041\U000E0042s", "tags")]             // invisible tag characters
    [InlineData("p\uE000ua", "pua")]                              // private use
    [InlineData("\u3164\u3164", null)]                            // Hangul fillers: draw nothing
    [InlineData("\u115F\u1160\uFFA0\u2800", null)]
    [InlineData("a\u034Fb", "ab")]                                // combining grapheme joiner
    public void Right_to_left_and_zero_width_tricks_are_removed(string raw, string? expected) => Clean(raw).Text.ShouldBe(expected);

    [Fact]
    public void Right_to_left_letters_stay_they_are_languages()
    {
        Clean("\u05E9\u05DC\u05D5\u05DD world").Text.ShouldBe("\u05E9\u05DC\u05D5\u05DD world");
        Clean("\u0645\u0631\u062D\u0628\u0627").Text.ShouldBe("\u0645\u0631\u062D\u0628\u0627");
    }

    [Fact]
    public void A_line_cannot_draw_itself_as_a_name_or_reorder_what_is_around_it()
    {
        // The classic: "Ada: hi" made to look as if another player said it, by a reversal around the colon. Without the controls it is plain text, and the client draws the
        // server's name apart from it.
        var text = Clean("\u202E:adA\u202C hi").Text!;
        text.ShouldBe(":adA hi");
        text.ShouldNotContain('\u202E');
        foreach (var c in text) char.GetUnicodeCategory(c).ShouldNotBe(System.Globalization.UnicodeCategory.Format);
    }

    [Fact]
    public void Combining_marks_are_kept_up_to_the_setting_after_each_letter_and_never_first()
    {
        var zalgo = "q" + new string('\u0301', 30) + "a" + new string('\u0336', 2);
        Clean(zalgo).Text.ShouldBe("q\u0301\u0301\u0301a\u0336\u0336");
        Clean(zalgo, marks: 0).Text.ShouldBe("qa");
        Clean("\u0301\u0301x").Text.ShouldBe("x", "a mark without a letter goes");
        Clean("x \u0301").Text.ShouldBe("x", "nor after a space");
        Clean("Ti\u1EBFng Vi\u1EC7t").Text.ShouldBe("Ti\u1EBFng Vi\u1EC7t".Normalize(System.Text.NormalizationForm.FormC));
    }

    [Fact]
    public void Text_is_normalised_so_what_looks_alike_is_alike() =>
        Clean("e\u0301").Text.ShouldBe("\u00E9");

    [Fact]
    public void Broken_surrogates_are_dropped_and_whole_ones_kept()
    {
        Clean("a\uD800b").Text.ShouldBe("ab");
        Clean("ok \U0001F600").Text.ShouldBe("ok \U0001F600");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\u200B\u202E\n\t")]
    public void Nothing_left_is_refused_as_empty(string raw) => Clean(raw).Refusal.ShouldBe(ChatReasons.Empty);

    [Fact]
    public void The_length_limit_is_on_the_cleaned_text_in_utf16_units_at_its_edges()
    {
        Clean(new string('a', 10), max: 10).Text.ShouldBe(new string('a', 10));
        Clean(new string('a', 11), max: 10).Refusal.ShouldBe(ChatReasons.TooLong);
        Clean("  " + new string('a', 10) + "\u200B\u200B  ", max: 10).Text.ShouldBe(new string('a', 10), "what is removed does not count");
        Clean(new string('a', 9) + "\U0001F600", max: 10).Refusal.ShouldBe(ChatReasons.TooLong, "a pair is two units");
        Clean(new string('a', 100_000), max: 10).Refusal.ShouldBe(ChatReasons.TooLong);
    }

    [Fact]
    public void Markup_is_text_the_client_never_parses_it() =>
        Clean("[color=red]Staff[/color] <b>x</b>").Text.ShouldBe("[color=red]Staff[/color] <b>x</b>");
}

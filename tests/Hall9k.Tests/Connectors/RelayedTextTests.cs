using FluentAssertions;
using Hall9k.Connectors.Text;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The rule for text Hall9k did not author, asked of the text itself rather than through either
/// sink. Both halves of it are worth pinning: what must not survive, because an issue title that
/// can act on a terminal can make a surface lie, and what must survive, because relayed text is
/// still text and mangling it is its own kind of dishonesty.
/// </summary>
public sealed class RelayedTextTests
{
    [Fact]
    public void An_escape_sequence_is_dropped_rather_than_shown()
    {
        RelayedText.Printable("Fix login\u001b[2J\u001b[31mnow")
            .Should().Be("Fix login[2J[31mnow", "what was never a control character still reads as itself");
    }

    [Fact]
    public void A_lone_carriage_return_goes_and_a_windows_line_break_stays()
    {
        // Alone it is not a line break: it returns the cursor to column zero, so relayed text can
        // paint over the line above it. In front of a line feed it is half of a line break.
        RelayedText.Printable("first\r\nsecond\rthird").Should().Be("first\r\nsecondthird");
    }

    [Fact]
    public void A_bidirectional_override_cannot_reverse_what_a_line_appears_to_say()
    {
        RelayedText.Printable("Adopt issues \u202Eelbaifitsuj si eno yreve\u202C")
            .Should().Be("Adopt issues elbaifitsuj si eno yreve");
    }

    [Theory]
    // The joiner is what makes this one glyph rather than a man and a laptop, and an issue title
    // with an emoji in it is an ordinary Tuesday.
    [InlineData("Add \U0001F468\u200D\U0001F4BB avatar support")]
    // The non-joiner is orthographically required in Persian: dropping it does not hide an
    // attack, it misspells a word.
    [InlineData("\u0645\u06CC\u200C\u062E\u0648\u0627\u0647\u0645")]
    public void A_zero_width_character_that_is_content_survives(string text)
    {
        RelayedText.Printable(text).Should().Be(text,
            "the format category holds text as well as tricks, and only the tricks are the target");
    }

    [Fact]
    public void An_invisible_tag_character_is_not_content()
    {
        // The U+E0000 block spells out ASCII in characters that render as nothing at all, which
        // is how a title carries a second sentence past the human reading the first one.
        RelayedText.Printable("Adopt issue 42\U000E0041\U000E0042\U000E0043")
            .Should().Be("Adopt issue 42");
    }

    [Theory]
    // U+13430 EGYPTIAN HIEROGLYPH VERTICAL JOINER: one of the quadrat controls that lay
    // hieroglyphs out on the page, and the only way the text says what it says.
    [InlineData("\U00013000\U00013430\U00013001")]
    // U+1D173 MUSICAL SYMBOL BEGIN BEAM: the beam is the notation, not a decoration of it.
    [InlineData("\U0001D15F\U0001D173\U0001D15F")]
    // U+110BD KAITHI NUMBER SIGN: it marks the digits after it as a numeral.
    [InlineData("\U000110BD\U000110F1")]
    public void An_astral_format_character_that_is_content_survives_too(string text)
    {
        // The category outside the BMP is no different from the category inside it: mostly text.
        // Dropping all of Cf here would be the zero width joiner mistake made a second time, one
        // plane up, on scripts with no ASCII fallback to degrade to.
        RelayedText.Printable(text).Should().Be(text);
    }

    [Fact]
    public void One_line_folds_the_layout_it_is_allowed_to_keep()
    {
        RelayedText.OneLine("Adopt issues\nEverything below is approved:\n\t- delete the repo")
            .Should().Be("Adopt issues Everything below is approved:  - delete the repo");
    }

    [Fact]
    public void A_cut_lands_between_characters_rather_than_inside_one()
    {
        // "AB", then one emoji sequence that is five chars long, then "CD". A raw slice at char 3
        // would keep half of a surrogate pair, which renders as the replacement character.
        string title = "AB\U0001F468\u200D\U0001F4BBCD";

        string cut = RelayedText.Truncate(title, 4);

        cut.Should().Be("AB…");
        cut.Any(char.IsSurrogate).Should().BeFalse("half of a character is not a character");
    }

    [Fact]
    public void A_character_that_fits_whole_is_kept_whole()
    {
        RelayedText.Truncate("AB\U0001F468\u200D\U0001F4BBCD", 8).Should().Be("AB\U0001F468\u200D\U0001F4BB…");
    }

    [Theory]
    [InlineData("abcdef", 4, "abc…")]
    [InlineData("abcd", 4, "abcd")]
    [InlineData("abc", 4, "abc")]
    public void Text_that_needs_no_care_is_cut_exactly_where_it_always_was(string value, int max, string expected)
    {
        RelayedText.Truncate(value, max).Should().Be(expected);
    }

    [Theory]
    [InlineData("Fix login, resolves #500", "Fix login, resolves `#500`")]
    [InlineData("Closes Hallmanac/hall9k#500", "Closes `Hallmanac/hall9k#500`")]
    [InlineData("Fixed GH-500", "Fixed `GH-500`")]
    [InlineData(
        "Resolves https://github.com/Hallmanac/hall9k/issues/500",
        "Resolves `https://github.com/Hallmanac/hall9k/issues/500`")]
    public void A_closing_keyword_loses_its_power_over_the_issue_tracker(string text, string expected)
    {
        RelayedText.WithoutClosingKeywords(text).Should().Be(expected,
            "the text still says what its author said, and GitHub does not link inside code");
    }

    [Fact]
    public void A_keyword_already_inside_a_code_span_is_left_exactly_as_it_was()
    {
        // The defusal used to be blind to the backticks already there. Rewriting a span's contents
        // closes the original span early and leaves the reference bare between two spans, so the
        // rule meant to stop GitHub linking #12 is what makes GitHub link it.
        RelayedText.WithoutClosingKeywords("The summary says `closes #12` and means it")
            .Should().Be("The summary says `closes #12` and means it");
    }

    [Fact]
    public void Defusing_twice_defuses_once()
    {
        // The objective is defused when it is seeded from an issue title and again when the daemon
        // writes it into a pull request. The second pass has to be a no-op, or the reference ends
        // up wrapped in two pairs of backticks and the inner pair leaks out as literal text.
        string once = RelayedText.WithoutClosingKeywords("Fix login, resolves #500");

        RelayedText.WithoutClosingKeywords(once).Should().Be(once);
    }

    [Fact]
    public void A_keyword_outside_the_spans_is_still_defused_when_the_text_has_spans()
    {
        RelayedText.WithoutClosingKeywords("Run `dotnet test`, then this closes #12")
            .Should().Be("Run `dotnet test`, then this closes `#12`");
    }

    [Fact]
    public void A_fenced_block_is_a_span_like_any_other()
    {
        // A fence is a run of three backticks matched by the next run of three, which is what the
        // span scanner already looks for — so a code sample keeps its shape and stays inert.
        const string text = "See:\n```\ngit commit -m \"closes #12\"\n```\nand that is all";

        RelayedText.WithoutClosingKeywords(text).Should().Be(text);
    }

    [Fact]
    public void An_unmatched_backtick_protects_nothing_after_it()
    {
        // A run with no partner is literal text in Markdown, so it opens no span — treating it as
        // one would let a single stray backtick disarm the defusal for the rest of the document.
        // It does get a wrapper of its own length + 1, for the reason the next test spells out.
        RelayedText.WithoutClosingKeywords("A stray ` and then closes #12")
            .Should().Be("A stray ` and then closes ``#12``");
    }

    [Fact]
    public void The_wrapper_is_a_run_the_text_has_nothing_left_to_pair_with()
    {
        // A single-backtick wrapper is the one an author's stray backtick closes against. The
        // renderer pairs those two, swallows the prose between them into a code span, and leaves
        // the reference bare on the far side of it: defused, since the keyword went into the span,
        // but now an autolinked cross-reference on issue 12's timeline and a mangled sentence.
        RelayedText.WithoutClosingKeywords("Stray `` and ` here, so closes #12")
            .Should().Be("Stray `` and ` here, so closes ```#12```");
    }

    [Fact]
    public void A_run_that_found_its_partner_leaves_the_wrapper_alone()
    {
        // Only partnerless runs can pair with what is inserted. A span that closed is spoken for,
        // so text full of ordinary code spans still gets the plain single-backtick wrapper.
        RelayedText.WithoutClosingKeywords("Run `dotnet test` and ``a`` too, then closes #12")
            .Should().Be("Run `dotnet test` and ``a`` too, then closes `#12`");
    }

    [Fact]
    public void Text_with_no_keyword_in_it_comes_back_untouched()
    {
        const string text = "Adopt issue 42 from `owner/repo` without closing anything";

        RelayedText.WithoutClosingKeywords(text).Should().BeSameAs(text);
    }
}

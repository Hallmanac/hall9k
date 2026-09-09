using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The mechanical half of the conventions (task 412afe6c): what it rewrites, what it refuses, and
/// the two things it deliberately leaves alone.
/// </summary>
public sealed class WritingConventionsCheckTests
{
    private static WritingConventionsVerdict Vet(string text) =>
        WritingConventionsCheck.Vet(text, WritingConventions.Default);

    [Fact]
    public void Prose_that_breaks_no_mechanical_rule_goes_through_untouched()
    {
        WritingConventionsVerdict verdict = Vet("The limiter resets per window; that is the contract.");

        verdict.Clean.Should().BeTrue();
        verdict.Refused.Should().BeFalse();
        verdict.Text.Should().Be("The limiter resets per window; that is the contract.");
    }

    [Fact]
    public void Blank_prose_is_nothing_to_check()
    {
        Vet(string.Empty).Clean.Should().BeTrue();
        WritingConventionsCheck.Vet(null, WritingConventions.Default).Clean.Should().BeTrue();
    }

    /// <summary>
    /// The three contexts, each named by what it replaces. A second independent clause takes a
    /// semicolon, an enumeration takes a colon, and anything else takes a comma, which is the one
    /// substitution that is never ungrammatical where an em dash set off a trailing phrase.
    /// </summary>
    [Theory]
    [InlineData(
        "The limiter resets per window — that is the contract.",
        "The limiter resets per window; that is the contract.")]
    [InlineData(
        "It touches three surfaces — the CLI, the daemon, and the domain.",
        "It touches three surfaces: the CLI, the daemon, and the domain.")]
    [InlineData(
        "The check runs before the post — deliberately.",
        "The check runs before the post, deliberately.")]
    [InlineData(
        "The note — in my judgment — is fine.",
        "The note, in my judgment, is fine.")]
    public void An_em_dash_is_rewritten_by_context(string written, string posted)
    {
        WritingConventionsVerdict verdict = Vet(written);

        verdict.Refused.Should().BeFalse();
        verdict.Text.Should().Be(posted);
        verdict.Rewrites.Should().ContainSingle().Which.Should().Contain("em dash");
    }

    /// <summary>
    /// An unspaced em dash still loses the space nobody typed on its left and gains the one every
    /// mark needs on its right, so the rewrite reads as prose rather than as a repaired string.
    /// </summary>
    [Fact]
    public void An_unspaced_em_dash_is_rewritten_with_the_spacing_the_mark_needs()
    {
        Vet("Two things—both of them small.").Text.Should().Be("Two things, both of them small.");
    }

    /// <summary>
    /// The attribution footer as it is actually written, markdown link and robot emoji included.
    /// Alone on its line, so dropping the line is the whole fix and nothing an author wrote is lost.
    /// </summary>
    [Fact]
    public void An_attribution_alone_on_its_line_is_dropped()
    {
        WritingConventionsVerdict verdict = Vet(
            "Every host uses the shared provider now.\n\n"
            + "\U0001F916 Generated with [Claude Code](https://claude.ai/code)");

        verdict.Refused.Should().BeFalse();
        verdict.Text.Should().Be("Every host uses the shared provider now.");
        verdict.Rewrites.Should().ContainSingle().Which.Should().Contain("attribution");
    }

    [Fact]
    public void A_co_authored_by_trailer_is_dropped()
    {
        Vet("Bound the limiter.\n\nCo-Authored-By: Claude <noreply@anthropic.com>")
            .Text.Should().Be("Bound the limiter.");
    }

    /// <summary>
    /// The one hit with no mechanical fix: deleting the line would delete the sentence, and leaving
    /// it would post the attribution, so the caller is told to post nothing. The refusal names the
    /// rule and quotes the line, which is what its log line needs.
    /// </summary>
    [Fact]
    public void An_attribution_welded_into_a_sentence_is_refused_rather_than_guessed_at()
    {
        WritingConventionsVerdict verdict = Vet(
            "This section was Generated with Claude and then rewritten against the spec by hand.");

        verdict.Refused.Should().BeTrue();
        verdict.Refusal.Should().Contain("AI-attribution rule")
            .And.Contain("This section was Generated with Claude");
    }

    /// <summary>
    /// The conventions are the operator's own sentence, so a project that dropped a rule from them
    /// gets its prose posted as its agent wrote it. Enforcing the platform's default rules over a
    /// text that never claimed them would rewrite prose somebody deliberately allowed.
    /// </summary>
    [Fact]
    public void Conventions_that_state_neither_checkable_rule_check_nothing()
    {
        WritingConventions permissive = WritingConventions.Parse("Write like a person. Keep it short.");

        WritingConventionsCheck.Vet("Two things — both of them small.", permissive)
            .Clean.Should().BeTrue();
    }

    /// <summary>
    /// A project that keeps the em-dash rule and drops the attribution one keeps exactly the half
    /// it kept: the probe is per rule, not per conventions text.
    /// </summary>
    [Fact]
    public void The_probe_is_per_rule_rather_than_all_or_nothing()
    {
        WritingConventions emDashOnly = WritingConventions.Parse("No em dashes. Everything else is yours.");

        WritingConventionsCheck.Vet("Two things — both small.\n\nCo-Authored-By: Someone", emDashOnly)
            .Text.Should().Be("Two things, both small.\n\nCo-Authored-By: Someone");
    }

    /// <summary>
    /// A fenced block is somebody's output, a diff, or a command line, and an em dash inside one is
    /// data. Rewriting it would corrupt the very thing the fence exists to reproduce verbatim.
    /// </summary>
    [Fact]
    public void A_fenced_block_is_left_exactly_as_written()
    {
        const string Text = "Here is the failing line:\n\n```\nassert x == \"a — b\"\n```\n\nIt fixes itself.";

        Vet(Text).Clean.Should().BeTrue();
    }

    /// <summary>
    /// The permissive direction of the same probe. A conventions text that mentions em dashes in
    /// order to allow them states no rule at all, and reading the mention itself as one would
    /// rewrite prose the operator deliberately permitted, which is the outcome the probe exists to
    /// avoid rather than one it is allowed to cause.
    /// </summary>
    [Fact]
    public void Conventions_that_permit_what_they_mention_check_nothing()
    {
        WritingConventions permissive = WritingConventions.Parse(
            "Em dashes are fine here; use them freely. Give attribution when quoting external sources.");

        WritingConventionsCheck.Vet("Two things \u2014 both of them small.", permissive).Clean.Should().BeTrue();
        WritingConventionsCheck.Vet("Bound the limiter.\n\nGenerated with Claude Code", permissive).Clean.Should().BeTrue();
    }

    /// <summary>A rule the operator worded their own way is still a rule, so long as it bans rather than mentions.</summary>
    [Theory]
    [InlineData("Avoid em dashes.")]
    [InlineData("Never use an em dash; a comma is almost always what you meant.")]
    [InlineData("- No em-dash anywhere\n- Full sentences")]
    public void A_rule_worded_as_a_prohibition_is_read_as_one(string conventions) =>
        WritingConventionsCheck.Vet("Two things \u2014 both small.", WritingConventions.Parse(conventions))
            .Text.Should().Be("Two things, both small.");

    /// <summary>
    /// The sentence-initial twin of the welded-in case. The line opens with the attribution and
    /// then keeps going in the author's own voice, so dropping it would delete their words exactly
    /// as the mid-sentence one would, and it takes the same refusal rather than a silent drop.
    /// </summary>
    [Fact]
    public void An_attribution_the_author_kept_writing_past_is_refused_rather_than_dropped()
    {
        WritingConventionsVerdict verdict = Vet(
            "Bound the limiter.\n\nGenerated with Claude and then rewritten against the spec by hand.");

        verdict.Refused.Should().BeTrue();
        verdict.Refusal.Should().Contain("AI-attribution rule").And.Contain("rewritten against the spec");
    }

    /// <summary>
    /// The other side of that bound: the footer shapes this platform actually sees still carry a
    /// tool name, a link, or a co-author's address after the phrase, and every one of them is still
    /// the attribution alone.
    /// </summary>
    [Theory]
    [InlineData("Co-Authored-By: Claude <noreply.com>")]
    [InlineData("Co-Authored-By: Brian Hall <brian@agelessrx.com>")]
    [InlineData("\U0001F916 Generated with Claude Code")]
    [InlineData("Generated with Claude Code (https://claude.ai/code)")]
    public void The_footer_shapes_this_platform_actually_sees_are_still_dropped(string footer) =>
        Vet($"Bound the limiter.\n\n{footer}").Text.Should().Be("Bound the limiter.");

    /// <summary>
    /// An inline code span is a fenced block in miniature: what is inside it is data somebody is
    /// quoting, and rewriting it would make the reply misquote the output it is discussing.
    /// </summary>
    [Fact]
    public void An_em_dash_inside_an_inline_code_span_is_data_too() =>
        Vet("The assertion reads `expected \u2014 got nothing` and never fires.").Clean.Should().BeTrue();

    /// <summary>The span is the exemption, not the line: prose around a quoted dash is still prose.</summary>
    [Fact]
    public void Prose_around_a_quoted_em_dash_is_still_rewritten() =>
        Vet("It fails once \u2014 `expected \u2014 got nothing` is the output.")
            .Text.Should().Be("It fails once, `expected \u2014 got nothing` is the output.");

    /// <summary>
    /// The two fences the first fence test did not draw: the tilde form, and a fence inside a
    /// blockquote, which is how a review reply carries somebody else's output most of the time.
    /// </summary>
    [Theory]
    [InlineData("Here is the failing line:\n\n~~~\nassert x == \"a \u2014 b\"\n~~~\n\nIt fixes itself.")]
    [InlineData("They quoted it:\n\n> ```\n> assert x == \"a \u2014 b\"\n> ```\n\nIt fixes itself.")]
    public void A_fence_drawn_the_other_ways_is_left_exactly_as_written(string text) =>
        Vet(text).Clean.Should().BeTrue();

    /// <summary>
    /// A rewrite touches the line it is on and no other. Trimming the whole text once took the
    /// indentation off its first line, which changes how markdown the check never flagged renders.
    /// </summary>
    [Fact]
    public void A_rewrite_leaves_the_indentation_of_lines_it_never_touched() =>
        Vet("    indented on purpose\n\nTwo things \u2014 both small.")
            .Text.Should().Be("    indented on purpose\n\nTwo things, both small.");

    /// <summary>
    /// The bound a word count alone never drew. Every one of these lines opens with the
    /// attribution and then says something of the author's own, in few enough words to fit under
    /// the cap, so a check that counted words would drop the line and their sentence with it. Each
    /// takes the refusal instead, and the refusal quotes what would otherwise have been deleted.
    /// </summary>
    [Theory]
    [InlineData("Generated with Claude Code. Reviewed manually.", "Reviewed manually")]
    [InlineData("Generated with Claude Code, checked by hand.", "checked by hand")]
    [InlineData("Generated with Claude Code And Rewritten Since", "Rewritten Since")]
    public void An_attribution_followed_by_the_authors_own_words_is_refused_however_few_they_are(
        string line, string deleted)
    {
        WritingConventionsVerdict verdict = Vet($"Bound the limiter.\n\n{line}");

        verdict.Refused.Should().BeTrue();
        verdict.Refusal.Should().Contain("AI-attribution rule").And.Contain(deleted);
    }

    /// <summary>
    /// The other side of that bound, again: a footer's own tail is a name, and a name comes
    /// through every one of the tighter reads unchanged. The second shape is the git trailer
    /// somebody typed without the angle brackets, whose address is still the footer's own
    /// furniture rather than three lower-case words of theirs.
    /// </summary>
    [Theory]
    [InlineData("Generated with Claude Code.")]
    [InlineData("Co-Authored-By: Brian Hall brian@agelessrx.com")]
    public void A_footer_that_carries_only_a_name_is_still_dropped(string footer) =>
        Vet($"Bound the limiter.\n\n{footer}").Text.Should().Be("Bound the limiter.");

    /// <summary>
    /// An attribution inside an inline code span is quoted rather than claimed, for the reason an
    /// em dash inside one is data. Every line here is prose about the footer: the first two
    /// discuss it, the third is the footer alone but in a span, which is how a reply shows the
    /// reader the exact string. Reading any of them as the real thing would refuse a reply written
    /// to explain the rule, or drop the quote out of one written to demonstrate it.
    /// </summary>
    [Theory]
    [InlineData("Never post `Co-Authored-By: Claude <noreply@anthropic.com>` in a commit here.")]
    [InlineData("The rule bans `Generated with [Claude Code](https://claude.ai/code)` outright.")]
    [InlineData("It is exactly this line:\n\n`\U0001F916 Generated with [Claude Code](https://claude.ai/code)`")]
    public void An_attribution_inside_an_inline_code_span_is_quoted_rather_than_claimed(string text) =>
        Vet(text).Clean.Should().BeTrue();

    /// <summary>
    /// The span is the exemption, not the text: a real footer further down is still dropped, and a
    /// line mixing a quoted attribution with an unquoted one is still the welded-in refusal.
    /// </summary>
    [Fact]
    public void A_real_attribution_is_still_caught_alongside_a_quoted_one()
    {
        Vet("Never post `Co-Authored-By: Claude` here.\n\nCo-Authored-By: Claude <noreply@anthropic.com>")
            .Text.Should().Be("Never post `Co-Authored-By: Claude` here.");

        Vet("This section was Generated with Claude, which `Generated with Claude` names.")
            .Refused.Should().BeTrue();
    }

    /// <summary>
    /// Two trailing spaces are a markdown hard line break, so they outlive a rewrite on their own
    /// line: trimming them would move the following line into the same rendered paragraph, which
    /// is a formatting change the em-dash rule never asked for and nobody reading the diff can see.
    /// </summary>
    [Fact]
    public void A_hard_line_break_survives_a_rewrite_on_its_own_line() =>
        Vet("Two things — both small.  \nAnd a second line.")
            .Text.Should().Be("Two things, both small.  \nAnd a second line.");

    /// <summary>
    /// The construct a fence exists for most of all: a longer fence quoting a shorter one, which is
    /// how a review reply shows somebody the markdown they should have written. Reading the inner
    /// fence as the outer one's close would vet the quoted lines as prose and rewrite the very data
    /// the quote reproduces, which is markdown's own rule the other way round: a closing fence is
    /// at least as long as its opener and carries no info string.
    /// </summary>
    [Fact]
    public void A_fence_quoting_a_shorter_fence_is_left_exactly_as_written() =>
        Vet("Write it this way:\n\n````markdown\n```bash\nh9k task add — with flags\n```\n````\n\nThat is all.")
            .Clean.Should().BeTrue();

    /// <summary>
    /// The other half of the same rule: a fence the text never closes is malformed markdown rather
    /// than a block, so the lines after it are prose and are checked. Reading it as a block open to
    /// the end of the text is what let an attribution footer, the one hit whose fix is completely
    /// mechanical, past the check entirely.
    /// </summary>
    [Fact]
    public void A_fence_that_never_closes_leaves_the_rest_of_the_text_checked() =>
        Vet("Here is the tail of it:\n\n```\nassert x == 1\n\n\U0001F916 Generated with Claude Code")
            .Text.Should().Be("Here is the tail of it:\n\n```\nassert x == 1");

    /// <summary>
    /// The other verbatim construct markdown has. Four spaces of indent after a blank line is a
    /// code block, and an em dash inside one is data for the reason a fenced one's is.
    /// </summary>
    [Fact]
    public void An_indented_code_block_is_left_exactly_as_written() =>
        Vet("The output reads:\n\n    expected — got nothing\n\nIt fixes itself.").Clean.Should().BeTrue();

    /// <summary>
    /// The bound on that exemption: indented code cannot interrupt a paragraph, so a wrapped line
    /// under a bullet is prose however far it is indented, and its em dash is still rewritten.
    /// </summary>
    [Fact]
    public void An_indented_line_carrying_on_a_paragraph_is_still_prose() =>
        Vet("- The limiter resets per window\n    and it holds — always.")
            .Text.Should().Be("- The limiter resets per window\n    and it holds, always.");

    /// <summary>
    /// A dash somebody drew a bullet with goes, and the indentation it was drawn at stays: that
    /// indentation is the item's place in its parent list, and dedenting the line to column zero
    /// takes the item out of that list in the rendered markdown.
    /// </summary>
    [Fact]
    public void A_hand_drawn_bullet_keeps_the_indentation_it_was_drawn_at() =>
        Vet("Two things:\n\n- one\n  — the nested one\n").Text.Should().Be("Two things:\n\n- one\n  the nested one");

    /// <summary>
    /// Two dashes with nothing but whitespace between them are one mark typed twice, not two marks
    /// bracketing an empty clause. Reading them as two wrote a second mark straight after the
    /// first, so "a——b" was posted as "a, , b".
    /// </summary>
    [Theory]
    [InlineData("It holds——always.", "It holds, always.")]
    [InlineData("It holds — — always.", "It holds, always.")]
    public void A_dash_somebody_typed_twice_is_one_mark(string written, string posted) =>
        Vet(written).Text.Should().Be(posted);

    /// <summary>
    /// The log line records what happened rather than what usually happens. A dash with no clause
    /// on one side of it leaves no mark behind, and counting it with the rewritten ones told the
    /// operator a comma had been written where nothing was (AGENTS.md: never guess at unobserved
    /// facts).
    /// </summary>
    [Fact]
    public void A_dash_that_left_no_mark_behind_is_not_reported_as_punctuation()
    {
        WritingConventionsVerdict verdict = Vet("It holds —\nAnd then it does not — it resets.");

        verdict.Text.Should().Be("It holds\nAnd then it does not; it resets.");
        verdict.Rewrites.Should().HaveCount(2);
        verdict.Rewrites.Should().Contain("1 em dash rewritten as a comma, a semicolon or a colon");
        verdict.Rewrites.Should().Contain("1 em dash removed with nothing in its place");
    }
}

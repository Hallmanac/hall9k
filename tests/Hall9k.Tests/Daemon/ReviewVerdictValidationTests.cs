using FluentAssertions;
using Hall9k.Daemon.Review;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// Whether a needs-fixes verdict's own output names something (task filed 2026-08-25, ten
/// occurrences): a location and a defect description, read the same way regardless of which
/// lens wrote it or whether it used the structured FINDING: header.
/// </summary>
public sealed class ReviewVerdictValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("VERDICT: needs-fixes")]
    [InlineData("Nothing survived my own reading of it.\n\nVERDICT: needs-fixes")]
    [InlineData("I found six verified findings, reported above.\n\nVERDICT: needs-fixes")]
    [InlineData("I checked the work against AGENTS.md and the acceptance criteria; the six findings "
        + "are reported above.\n\nVERDICT: needs-fixes")]
    [InlineData("Every criterion is met, and Program.cs proves it.\n\nVERDICT: needs-fixes")]
    public void An_output_that_names_no_location_or_no_defect_does_not_name_a_finding(string? output) =>
        ReviewVerdictValidation.NamesAFinding(output).Should().BeFalse();

    [Theory]
    [InlineData("1. `Auth.cs:42` — the limiter never resets.\n\nVERDICT: needs-fixes")]
    [InlineData("FINDING: severity=high; scope=in-scope; at=src/Auth.cs:42\n"
        + "Defect: the limiter never resets.\n\nVERDICT: needs-fixes")]
    [InlineData("The third acceptance criterion is not met — see Program.cs.\n\nVERDICT: needs-fixes")]
    [InlineData("1. `WorkItemContext.cs:18` — task text reaches the prompt unfenced. "
        + "Scenario: a crafted objective redirects the agent.\n\nVERDICT: needs-fixes")]
    [InlineData("FINDING: severity=high; scope=in-scope; at=Dockerfile:12\n"
        + "Defect: the base image is unpinned.\n\nVERDICT: needs-fixes")]
    [InlineData("1. `.gitignore` — the build output directory is never excluded.\n\nVERDICT: needs-fixes")]
    public void An_output_naming_a_location_and_a_defect_names_a_finding(string output) =>
        ReviewVerdictValidation.NamesAFinding(output).Should().BeTrue();

    /// <summary>
    /// The verdict line itself is set aside before the location scan, so a lens whose only
    /// mention of a path is the marker line's own boilerplate is not read as having named
    /// anything — the check is deliberately blind to the file the platform, not the reviewer,
    /// might one day quote there.
    /// </summary>
    [Fact]
    public void Only_the_verdict_line_is_excluded_not_the_rest_of_the_output()
    {
        ReviewVerdictValidation.NamesAFinding("The limiter never resets, per Program.cs.\n\nVERDICT: needs-fixes")
            .Should().BeTrue("the location and the defect both live outside the verdict line");
        ReviewVerdictValidation.NamesAFinding("Nothing survived verification.\n\nVERDICT: needs-fixes")
            .Should().BeFalse();
    }

    /// <summary>
    /// A short abbreviation like "e.g." must not be misread as a file reference: the extension
    /// has to run at least two letters, which "e.g." and "i.e." never do.
    /// </summary>
    [Fact]
    public void A_stray_abbreviation_is_not_mistaken_for_a_location()
    {
        ReviewVerdictValidation.NamesAFinding(
                "Everything reads cleanly, e.g. the naming is consistent throughout.\n\nVERDICT: needs-fixes")
            .Should().BeFalse();
    }

    /// <summary>
    /// A location by itself is not a defect description (task filed 2026-08-25): mentioning a
    /// file only to affirm compliance, or only as a doctrine citation, has not named anything
    /// wrong with it.
    /// </summary>
    [Fact]
    public void A_location_mentioned_only_in_passing_does_not_name_a_finding()
    {
        ReviewVerdictValidation.NamesAFinding("Every criterion is met, and Program.cs proves it.\n\nVERDICT: needs-fixes")
            .Should().BeFalse("the mention of Program.cs affirms compliance rather than naming a defect");
        ReviewVerdictValidation.NamesAFinding(
                "I checked the work against AGENTS.md and the acceptance criteria; the six findings "
                + "are reported above.\n\nVERDICT: needs-fixes")
            .Should().BeFalse("AGENTS.md is cited as doctrine, not as the site of a defect");
    }

    /// <summary>
    /// A location without an extension is still a location: Dockerfiles, Makefiles and dotfiles
    /// are real files a diff can touch, and the original dot-extension-only pattern could not
    /// match any of them (task filed 2026-08-25).
    /// </summary>
    [Theory]
    [InlineData("FINDING: severity=high; scope=in-scope; at=Dockerfile:12\n"
        + "Defect: the base image is unpinned.\n\nVERDICT: needs-fixes")]
    [InlineData("1. `Makefile:40` — the release target never cleans the build directory first.\n\nVERDICT: needs-fixes")]
    [InlineData("1. `Jenkinsfile:7` — the pipeline never checks out submodules.\n\nVERDICT: needs-fixes")]
    [InlineData("1. `.gitignore` — the build output directory is never excluded.\n\nVERDICT: needs-fixes")]
    public void An_extensionless_or_dotfile_location_still_names_a_finding(string output) =>
        ReviewVerdictValidation.NamesAFinding(output).Should().BeTrue();

    /// <summary>
    /// A location and defect language that live in different sentences of the same passage do
    /// not name a finding: an affirming sentence that happens to mention a file, followed by a
    /// separate sentence that happens to use a negation word, is the same "affirming review that
    /// still said needs-fixes" shape as <see cref="A_location_mentioned_only_in_passing_does_not_name_a_finding"/>,
    /// just spread across two sentences instead of one (found by the pre-PR review pass that
    /// hardened this check: a whole-body keyword scan cannot tell "X is wrong" from "nothing
    /// about X is wrong" once the two words fall in unrelated sentences).
    /// </summary>
    [Fact]
    public void A_location_and_defect_language_in_different_sentences_do_not_name_a_finding()
    {
        ReviewVerdictValidation.NamesAFinding(
                "Every criterion is met, and Program.cs proves it. Nothing here is wrong.\n\nVERDICT: needs-fixes")
            .Should().BeFalse("Program.cs is named in the affirming sentence, not the one using \"wrong\"");
    }

    /// <summary>
    /// The dotfile alternative is a fixed name list, not "a dot followed by letters": that
    /// generic shape also matched an ellipsis running into the next word, or ordinary prose
    /// like ".NET", and let either stand in for a real location (found by the pre-PR review
    /// pass that hardened this check).
    /// </summary>
    [Theory]
    [InlineData("I hunted hard...nothing here is wrong.\n\nVERDICT: needs-fixes")]
    [InlineData("This project targets .NET and nothing here is wrong.\n\nVERDICT: needs-fixes")]
    [InlineData("Call .Where and nothing here is wrong.\n\nVERDICT: needs-fixes")]
    public void A_dot_that_is_not_a_recognized_dotfile_does_not_name_a_location(string output) =>
        ReviewVerdictValidation.NamesAFinding(output).Should().BeFalse();

    /// <summary>
    /// A structured `FINDING:` block is trusted on <see cref="ReviewResultParser.ParseFindings"/>'s
    /// own read of its `at=` tag, not re-derived from the sentence-proximity heuristic: a header
    /// separated from its `Defect:`/`Scenario:` labels by a blank line (cycle-3 adversarial
    /// finding, `ReviewVerdictValidation.cs:116`), and a header whose body is plain prose with no
    /// negation word at all, both name a finding that the old paragraph/sentence scan discarded.
    /// </summary>
    [Theory]
    [InlineData("FINDING: severity=high; scope=in-scope; at=src/Foo.cs:12\n"
        + "\n"
        + "Defect: The cancellation token is dropped when the loop exits early.\n"
        + "\n"
        + "Scenario: A stopping daemon leaves the sweep running.\n\nVERDICT: needs-fixes")]
    [InlineData("FINDING: severity=high; scope=in-scope; at=src/Auth.cs:42\n"
        + "The limiter resets only on the happy path, so a rejected request keeps its slot forever."
        + "\n\nVERDICT: needs-fixes")]
    public void A_structured_finding_block_names_a_finding_from_its_own_location_tag(string output) =>
        ReviewVerdictValidation.NamesAFinding(output).Should().BeTrue();

    /// <summary>
    /// A structured `FINDING:` header with a readable `at=` tag and nothing else is a location,
    /// not a finding (cycle-5 adversarial finding, `ReviewVerdictValidation.cs:155`): a resumed
    /// session that echoes <c>AppendFindingContract</c>'s own reprompt template verbatim produces
    /// exactly this shape, and trusting the header tag alone would record that echo as a real
    /// needs-fixes and dispatch a fix session against a path that names nothing wrong.
    /// </summary>
    [Fact]
    public void A_structured_header_with_no_body_does_not_name_a_finding()
    {
        ReviewVerdictValidation.NamesAFinding(
                "FINDING: severity=high; scope=in-scope; at=src/Some/File.cs:123\n\nVERDICT: needs-fixes")
            .Should().BeFalse();
    }

    /// <summary>
    /// A structured `FINDING:` header followed by the finding contract's own worked example,
    /// quoted verbatim rather than answered, is the header-with-no-body shape wearing the
    /// example's text as a disguise (cycle-7 conformance finding, `ReviewVerdictValidation.cs:202`):
    /// the reprompt this class shares with <c>ReviewEngine.RepromptForVerdictAsync</c> reprints
    /// <c>AgentPromptBuilder.AppendFindingContract</c> in full, so a resumed session that quotes
    /// its own instructions before answering reproduces the example's `Defect:`/`Scenario:` lines
    /// exactly, and a bare non-blank-body check would read that echo as a real finding.
    /// </summary>
    [Fact]
    public void A_structured_header_followed_by_the_finding_contracts_own_example_does_not_name_a_finding()
    {
        ReviewVerdictValidation.NamesAFinding(
                "FINDING: severity=high; scope=in-scope; at=src/Some/File.cs:123\n"
                + "    Defect: one sentence saying what is wrong.\n"
                + "    Scenario: the input or state that makes it misbehave, and what goes wrong.\n"
                + "\nVERDICT: needs-fixes")
            .Should().BeFalse();
    }

    /// <summary>
    /// A structured `FINDING:` header followed by the worked example and then still more of the
    /// finding contract's own prose — not just the fixed `Defect:`/`Scenario:` pair — is the same
    /// echo wearing a longer disguise (cycle-8 conformance finding,
    /// `ReviewVerdictValidation.cs:268`): <c>ParseFindings</c> runs the block from the header to
    /// the next `FINDING:` or `VERDICT:` marker regardless of blank lines, so a session that
    /// quotes further into `AppendFindingContract`'s severity guidance before answering produces a
    /// block whose body is no longer an exact match for the two-line example, and an exact-match
    /// check alone would read the extra echoed prose as a real finding's body. The location tag
    /// is still the contract's own placeholder path, `src/Some/File.cs`, which no genuine finding
    /// is ever placed at.
    /// </summary>
    [Fact]
    public void A_structured_header_followed_by_the_example_and_more_echoed_contract_prose_does_not_name_a_finding()
    {
        ReviewVerdictValidation.NamesAFinding(
                "FINDING: severity=high; scope=in-scope; at=src/Some/File.cs:123\n"
                + "Defect: one sentence saying what is wrong.\n"
                + "Scenario: the input or state that makes it misbehave, and what goes wrong.\n"
                + "\n"
                + "**severity** — grade against these anchors, not against your own sense of importance:\n"
                + "\nVERDICT: needs-fixes")
            .Should().BeFalse();
    }

    /// <summary>
    /// Quoting the review mechanics' own bullet about how to cite a location — which puts the
    /// placeholder path `path/to/file.cs:123` and the word "defect" in one sentence — must not be
    /// read as a real finding just because it satisfies the prose heuristic's location-plus-defect-
    /// language shape (cycle-8 conformance finding, `ReviewVerdictValidation.cs:268`): the path is
    /// `AppendReviewMechanics`' own instructional placeholder, not anywhere in a real repository.
    /// </summary>
    [Fact]
    public void Quoting_the_review_mechanics_bullet_about_locations_does_not_name_a_finding()
    {
        ReviewVerdictValidation.NamesAFinding(
                "- Each finding must carry: the file and line (`path/to/file.cs:123`), a one-sentence\n"
                + "  statement of the defect, and a concrete failure scenario.\n"
                + "\nVERDICT: needs-fixes")
            .Should().BeFalse();
    }

    /// <summary>
    /// Plain prose (no `FINDING:` header) that puts the location in one sentence and the defect
    /// in the very next one, which visibly continues it, still names a finding (cycle-3
    /// conformance finding #1, `ReviewVerdictValidation.cs:119`) — the same-sentence-only rule
    /// rejected this shape and would park a human over a correctly located, correctly described
    /// defect.
    /// </summary>
    [Fact]
    public void Prose_with_the_defect_in_the_next_sentence_still_names_a_finding()
    {
        ReviewVerdictValidation.NamesAFinding(
                "`Auth.cs:42` is where the rate limiter lives. It is never reset after a rejected "
                + "request, so a client keeps its slot forever.\n\nVERDICT: needs-fixes")
            .Should().BeTrue();
    }

    /// <summary>
    /// The affirming-review shape stays rejected even though it has the same surface structure as
    /// <see cref="Prose_with_the_defect_in_the_next_sentence_still_names_a_finding"/> (a location
    /// sentence followed by one carrying defect vocabulary): "Nothing" opens an unrelated claim
    /// rather than continuing what the location's own sentence said, so it must not borrow the
    /// defect language meant for a different subject.
    /// </summary>
    [Fact]
    public void A_defect_word_in_an_unrelated_following_sentence_still_does_not_name_a_finding()
    {
        ReviewVerdictValidation.NamesAFinding(
                "Every criterion is met, and Program.cs proves it. Nothing here is wrong.\n\nVERDICT: needs-fixes")
            .Should().BeFalse();
    }

    /// <summary>
    /// A structured `FINDING:` header whose tags the parser cannot read — an unrecognized
    /// separator, or a header that carries no location tag at all — is not "no finding": the
    /// prose heuristic still runs over the whole output, header line included, so a location
    /// sitting in that text is not lost just because <see cref="ReviewResultParser.ParseFindings"/>
    /// came back with a blank <see cref="ReviewFinding.Location"/> (cycle-4 adversarial finding,
    /// `ReviewVerdictValidation.cs:144`; cycle-4 conformance finding #2).
    /// </summary>
    [Theory]
    [InlineData("FINDING: severity=high scope=in-scope at=src/Hall9k.Daemon/Review/ReviewEngine.cs:612\n"
        + "Defect: the session is never disposed, so the run leaks a handle.\n"
        + "Scenario: a long-lived daemon accumulates leaked handles across runs.\n\nVERDICT: needs-fixes")]
    [InlineData("FINDING: severity=high; scope=in-scope\n"
        + "Defect: `src/Auth.cs:42` never resets the limiter after a rejected request.\n\nVERDICT: needs-fixes")]
    public void A_structured_finding_whose_header_tag_the_parser_cannot_read_still_falls_back_to_prose(string output) =>
        ReviewVerdictValidation.NamesAFinding(output).Should().BeTrue();

    /// <summary>
    /// A defect stated in the affirmative, with no negation word at all, still names a finding
    /// (cycle-4 conformance finding #3, `ReviewVerdictValidation.cs:58`): the original vocabulary
    /// was negation-only, so ordinary defect prose like "is dropped" or "is overwritten" was
    /// rejected even though it names a real location and a real defect.
    /// </summary>
    [Theory]
    [InlineData("1. `src/Foo.cs:42` — the cancellation token is dropped when the loop exits early.\n\nVERDICT: needs-fixes")]
    [InlineData("1. `src/Foo.cs:10` — the manifest is overwritten before hashing.\n\nVERDICT: needs-fixes")]
    [InlineData("1. `src/Foo.cs:20` — this double-counts the retry budget.\n\nVERDICT: needs-fixes")]
    public void An_affirmative_defect_description_with_no_negation_word_still_names_a_finding(string output) =>
        ReviewVerdictValidation.NamesAFinding(output).Should().BeTrue();

    /// <summary>
    /// Plain prose that states the defect first and points at the location second, in the
    /// sentence right after it, still names a finding (cycle-6 finding,
    /// `ReviewVerdictValidation.cs:612`): the location's own sentence ("See
    /// ReviewEngine.cs:612.") is nothing but a backward pointer and carries no defect language of
    /// its own, so <see cref="ReviewVerdictValidation"/> has to look at the sentence before it
    /// rather than the sentence after, which is all the forward-only continuation check could
    /// ever see.
    /// </summary>
    [Fact]
    public void Prose_with_the_defect_in_the_previous_sentence_still_names_a_finding()
    {
        ReviewVerdictValidation.NamesAFinding(
                "The token is dropped when the loop exits early. See ReviewEngine.cs:612.\n\nVERDICT: needs-fixes")
            .Should().BeTrue();
    }

    /// <summary>
    /// The backward-pointer guard is asymmetric on purpose: it only ever looks backward from a
    /// sentence that is itself nothing but a pointer ("See", "This is at", "It is in"). An
    /// affirming sentence like "Every criterion is met, and Program.cs proves it." does not
    /// open with one of those words, so it must not borrow defect language from whatever
    /// happened to precede it in the output (cycle-6 finding, `ReviewVerdictValidation.cs:612`).
    /// </summary>
    [Fact]
    public void An_affirming_sentence_does_not_borrow_defect_language_from_a_preceding_sentence()
    {
        ReviewVerdictValidation.NamesAFinding(
                "Nothing here is wrong. Every criterion is met, and Program.cs proves it.\n\nVERDICT: needs-fixes")
            .Should().BeFalse();
    }

    /// <summary>
    /// "Here" opening a sentence that merely summarizes rather than pointing at a defect must not
    /// borrow defect language from an unrelated preceding sentence (cycle-7 conformance finding,
    /// `ReviewVerdictValidation.cs:126`): "Nothing I checked failed." states no defect about the
    /// location that follows it, and treating every "Here"-opener as a defect-free pointer wrongly
    /// let this plainly affirming verdict borrow "failed" from the sentence before it.
    /// </summary>
    [Fact]
    public void An_affirming_summary_sentence_opening_with_here_does_not_borrow_defect_language()
    {
        ReviewVerdictValidation.NamesAFinding(
                "Nothing I checked failed. Here is what I read: `src/Hall9k.Daemon/Review/ReviewEngine.cs`."
                + "\n\nVERDICT: needs-fixes")
            .Should().BeFalse();
    }

    /// <summary>
    /// CRLF output normalizes the same way <see cref="ReviewResultParser.ParseFindings"/> already
    /// does for this exact data (cycle-3 finding, `ReviewVerdictValidation.cs:74`): without it, a
    /// stray `\r` between the two `\n`s defeats <c>ParagraphBoundary</c>, the whole body collapses
    /// into one paragraph, and a hollow verdict with no location in its own paragraph is wrongly
    /// accepted as naming one.
    /// </summary>
    [Fact]
    public void A_crlf_authored_hollow_verdict_still_does_not_name_a_finding()
    {
        ReviewVerdictValidation.NamesAFinding(
                "I re-read Program.cs and the acceptance criteria.\r\n\r\nScenario: I could not "
                + "construct one for any of the six findings I reported above.\r\n\r\nVERDICT: needs-fixes")
            .Should().BeFalse();
    }
}

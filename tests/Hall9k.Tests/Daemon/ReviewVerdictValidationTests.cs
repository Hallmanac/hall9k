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
    [InlineData("1. `.github/workflows/ci.yml:22` — the release job never sets a timeout.\n\nVERDICT: needs-fixes")]
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
    /// A structured `FINDING:` header carrying a REAL (non-placeholder) location, followed by
    /// the finding contract's own worked example quoted verbatim, still does not name a finding
    /// (adversarial cycle-1 finding, `ReviewVerdictValidation.cs:208`): a resumed session that
    /// half-fills the reprompt's template — genuinely locating the header, but leaving
    /// `Defect:`/`Scenario:` as the literal example text — is the same echo as the placeholder-path
    /// case above, and a real path was never a placeholder <see cref="StripPlaceholderLocations"/>
    /// would strip, so it has to be caught on the body being the unanswered example instead. Before
    /// this was fixed, the paragraph's own structural markers and the example body's own defect
    /// vocabulary ("what is wrong", twice) both independently read this as naming a finding at
    /// `ReviewEngine.cs:612`, even though nothing was ever said about that line.
    /// </summary>
    [Fact]
    public void A_structured_header_with_a_real_location_followed_by_the_finding_contracts_own_example_does_not_name_a_finding()
    {
        ReviewVerdictValidation.NamesAFinding(
                "FINDING: severity=high; scope=in-scope; at=src/Hall9k.Daemon/Review/ReviewEngine.cs:612\n"
                + "Defect: one sentence saying what is wrong.\n"
                + "Scenario: the input or state that makes it misbehave, and what goes wrong.\n"
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
    /// A bare pointer sentence does not borrow defect language from a preceding sentence that
    /// denies a defect rather than asserting one (cycle-11 adversarial finding,
    /// `ReviewVerdictValidation.cs:810`): "no defect stands" and "nothing here is wrong" carry
    /// the same denial idiom the forward-looking branches already screen for, and the
    /// backward-pointer branch had no equivalent screen, so a hollow verdict that denies a defect
    /// and then merely points at a location was credited as naming one.
    /// </summary>
    [Theory]
    [InlineData("I checked every branch and no defect stands. See ReviewEngine.cs:612.\n\nVERDICT: needs-fixes")]
    [InlineData("Nothing here is wrong. See ReviewEngine.cs:612.\n\nVERDICT: needs-fixes")]
    public void A_bare_pointer_does_not_borrow_defect_language_from_a_preceding_denial(string output) =>
        ReviewVerdictValidation.NamesAFinding(output).Should().BeFalse();

    /// <summary>
    /// A location in one bullet and defect vocabulary in an unrelated next bullet do not credit
    /// each other (independent pre-PR review, cycle 2, adversarial finding,
    /// `ReviewVerdictValidation.cs:831`): <see cref="ReviewVerdictValidation"/>'s sentence
    /// splitter cannot break at a bare markdown bullet, so two adjacent list items with no
    /// terminal-punctuation-plus-capital-letter break between them arrive as one merged
    /// "sentence", and the same-sentence check used to trust that merged text directly instead of
    /// running it through the lookahead's own list-marker guard.
    /// </summary>
    [Fact]
    public void A_location_in_one_bullet_does_not_borrow_defect_language_from_an_unrelated_next_bullet()
    {
        ReviewVerdictValidation.NamesAFinding(
                "- I traced every path through `ReviewEngine.cs` and each one behaves as its doc "
                + "comment says.\n- No acceptance criterion is left unmet.\n\nMy findings are "
                + "reported above.\n\nVERDICT: needs-fixes")
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

    /// <summary>
    /// A `FINDING:` header at the contract's own placeholder path, followed by the worked example
    /// and then still more echoed contract prose with no blank line separating any of it, does not
    /// name a finding (cycle-9 finding #1): the exact-example paragraph check that used to be the
    /// only placeholder guard on this shape only ever matched a paragraph whose body was the
    /// two-line example and nothing else, so appending anything past it — with no blank line to
    /// split the extra text into a paragraph of its own — defeated that exact match while leaving
    /// the `Defect:`/`Scenario:` structural-marker branch free to fire on text that only ever names
    /// the prompt's own placeholder, `src/Some/File.cs`, dispatching a fix session against an
    /// example no repository contains.
    /// </summary>
    [Fact]
    public void A_placeholder_header_with_the_example_and_more_echoed_prose_on_one_paragraph_does_not_name_a_finding()
    {
        ReviewVerdictValidation.NamesAFinding(
                "FINDING: severity=high; scope=in-scope; at=src/Some/File.cs:123\n"
                + "Defect: one sentence saying what is wrong.\n"
                + "Scenario: the input or state that makes it misbehave, and what goes wrong.\n"
                + "**severity** — grade against these anchors, not against your own sense of importance:\n"
                + "\nVERDICT: needs-fixes")
            .Should().BeFalse();
    }

    /// <summary>
    /// A sentence that names the review mechanics' placeholder path first and a real location
    /// second still names a finding (cycle-9 finding #2): the prose heuristic reads only the first
    /// <c>LocationPattern</c> match in a sentence, so when that first match was the placeholder the
    /// old code discarded the whole sentence — real location, defect language and all — rather than
    /// looking past the placeholder for the one that actually points somewhere, and a human was
    /// parked over a named finding as though nothing had been named.
    /// </summary>
    [Fact]
    public void A_sentence_naming_the_placeholder_before_a_real_location_still_names_a_finding()
    {
        ReviewVerdictValidation.NamesAFinding(
                "Compare it against `path/to/file.cs:123`, but `src/Auth.cs:42` never resets the "
                + "limiter after a rejected request.\n\nVERDICT: needs-fixes")
            .Should().BeTrue();
    }

    /// <summary>
    /// Prose whose only location is one of this file's own prompt placeholders still names nothing
    /// (cycle-9 finding): stripping the placeholder out of the text before either the structured or
    /// the prose branch runs must not accidentally leave behind residue — punctuation, a stray
    /// article — that some other branch misreads as a location or a defect of its own.
    /// </summary>
    [Fact]
    public void Prose_whose_only_location_is_a_placeholder_still_does_not_name_a_finding()
    {
        ReviewVerdictValidation.NamesAFinding(
                "The mechanics bullet cites `path/to/file.cs:123`, but nothing here is wrong.\n\nVERDICT: needs-fixes")
            .Should().BeFalse();
    }

    /// <summary>
    /// The bare word "no" names a finding the same way its neighboring negation words ("not",
    /// "never", "no longer") already did (cycle-2 adversarial finding, `ReviewVerdictValidation.cs:65`).
    /// </summary>
    [Theory]
    [InlineData("There is no test for the archived path in `RunPathsTests.cs`.\n\nVERDICT: needs-fixes")]
    [InlineData("`src/Hall9k.Cli/Commands/LogsCommand.cs:50` has no cancellation token on the read.\n\nVERDICT: needs-fixes")]
    public void The_bare_word_no_names_a_finding(string output) =>
        ReviewVerdictValidation.NamesAFinding(output).Should().BeTrue();

    /// <summary>
    /// A structured `FINDING:` header carrying a REAL location, followed by the finding contract's
    /// own worked example, followed by yet more of the contract's own reprompt prose glued on with
    /// no blank line, still does not name a finding (cycle-2 conformance finding #1): the exact
    /// two-line match that closed this gap for a placeholder location
    /// (<see cref="A_structured_header_with_a_real_location_followed_by_the_finding_contracts_own_example_does_not_name_a_finding"/>)
    /// stopped matching as soon as anything was appended past the example, at which point the
    /// structural-marker branch fired on the literal `Defect:`/`Scenario:` labels anyway.
    /// </summary>
    [Fact]
    public void A_real_location_followed_by_the_example_and_more_echoed_prose_still_does_not_name_a_finding()
    {
        ReviewVerdictValidation.NamesAFinding(
                "FINDING: severity=high; scope=in-scope; at=src/Hall9k.Daemon/Review/ReviewEngine.cs:612\n"
                + "Defect: one sentence saying what is wrong.\n"
                + "Scenario: the input or state that makes it misbehave, and what goes wrong.\n"
                + "**severity** — grade against these anchors, not against your own sense of importance:\n"
                + "\nVERDICT: needs-fixes")
            .Should().BeFalse();
    }

    /// <summary>
    /// A conformance session that restates the task's own objective before concluding satisfies the
    /// location-plus-defect shape without having found anything (cycle-2 adversarial finding,
    /// `ReviewVerdictValidation.cs:190`): the objective is arbitrary per-task content the fixed
    /// placeholder list can never anticipate, so it has to be screened by value, passed in from the
    /// task that prompted this review pass.
    /// </summary>
    [Fact]
    public void Restating_the_tasks_own_objective_does_not_name_a_finding()
    {
        const string objective = "Stop LogsCommand.cs from resolving a stale run directory";
        ReviewVerdictValidation.NamesAFinding(
                $"The task was to {objective}, and the diff does this correctly.\n\nVERDICT: needs-fixes",
                objective)
            .Should().BeFalse();
    }

    /// <summary>
    /// The objective-echo screen only removes the objective's own text, so a genuine defect stated
    /// alongside a restated objective is still read as naming a finding.
    /// </summary>
    [Fact]
    public void A_real_defect_stated_alongside_a_restated_objective_still_names_a_finding()
    {
        const string objective = "Stop LogsCommand.cs from resolving a stale run directory";
        ReviewVerdictValidation.NamesAFinding(
                $"The task was to {objective}. `Auth.cs:42` never resets the limiter after a rejected "
                + "request.\n\nVERDICT: needs-fixes",
                objective)
            .Should().BeTrue();
    }

    /// <summary>
    /// A conformance session that restates one of the task's own acceptance criteria before
    /// concluding satisfies the location-plus-defect shape without having found anything (cycle-3
    /// review, `AgentPromptBuilder.cs:813-817`): the criteria bullets are arbitrary per-task text
    /// printed into the conformance prompt exactly like the objective, and this project's own task
    /// store shows criteria routinely pairing a filename with a defect word.
    /// </summary>
    [Fact]
    public void Restating_a_tasks_own_acceptance_criterion_does_not_name_a_finding()
    {
        const string criterion = "LogsCommand.cs no longer resolves a stale run directory";
        ReviewVerdictValidation.NamesAFinding(
                $"Acceptance criterion met: {criterion}.\n\nVERDICT: needs-fixes",
                taskAcceptanceCriteria: [criterion])
            .Should().BeFalse();
    }

    /// <summary>
    /// The acceptance-criteria echo screen only removes each criterion's own text, so a genuine
    /// defect stated alongside a restated criterion is still read as naming a finding.
    /// </summary>
    [Fact]
    public void A_real_defect_stated_alongside_a_restated_acceptance_criterion_still_names_a_finding()
    {
        const string criterion = "LogsCommand.cs no longer resolves a stale run directory";
        ReviewVerdictValidation.NamesAFinding(
                $"Acceptance criterion met: {criterion}. `Auth.cs:42` never resets the limiter after a "
                + "rejected request.\n\nVERDICT: needs-fixes",
                taskAcceptanceCriteria: [criterion])
            .Should().BeTrue();
    }

    /// <summary>
    /// A conformance session that restates the task's own agent context before concluding
    /// satisfies the location-plus-defect shape without having found anything (cycle-2 review,
    /// adversarial finding, `AgentPromptBuilder.cs:1117`): a routed bug task's agent context
    /// embeds a prior review finding verbatim, and an ordinary adopted task's context is an issue
    /// body that routinely pairs a filename with a defect word, exactly like the objective and
    /// the acceptance criteria already screened above.
    /// </summary>
    [Fact]
    public void Restating_the_tasks_own_agent_context_does_not_name_a_finding()
    {
        const string agentContext = "LogsCommand.cs no longer resolves a stale run directory";
        ReviewVerdictValidation.NamesAFinding(
                $"Context says: {agentContext}.\n\nVERDICT: needs-fixes",
                taskAgentContext: agentContext)
            .Should().BeFalse();
    }

    /// <summary>
    /// The agent-context echo screen only removes the context's own text, so a genuine defect
    /// stated alongside a restated agent context is still read as naming a finding.
    /// </summary>
    [Fact]
    public void A_real_defect_stated_alongside_a_restated_agent_context_still_names_a_finding()
    {
        const string agentContext = "LogsCommand.cs no longer resolves a stale run directory";
        ReviewVerdictValidation.NamesAFinding(
                $"Context says: {agentContext}. `Auth.cs:42` never resets the limiter after a "
                + "rejected request.\n\nVERDICT: needs-fixes",
                taskAgentContext: agentContext)
            .Should().BeTrue();
    }

    /// <summary>
    /// A human's own review-park <c>--reason</c> text can be as short as a single word (cycle-6
    /// human triage, task: review prompts carry prior rulings). "no" is also
    /// <c>DefectLanguagePattern</c>'s own bare negation word, so stripping every occurrence of it
    /// verbatim would blank the "no" out of a genuine reviewer's "has no timeout guard" the same
    /// way it blanks the echoed reason — there is no telling the two apart from the needle alone.
    /// Below the strip's minimum needle length, a two-letter reason is left alone entirely, so the
    /// real finding survives to be read.
    /// </summary>
    [Fact]
    public void A_two_letter_ruling_reason_does_not_mangle_a_real_finding()
    {
        ReviewVerdictValidation.NamesAFinding(
                "`Auth.cs:42` has no timeout guard on the retry loop.\n\nVERDICT: needs-fixes",
                priorRulingReasons: ["no"])
            .Should().BeTrue();
    }

    /// <summary>
    /// The conformance lens's own reporting verbs — "unmet", "departs", "violates", "breaks",
    /// "lacks", "omits" — name a defect the same way the rest of this vocabulary does (cycle-3
    /// review): the conformance lens has no structured findings contract, so this prose branch is
    /// its only path to naming anything, and a correctly located, correctly described finding
    /// phrased with one of these verbs must not read as naming nothing.
    /// </summary>
    [Theory]
    [InlineData("1. `RunPaths.cs:23` — this class violates the sealed-by-default rule; it is not sealed.\n\nVERDICT: needs-fixes")]
    [InlineData("1. `RunPaths.cs:23` — this class breaks the sealed-by-default rule.\n\nVERDICT: needs-fixes")]
    [InlineData("1. `RunPaths.cs:23` — this class lacks the sealed modifier the codebase's own rule requires.\n\nVERDICT: needs-fixes")]
    [InlineData("1. `RunPaths.cs:23` — this class omits the sealed modifier the codebase's own rule requires.\n\nVERDICT: needs-fixes")]
    [InlineData("1. `RunPaths.cs:23` — the acceptance criterion is unmet.\n\nVERDICT: needs-fixes")]
    [InlineData("1. `RunPaths.cs:23` — this class departs from the sealed-by-default rule.\n\nVERDICT: needs-fixes")]
    public void The_conformance_lenss_own_reporting_verbs_name_a_finding(string output) =>
        ReviewVerdictValidation.NamesAFinding(output).Should().BeTrue();

    /// <summary>
    /// A structured `FINDING:` header whose `at=` tag is not a real location — a half-filled
    /// reprompt template like "at=the review loop" — does not name a finding just because the
    /// tag and the body are both non-blank (cycle-4 adversarial finding,
    /// `ReviewVerdictValidation.cs:326`): trusting any non-blank header tag let this exact hollow
    /// shape through the structured branch even though the prose branch would have rejected the
    /// identical text for naming no real location at all.
    /// </summary>
    [Fact]
    public void A_structured_header_whose_at_tag_is_not_a_real_location_does_not_name_a_finding()
    {
        ReviewVerdictValidation.NamesAFinding(
                "FINDING: severity=high; scope=in-scope; at=the review loop\n"
                + "Defect: findings are reported above.\n\nVERDICT: needs-fixes")
            .Should().BeFalse();
    }

    /// <summary>
    /// Plain prose that describes what a located comment fails to convey for a reader, rather
    /// than for the comment itself, still names a finding (cycle-4 conformance finding): the
    /// sentence that carries the defect language opens with "A reader", not one of the pronouns
    /// <see cref="Prose_with_the_defect_in_the_next_sentence_still_names_a_finding"/> already
    /// covers, and the same-sentence-only rule rejected it even though it is exactly as much a
    /// continuation of the location's own sentence.
    /// </summary>
    [Fact]
    public void Prose_describing_what_a_reader_never_reaches_still_names_a_finding()
    {
        ReviewVerdictValidation.NamesAFinding(
                "The comment at `Auth.cs:10` references `Auth.cs:40`. A reader following either "
                + "pointer lands on unrelated text and never reaches the rule the comment is "
                + "explaining.\n\nVERDICT: needs-fixes")
            .Should().BeTrue();
    }

    /// <summary>
    /// The affirmative verb "refuses" names a defect the same way "drops" and "overwrites"
    /// already do (cycle-4 conformance finding): a guard checked too late, so it "refuses to
    /// finish" after other work already ran, is a real defect that the pre-affirmative-verb
    /// vocabulary read as naming nothing.
    /// </summary>
    [Fact]
    public void The_verb_refuses_names_a_finding()
    {
        ReviewVerdictValidation.NamesAFinding(
                "`AGENTS.md`'s guard in `BuildAsync` is checked last, after the recipe has already "
                + "written the whole home, so it refuses to finish only once the damage is already "
                + "done.\n\nVERDICT: needs-fixes")
            .Should().BeTrue();
    }

    /// <summary>
    /// A markdown heading that names only a location, with the defect described in the paragraph
    /// that follows it, still names a finding (cycle-5 conformance finding #1,
    /// `ReviewVerdictValidation.cs:262`): a heading and its own body are always split by the same
    /// blank line that separates any two paragraphs, so neither paragraph alone satisfied the
    /// old same-paragraph rule even though together they plainly name a location and a defect.
    /// </summary>
    [Theory]
    [InlineData("### 1. `src/Hall9k.Daemon/Review/ReviewEngine.cs:614` — the adversarial pass is over-screened\n"
        + "\n"
        + "Stripping the objective from an adversarial pass's output deletes the only location it "
        + "named.\n\nVERDICT: needs-fixes")]
    [InlineData("**1. `src/Hall9k.Daemon/Review/ReviewEngine.cs:614`**\n"
        + "\n"
        + "Stripping the objective from an adversarial pass's output deletes the only location it "
        + "named.\n\nVERDICT: needs-fixes")]
    public void A_heading_naming_a_location_with_the_defect_in_the_next_paragraph_still_names_a_finding(string output) =>
        ReviewVerdictValidation.NamesAFinding(output).Should().BeTrue();

    /// <summary>
    /// The heading-lead-in borrow is gated on the location's own paragraph actually being a
    /// heading or bold lead-in, not on mere paragraph adjacency: an ordinary sentence paragraph
    /// that mentions a location only in passing, followed by an unrelated paragraph that happens
    /// to use defect vocabulary about something else, must not borrow that language — the same
    /// CRLF-authored shape <see cref="A_crlf_authored_hollow_verdict_still_does_not_name_a_finding"/>
    /// already guards against paragraph boundaries collapsing unrelated text together.
    /// </summary>
    [Fact]
    public void An_ordinary_paragraph_does_not_borrow_defect_language_from_the_next_paragraph()
    {
        ReviewVerdictValidation.NamesAFinding(
                "Every criterion is met, and Program.cs proves it.\n\n"
                + "Nothing else here is wrong.\n\nVERDICT: needs-fixes")
            .Should().BeFalse();
    }

    /// <summary>
    /// Prescriptive doctrine phrasing ("should …") names a defect the same way the rest of this
    /// vocabulary does (cycle-5 conformance finding #2, `ReviewVerdictValidation.cs:86`): a
    /// house-rule departure is most often written as a "should" statement, and this repository's
    /// own coding standards are themselves a list of prescriptions, so a correctly located,
    /// correctly described finding phrased this way must not read as naming nothing.
    /// </summary>
    [Theory]
    [InlineData("`RunPaths.cs:23` should be sealed per AGENTS.md.\n\nVERDICT: needs-fixes")]
    [InlineData("The new async method at `ReviewEngine.cs:1530` should take a CancellationToken as "
        + "its last parameter.\n\nVERDICT: needs-fixes")]
    public void Prescriptive_should_phrasing_names_a_finding(string output) =>
        ReviewVerdictValidation.NamesAFinding(output).Should().BeTrue();

    /// <summary>
    /// A capitalized `Type.Member` reference must not be read as a file location (cycle-6
    /// finding, minimal repro): <c>Uri.TryCreate</c> reads exactly like a dotted
    /// "name.extension" pair the same way a real path does, so an affirming summary built out of
    /// such references, paired with a plain negation word ("not"), used to pass
    /// <see cref="ReviewVerdictValidation.NamesAFinding"/> despite naming no real location at
    /// all.
    /// </summary>
    [Fact]
    public void A_capitalized_type_member_reference_is_not_mistaken_for_a_location()
    {
        ReviewVerdictValidation.NamesAFinding(
                "`Uri.TryCreate` handles this correctly; nothing here is wrong.\n\nVERDICT: needs-fixes")
            .Should().BeFalse();
    }

    /// <summary>
    /// The Type.Member shape of the two recorded hollow verdicts this fix was written against
    /// (cycle-6 finding, task 4633f355 run 01a03935 review-3 conformance and task 74e572fb run
    /// 01a03726 review-9 conformance): a bullet from each, reporting something the reviewer says
    /// it checked and cleared rather than something it found wrong, used to pass
    /// <see cref="ReviewVerdictValidation.NamesAFinding"/> purely because it is dense with
    /// capitalized `Type.Member` references (`File.WriteAllTextAsync`, `PlatformPaths.Home`,
    /// `RunPaths.Root`) sitting near a negation word used to describe what did NOT need fixing.
    /// Each recorded output in full still passes even after this fix, for a reason this fix does
    /// not touch and is not meant to: both also mention an unrelated real (lowercase-extension)
    /// location — `cmd.exe`, `config.json` — inside a sentence that separately negates its own
    /// claim ("never applies", "I did **not** file"), which is the keyword-and-proximity gap
    /// <see cref="ReviewVerdictValidation.NamesAFinding"/>'s own doc comment already discloses as
    /// permanent ("closing that gap needs reading comprehension a regex cannot do"). Isolating
    /// just the `Type.Member`-bearing bullet from each recorded output, the way this test does,
    /// is what isolates the mechanism this fix actually closes from that pre-existing, separately
    /// documented one.
    /// </summary>
    [Theory]
    [InlineData(
        "Six verified findings, reported above.\n\n"
        + "What I checked and found sound, so it does not need re-examining:\n\n"
        + "- **The stop-request path.** The read/write race is safe on Windows specifically "
        + "because `File.WriteAllTextAsync`'s `FileShare.Read` makes a concurrent reader fail "
        + "with `IOException` (already handled) rather than see truncated content.\n\n"
        + "VERDICT: needs-fixes")]
    [InlineData(
        "Findings reported above. Notes on what I checked and cleared, so the fix run does not "
        + "re-litigate it:\n\n"
        + "- **The install/uninstall mirror is faithful.** I grepped every "
        + "`PlatformPaths.Home`/`RunPaths.Root` consumer; nothing install writes is missed, and "
        + "nothing it does not write is listed.\n\n"
        + "VERDICT: needs-fixes")]
    public void A_recorded_hollow_verdict_dense_with_type_member_references_does_not_name_a_finding(string output) =>
        ReviewVerdictValidation.NamesAFinding(output).Should().BeFalse();

    /// <summary>
    /// A legitimate lowercase-extension location is unaffected by the lowercase requirement the
    /// cycle-6 fix added: `cmd.exe`, `docs/cli.md` and `install.ps1` are exactly the shapes the
    /// two recorded hollow verdicts above also mention in passing, but each still names a
    /// finding when paired with real defect language of its own.
    /// </summary>
    [Theory]
    [InlineData("1. `cmd.exe:12` — the quoting is wrong for arguments with embedded quotes.\n\nVERDICT: needs-fixes")]
    [InlineData("1. `docs/cli.md` — this omits the new flag entirely.\n\nVERDICT: needs-fixes")]
    [InlineData("1. `install.ps1:40` — this never checks the download hash.\n\nVERDICT: needs-fixes")]
    public void A_lowercase_extension_location_still_names_a_finding(string output) =>
        ReviewVerdictValidation.NamesAFinding(output).Should().BeTrue();

    /// <summary>
    /// The lowercase-extension narrowing's known, accepted cost (cycle-11 conformance finding,
    /// `ReviewVerdictValidation.cs:58`; recorded output
    /// <c>~/.hall9k/runs/01a031db-ae49-73a3-a8d3-e3117796f0ba/review-2-conformance-findings.md</c>):
    /// a genuine, cleanly-stated finding whose only location is a backticked `Type.Member` symbol
    /// rather than a file path has no real (lowercase-extension) location anywhere in its text, so
    /// this narrowing — which cannot tell a real symbol-only location apart from the spurious
    /// `Type.Member` mentions <see cref="A_recorded_hollow_verdict_dense_with_type_member_references_does_not_name_a_finding"/>
    /// covers — currently reads it as naming nothing. This documents the tradeoff rather than
    /// asserting it should pass: recovering it needs a distinct location grammar for symbol-only
    /// pointers, not a loosening of the extension-casing rule cycle-6 added.
    /// </summary>
    [Fact]
    public void A_finding_located_only_by_a_type_member_symbol_is_not_currently_recognized()
    {
        ReviewVerdictValidation.NamesAFinding(
                "One confirmed defect found: `ProjectHomeRenderEngine.RenderIdea` creates a decoy "
                + "`workspace/` directory when an idea is reassigned across two projects that both "
                + "have materialised homes, because it checks `idea.WorkspaceHome.HasValue` instead "
                + "of whether `WorkspaceHome` matches the project currently being rendered.\n\n"
                + "VERDICT: needs-fixes")
            .Should().BeFalse();
    }

    /// <summary>
    /// A heading naming a real location, immediately followed by the finding contract's own
    /// worked example, does not name a finding (cycle-10 conformance finding #1,
    /// `ReviewVerdictValidation.cs:368`): the heading-lead-in borrow has to be screened against
    /// the contract's own example the same way the structured header-to-body shape already is,
    /// or a half-filled reprompt template dispatches a fix session against text the platform
    /// wrote, not text the reviewer found.
    /// </summary>
    [Fact]
    public void A_heading_followed_by_the_finding_contracts_own_example_does_not_name_a_finding()
    {
        ReviewVerdictValidation.NamesAFinding(
                "### `src/Auth.cs:42`\n"
                + "\n"
                + "Defect: one sentence saying what is wrong.\n"
                + "Scenario: the input or state that makes it misbehave, and what goes wrong.\n"
                + "\nVERDICT: needs-fixes")
            .Should().BeFalse();
    }

    /// <summary>
    /// A markdown heading immediately followed by unrelated body text on the very next line, with
    /// no blank line separating them, is not a bare heading lead-in — it is one ordinary paragraph
    /// that merely opens with a `#` (cycle-10 conformance finding #2,
    /// `ReviewVerdictValidation.cs:231`): the old `#` alternative matched on the marker alone,
    /// with nothing checking that the heading was the whole paragraph, so this shape let an
    /// affirming review borrow "broken" for a location the reviewer's own heading named for
    /// something else entirely.
    /// </summary>
    [Fact]
    public void A_heading_immediately_followed_by_body_text_on_the_same_paragraph_does_not_borrow_defect_language()
    {
        ReviewVerdictValidation.NamesAFinding(
                "## Conformance review of `ReviewEngine.cs`\n"
                + "Everything checks out; I verified each criterion.\n"
                + "\n"
                + "The daemon's own dispatch loop is broken elsewhere, but that is pre-existing.\n"
                + "\nVERDICT: needs-fixes")
            .Should().BeFalse();
    }

    /// <summary>
    /// A heading naming a real location, followed by a paragraph that denies rather than asserts a
    /// defect, does not name a finding (cycle-10 adversarial finding #2,
    /// `ReviewVerdictValidation.cs:371`): "Nothing is wrong; no defect stands." trips
    /// <see cref="ReviewVerdictValidation"/>'s defect vocabulary purely because "wrong", "no" and
    /// "defect" are all in it, even though every one of them is being used to deny a problem — the
    /// same affirming-review shape <see cref="An_affirming_sentence_does_not_borrow_defect_language_from_a_preceding_sentence"/>
    /// already guards against at sentence scope, now closed for the heading-lead-in branch too.
    /// </summary>
    [Fact]
    public void A_heading_followed_by_a_denial_paragraph_does_not_name_a_finding()
    {
        ReviewVerdictValidation.NamesAFinding(
                "## Findings for `ReviewEngine.cs`\n"
                + "\n"
                + "Nothing is wrong; no defect stands.\n"
                + "\nVERDICT: needs-fixes")
            .Should().BeFalse();
    }

    /// <summary>
    /// A genuine finding that restates an acceptance criterion's own wording still names a finding
    /// from the location the criterion and the finding share (cycle-10 adversarial finding,
    /// `ReviewVerdictValidation.cs:326`): the conformance lens's most ordinary phrasing restates
    /// each criterion and marks it, so blanking the whole matched span used to delete the only
    /// location the reviewer stated along with the criterion's own wording, leaving the reviewer's
    /// own defect language ("UNMET", "still joins a literal…") with nothing left to point at.
    /// </summary>
    [Fact]
    public void A_criterion_restated_as_part_of_a_genuine_finding_still_names_a_finding()
    {
        const string criterion = "LogsCommand.cs resolves an archived task's run directory";
        ReviewVerdictValidation.NamesAFinding(
                "## Acceptance criteria\n"
                + "\n"
                + $"- {criterion} — UNMET. The command still joins a literal \"runs\" segment.\n"
                + "\nVERDICT: needs-fixes",
                taskAcceptanceCriteria: [criterion])
            .Should().BeTrue();
    }

    /// <summary>
    /// Plain prose that states what the located code does in the sentence right after the
    /// location, and only says what is wrong with it a second sentence later, still names a
    /// finding (cycle-2 review, conformance finding #1, a recorded output at
    /// `~/.hall9k/runs/01a02984-.../review-1-conformance-findings.md`): the old one-sentence,
    /// pronoun-gated lookahead could reach neither "Every failure branch…" (no continuation
    /// pronoun) nor the sentence past it, so a correctly located, correctly described defect
    /// ("the subject is wrong") was demoted to a missing verdict.
    /// </summary>
    [Fact]
    public void Prose_with_the_defect_two_sentences_after_the_location_still_names_a_finding()
    {
        ReviewVerdictValidation.NamesAFinding(
                "`src/Foo/Bar.cs:143` — `VerifyAccessAsync` passes the wrong argument to `SendAsync`. "
                + "Every failure branch in `Explain` interpolates it into the message, so all five "
                + "reachable messages render the wrong subject. The corrective guidance in each "
                + "message is still right, but the subject is wrong in the first command a new user "
                + "runs.\n\nVERDICT: needs-fixes")
            .Should().BeTrue();
    }

    /// <summary>
    /// The lookahead past a location's own sentence is bounded, not run to the end of the
    /// paragraph (see <c>ReviewVerdictValidation.DefectLookaheadSentences</c>): a defect word
    /// three sentences after the location, with two intervening sentences that are themselves
    /// silent on the subject, is still outside the two real recorded gaps this bound was written
    /// to close and stays unread, the same narrowing the rest of this file's vocabulary accepts.
    /// </summary>
    [Fact]
    public void A_defect_word_three_sentences_after_the_location_stays_outside_the_lookahead()
    {
        ReviewVerdictValidation.NamesAFinding(
                "`src/Foo/Bar.cs:143` is where the handler lives. It has three branches. Each one "
                + "does its own thing. Something here is wrong.\n\nVERDICT: needs-fixes")
            .Should().BeFalse();
    }

    /// <summary>
    /// A bold lead-in whose own emphasis uses a single asterisk still reads as a lead-in, with
    /// the defect in a later paragraph (cycle-2 review, conformance finding #2, a recorded output
    /// at `~/.hall9k/runs/01a02a91-.../review-1-conformance-findings.md`): the old
    /// <c>\*\*[^\n*]+\*\*</c> read the inner <c>*before*</c> as the closing marker, so the whole
    /// paragraph matched neither heading alternative and the borrow never fired.
    /// </summary>
    [Fact]
    public void A_bold_lead_in_with_an_embedded_italic_run_still_borrows_the_defect_that_follows()
    {
        ReviewVerdictValidation.NamesAFinding(
                "**1. `src/Foo/Bar.cs:277` — the sweep logs the count measured *before* its own "
                + "claims.**\n\n"
                + "`MeasureLoadAsync` runs, and only then does the claim loop run.\n\n"
                + "Failure scenario: the log then contradicts itself and reads wrong.\n\n"
                + "VERDICT: needs-fixes")
            .Should().BeTrue();
    }

    /// <summary>
    /// A bold location followed by a short same-line label, with the defect two paragraphs later
    /// behind a neutral mechanism paragraph, still names a finding (cycle-2 review, conformance
    /// finding #2 and #3, recorded outputs at
    /// `~/.hall9k/runs/01a029e6-.../review-3-adversarial-findings.md` and
    /// `~/.hall9k/runs/01a02a91-.../review-1-conformance-findings.md`): the old bold alternative's
    /// `$` anchor refused a marker that did not consume the whole paragraph, so this shape matched
    /// no heading alternative at all, and even when it did, the old check only ever looked at the
    /// single next paragraph — never the one past a neutral mechanism paragraph in between.
    /// </summary>
    [Fact]
    public void A_same_line_bold_label_still_borrows_the_defect_two_paragraphs_later()
    {
        ReviewVerdictValidation.NamesAFinding(
                "**`src/Foo/Bar.cs:274`** — the adoption path records the caution twice.\n\n"
                + "`WaitAsync` already returns the caution once; `Stranded` appends it again.\n\n"
                + "Failure scenario: the operator-facing sentence repeats itself and reads wrong.\n\n"
                + "VERDICT: needs-fixes")
            .Should().BeTrue();
    }

    /// <summary>
    /// Two consecutive lead-ins that are each genuinely hollow — a mechanism paragraph with no
    /// vocabulary word between them and after the last one — do not name a finding just because
    /// the multi-paragraph borrow now walks past a single next paragraph: the walk still has to
    /// land on real defect language, or a stated denial, or another lead-in, before it finds
    /// anything, and none of this output has any.
    /// </summary>
    [Fact]
    public void Two_hollow_lead_ins_in_a_row_still_do_not_name_a_finding()
    {
        ReviewVerdictValidation.NamesAFinding(
                "**1. `src/Foo/Bar.cs:10`** — first finding's label.\n\n"
                + "A neutral mechanism sentence carrying nothing else worth naming.\n\n"
                + "**2. `src/Foo/Baz.cs:20`** — second finding's label.\n\n"
                + "Another neutral mechanism sentence carrying nothing else worth naming.\n\n"
                + "VERDICT: needs-fixes")
            .Should().BeFalse();
    }

    /// <summary>
    /// A same-line bold label that already denies its own location — even with the denial too far
    /// past the location for the sentence-scoped check's own bounded lookahead to see it — is not
    /// a lead-in needing to borrow from whatever unrelated defect language happens to sit in a
    /// later paragraph.
    /// </summary>
    [Fact]
    public void A_same_line_bold_label_that_already_denies_a_defect_does_not_borrow_from_a_later_paragraph()
    {
        ReviewVerdictValidation.NamesAFinding(
                "**`src/Foo/Bar.cs:10`** — checked. Reviewed twice. Confirmed clean. Nothing about "
                + "it stands as a defect.\n\n"
                + "Something unrelated elsewhere in the diff is broken.\n\n"
                + "VERDICT: needs-fixes")
            .Should().BeFalse();
    }
}

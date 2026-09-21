using FluentAssertions;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// What the platform reads back out of a QA review's own report (idea b9b09779, piece 2): the
/// blast-radius map and its verdicts, the end-to-end outcome, and the flows the session drove.
/// The inputs here are the reports a QA session actually writes for a small, made-up diff, not
/// synthetic marker soup, because the thing worth proving is that a real report parses.
/// </summary>
public sealed class QaReviewReportTests
{
    /// <summary>
    /// The QA report for a fake diff: a coupon can now be removed from a cart. Three map
    /// entries, one of each verdict, which is the whole point — the map has to be able to carry
    /// all three, and an entry carries exactly one.
    /// </summary>
    private const string ReportForAFakeDiff = """
        ## Blast radius

        ### What the diff changes

        MAP: entry=b1; coverage=covered
        Removing a coupon from a cart recalculates the cart total immediately rather than at checkout.
        What backs the verdict: tests/e2e/cart.spec.ts, "removing a coupon updates the total".

        ### What sits next to it

        MAP: entry=b2; coverage=new-test
        Applying a second coupon over an existing one shares the same recalculation path and the same
        cached subtotal, and nothing exercises the two together.
        What backs the verdict: an end-to-end test that applies two coupons in turn, removes the first,
        and asserts the total reflects only the second.

        ### User-facing flows

        MAP: entry=b3; coverage=walk-through
        A returning customer with a saved cart opens it a day later and removes the coupon.
        What backs the verdict:
        1. Sign in as a customer with a saved cart holding one coupon.
        2. Wait out the cart's own cache window, or clear the session cookie and sign in again.
        3. Open the cart and remove the coupon.
        4. The total should drop by the coupon's value and the saved cart should still hold its items.

        ## Evidence

        END-TO-END TESTS: pass
        Ran `npm run test:e2e -- cart` on this worktree: 34 passed, 0 failed.

        Conventions: met. Writing conventions: not verifiable from the code alone, since this change
        adds no prose a person reads. Decision log: not applicable. Acceptance criteria: met.

        ## Findings

        FINDING: severity=medium; scope=in-scope; at=src/cart/recalculate.ts:88
        Defect: entry b2 on the map above is uncovered and this change widens it.
        Scenario: two coupons applied in turn, the first removed, and the cached subtotal is stale.

        RUN-SKILL DRIFT: no

        VERDICT: needs-fixes
        """;

    [Fact]
    public void A_fake_diffs_report_produces_a_map_carrying_all_three_verdict_kinds()
    {
        IReadOnlyList<QaBlastRadiusEntry> map = ReviewResultParser.ParseBlastRadiusMap(ReportForAFakeDiff);

        map.Select(entry => entry.Id).Should().Equal("b1", "b2", "b3");
        map.Select(entry => entry.Verdict).Should().Equal(
            QaCoverageVerdict.Covered, QaCoverageVerdict.NewTest, QaCoverageVerdict.WalkThrough);
        map.Should().OnlyContain(entry => entry.Verdict.HasValue, "no entry may be left without a verdict");

        // Each entry carries its own prose whole, and stops where the next one starts — a map the
        // platform could only count would be no better than a map it never read.
        map[0].Text.Should().Contain("recalculates the cart total immediately");
        map[0].Text.Should().NotContain("Applying a second coupon");
        map[2].Text.Should().Contain("4. The total should drop by the coupon's value");
    }

    /// <summary>
    /// The map stops at the report, and the report is not swallowed into the last entry: a
    /// finding, the evidence lines, the standing question, and the verdict all close it.
    /// </summary>
    [Fact]
    public void The_map_does_not_swallow_the_report_that_follows_it()
    {
        IReadOnlyList<QaBlastRadiusEntry> map = ReviewResultParser.ParseBlastRadiusMap(ReportForAFakeDiff);

        map[2].Text.Should().NotContain("34 passed");
        map[2].Text.Should().NotContain("Defect:");
        map.Should().HaveCount(3, "the evidence and findings sections are not map entries");
    }

    /// <summary>
    /// An ungraded entry is reported as ungraded rather than read as the nearest plausible
    /// verdict. This is the gap the map exists to expose, so guessing at it would defeat the
    /// section.
    /// </summary>
    [Fact]
    public void An_entry_with_no_readable_verdict_is_unstated_rather_than_guessed_at()
    {
        IReadOnlyList<QaBlastRadiusEntry> map = ReviewResultParser.ParseBlastRadiusMap(
            "MAP: entry=b1\nSomething changed.\n\nMAP: entry=b2; coverage=probably fine\nSomething else.");

        map.Should().HaveCount(2);
        map.Should().OnlyContain(entry => entry.Verdict == QaCoverageVerdict.Unstated);
    }

    /// <summary>
    /// A session that quotes its own instructions before answering echoes the contract's worked
    /// example back verbatim, and a map entry has no "last one wins" rule to absorb it the way
    /// the verdict line does. The echo is dropped rather than standing as a fabricated entry
    /// with a verdict nobody assigned — the same guard the finding parser's placeholder location
    /// already gives findings.
    /// </summary>
    [Fact]
    public void An_echo_of_the_contracts_own_worked_example_is_dropped_rather_than_counted()
    {
        IReadOnlyList<QaBlastRadiusEntry> map = ReviewResultParser.ParseBlastRadiusMap(
            $"I was told each entry opens with a header of this shape:\n\n"
            + $"    MAP: entry={ReviewResultParser.ExampleMapEntryPlaceholder}; coverage=covered\n"
            + "    The behaviour, in one or two plain sentences.\n\n"
            + "MAP: entry=b1; coverage=covered\nRemoving a coupon recalculates the cart total.");

        map.Should().ContainSingle().Which.Id.Should().Be("b1");
    }

    [Fact]
    public void The_example_placeholder_is_a_label_no_reviewer_would_pick_for_a_real_behaviour() =>
        ReviewResultParser.ExampleMapEntryPlaceholder.Should().Be("map-entry-example");

    [Theory]
    [InlineData("END-TO-END TESTS: pass", "pass")]
    [InlineData("END-TO-END TESTS: failed", "fail")]
    [InlineData("END-TO-END TESTS: absent", "absent")]
    public void The_end_to_end_outcome_is_read_off_its_own_line(string line, string expected) =>
        ReviewResultParser.ParseEndToEndOutcome($"Some prose.\n{line}\n").Value.Should().Be(expected);

    [Fact]
    public void An_unreported_end_to_end_run_is_unstated_rather_than_a_pass() =>
        ReviewResultParser.ParseEndToEndOutcome("Everything looked fine to me.")
            .Should().Be(QaEndToEndOutcome.Unstated);

    [Fact]
    public void A_session_that_quoted_the_contract_before_answering_is_read_on_its_last_answer() =>
        ReviewResultParser.ParseEndToEndOutcome(
            "I was told to write END-TO-END TESTS: pass or END-TO-END TESTS: fail.\n\nEND-TO-END TESTS: fail")
            .Should().Be(QaEndToEndOutcome.Fail);

    [Fact]
    public void The_driven_flows_are_read_off_their_own_line()
    {
        ReviewResultParser.ParseDrivenFlows("DRIVEN: a returning customer checks out; a password reset")
            .Should().Equal("a returning customer checks out", "a password reset");
        ReviewResultParser.ParseDrivenFlows("No product was launched.").Should().BeEmpty();
    }

    /// <summary>
    /// The three facts a reader should not have to hunt for, stated above the QA section's own
    /// text — and the text itself carried whole underneath, so the evidence is never traded for
    /// the summary.
    /// </summary>
    [Fact]
    public async Task The_qa_section_summarises_the_map_the_run_and_the_drive_and_keeps_the_report_whole()
    {
        string body = await ComposeQaSectionAsync(ReportForAFakeDiff);

        body.Should().Contain("## QA review");
        body.Should().Contain("Blast radius: 3 entries — 1 covered, 1 new-test, 1 walk-through.");
        body.Should().Contain("End-to-end tests: ran on the review worktree and passed.");
        body.Should().Contain(
            "Driven: nothing — the product was not launched, because this project has QA-review driving "
            + "turned off and has no run skill on its ledger.");
        body.Should().Contain("Run-skill drift: checked, no");
        body.Should().Contain("MAP: entry=b2; coverage=new-test", "the report itself is carried verbatim");
    }

    /// <summary>
    /// A failing end-to-end run reaches the human with its evidence attached. The summary line
    /// says it failed and points down at the output; the output itself is in the report, not
    /// summarized away.
    /// </summary>
    [Fact]
    public async Task A_failing_end_to_end_run_is_reported_with_its_evidence_rather_than_swallowed()
    {
        const string failing = """
            MAP: entry=b1; coverage=covered
            Removing a coupon recalculates the cart total.

            END-TO-END TESTS: fail
            `npm run test:e2e -- cart` on this worktree: 33 passed, 1 failed.

                ✗ cart.spec.ts > removing a coupon updates the total
                  expected 42.00, received 55.00

            The same test passes on a clean checkout of main, so this change caused it.

            FINDING: severity=high; scope=in-scope; at=src/cart/recalculate.ts:88
            Defect: entry b1 on the map above is broken by this change.
            Scenario: a cart with one coupon; removing it leaves the total at the discounted value.

            RUN-SKILL DRIFT: no

            VERDICT: needs-fixes
            """;

        string body = await ComposeQaSectionAsync(failing);

        body.Should().Contain("End-to-end tests: ran on the review worktree and failed; the evidence is in the report below.");
        body.Should().Contain("expected 42.00, received 55.00");
        body.Should().Contain("The same test passes on a clean checkout of main");
        body.Should().Contain("33 passed, 1 failed");
    }

    [Fact]
    public async Task A_session_that_drove_the_product_names_the_flows_it_walked_in_the_report()
    {
        const string driven = """
            MAP: entry=b1; coverage=walk-through
            A returning customer removes a coupon from a saved cart.

            END-TO-END TESTS: pass
            DRIVEN: a returning customer removes a coupon from a saved cart; a new customer applies one

            Started the product from the run skill on port 43117 and stopped it afterward.

            RUN-SKILL DRIFT: no

            VERDICT: merge-ready
            """;

        string body = await ComposeQaSectionAsync(driven);

        body.Should().Contain(
            "Driven: a returning customer removes a coupon from a saved cart; a new customer applies one.");
        body.Should().Contain("port 43117");
    }

    /// <summary>
    /// A QA session that produced no map at all says so rather than rendering a tidy zero: the
    /// review still happened and its text is still here, and a reader has to know it was
    /// unmapped before they weigh it.
    /// </summary>
    [Fact]
    public void A_report_with_no_readable_map_says_so_rather_than_reporting_nothing()
    {
        string lines = PrReviewEngine.QaSummaryLines("I read the diff and it looked fine.", NoDrive);

        lines.Should().Contain("Blast radius: no entries this report could be read for");
        lines.Should().Contain("End-to-end tests: not reported by this session.");
    }

    /// <summary>
    /// The third state of the driven line (independent pre-PR review, cycle 1, adversarial
    /// lens). A session this run authorised to drive, whose report names no flows — it left the
    /// line out, or echoed the contract's own placeholder, which the parser reads as an echo —
    /// is not a session that was never allowed to launch anything, and the summary must not
    /// report the run's own decision as the opposite of what it recorded.
    /// </summary>
    [Fact]
    public async Task A_review_authorised_to_drive_that_names_no_flows_is_not_reported_as_never_launched()
    {
        const string silent = """
            MAP: entry=b1; coverage=covered
            Removing a coupon recalculates the cart total.

            END-TO-END TESTS: pass

            RUN-SKILL DRIFT: no

            VERDICT: merge-ready
            """;

        string body = await ComposeQaSectionAsync(silent, DriveAuthorised);

        body.Should().Contain("Driven: no flows named — driving was authorised for this review");
        body.Should().NotContain("the product was not launched");
    }

    /// <summary>
    /// The ordinary default, and the half of it the old wording left out: a review that did not
    /// drive says which of the two reasons applied, so a reader knows whether to change a setting
    /// or to record a run skill.
    /// </summary>
    [Fact]
    public void A_review_that_could_not_drive_names_the_reason_it_could_not()
    {
        string lines = PrReviewEngine.QaSummaryLines(
            "END-TO-END TESTS: pass",
            new ReviewDriveDecision(ReviewPersona.Qa, SettingOn: true, ProjectHasRunSkill: false));

        lines.Should().Contain(
            "Driven: nothing — the product was not launched, because this project has no run skill on "
            + "its ledger");
    }

    /// <summary>The default every project gets: QA-review driving off, and nothing to drive with.</summary>
    private static ReviewDriveDecision NoDrive => ReviewDriveDecision.NoneFor(ReviewPersona.Qa);

    private static ReviewDriveDecision DriveAuthorised =>
        new(ReviewPersona.Qa, SettingOn: true, ProjectHasRunSkill: true);

    private static async Task<string> ComposeQaSectionAsync(string qaReport, ReviewDriveDecision? drive = null)
    {
        string runDirectory = Path.Combine(Path.GetTempPath(), $"h9k-qa-report-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runDirectory);
        try
        {
            await File.WriteAllTextAsync(
                RunPaths.ReviewLensFindingsFile(runDirectory, 1, ReviewPersonaRegistry.QaSlug), qaReport);
            return await PrReviewEngine.ComposePersonaSectionsAsync(
                runDirectory, ReviewPersonaRegistry.Plan([ReviewPersona.Qa], drive is null ? null : [drive]),
                new Dictionary<string, ReviewPersonaSessionFailure>(), CancellationToken.None);
        }
        finally
        {
            Directory.Delete(runDirectory, recursive: true);
        }
    }
}

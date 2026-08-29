using FluentAssertions;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The standing sweep an out-of-scope, low-severity review finding folds into instead of
/// minting a draft of its own (Decisions Log #99).
/// </summary>
public sealed class SweepDraftTaskTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_brand_new_sweep_is_a_draft_chore_under_the_fixed_objective()
    {
        Guid projectId = DomainId.New();
        Guid draftTaskId = DomainId.New();

        TaskAdded added = SweepDraftTask.ComposeNew(draftTaskId, projectId, [Route()], Now, DomainId.New());

        added.Id.Should().Be(draftTaskId);
        added.ProjectId.Should().Be(projectId);
        added.Type.Should().Be(TaskType.Chore);
        added.Objective.Should().Be(SweepDraftTask.Objective);
        added.StartsAsDraft.Should().BeTrue("it stays a draft — the platform never publishes it");
        added.AcceptanceCriteria.Should().ContainSingle();
    }

    [Fact]
    public void An_item_carries_its_location_severity_finding_text_and_evidence_path()
    {
        string findingsFile = Path.Combine("runs", "abc", "review-3-findings.md");
        SweepFindingRoute route = Route(runId: DomainId.New(), cycle: 3, findingsFile: findingsFile);

        string body = SweepDraftTask.ComposeNew(DomainId.New(), DomainId.New(), [route], Now, DomainId.New()).AgentContext!;

        body.Should().Contain("### Cosmetic.cs:4")
            .And.Contain("Severity: Low")
            .And.Contain("a stale comment misleads")
            .And.Contain(route.RunId.ToString())
            .And.Contain("cycle 3")
            .And.Contain(findingsFile, "a directory RunPaths cannot resolve is returned unchanged, so a made-up test path still round-trips");
    }

    [Fact]
    public void The_generated_body_warns_the_footprint_is_wide_and_to_assign_it_alone()
    {
        string body = SweepDraftTask.ComposeNew(DomainId.New(), DomainId.New(), [Route()], Now, DomainId.New()).AgentContext!;

        body.Should().Contain("footprint is wide")
            .And.Contain("Assign it alone")
            .And.Contain("no parallel siblings");
    }

    [Fact]
    public void The_body_also_names_the_grooming_guideline()
    {
        string body = SweepDraftTask.ComposeNew(DomainId.New(), DomainId.New(), [Route()], Now, DomainId.New()).AgentContext!;

        body.Should().Contain("five to eight items");
    }

    /// <summary>
    /// A second finding naming the same file and stated line as an item already on the sweep
    /// updates that item's evidence list rather than duplicating the item — the idempotency the
    /// sweep exists to provide.
    /// </summary>
    [Fact]
    public void A_re_raise_of_the_same_file_and_line_updates_the_items_evidence_instead_of_duplicating_it()
    {
        Guid firstRun = DomainId.New();
        Guid secondRun = DomainId.New();
        string firstBody = SweepDraftTask.ComposeNew(
            DomainId.New(), DomainId.New(), [Route(runId: firstRun, cycle: 1)], Now, DomainId.New()).AgentContext!;

        string updated = SweepDraftTask.Append(firstBody, [Route(runId: secondRun, cycle: 4)]);

        CountOccurrences(updated, "### Cosmetic.cs:4").Should().Be(1, "one defect stays one item");
        updated.Should().Contain(firstRun.ToString()).And.Contain(secondRun.ToString(),
            "both observations are recorded, on the one item");
    }

    /// <summary>
    /// The exact same run and cycle appended twice — a resumed daemon re-processing a batch
    /// whose write landed but whose acknowledgement did not, for instance — records one
    /// evidence entry, not two: the platform observed it once.
    /// </summary>
    [Fact]
    public void The_same_run_and_cycle_appended_twice_records_one_evidence_entry()
    {
        Guid runId = DomainId.New();
        string firstBody = SweepDraftTask.ComposeNew(
            DomainId.New(), DomainId.New(), [Route(runId: runId, cycle: 2)], Now, DomainId.New()).AgentContext!;

        string updated = SweepDraftTask.Append(firstBody, [Route(runId: runId, cycle: 2)]);

        CountOccurrences(updated, $"Run {runId}").Should().Be(1);
    }

    /// <summary>
    /// A different stated line in the same file is a different defect (the same conservative
    /// reading <see cref="ReviewFindingLocations"/> documents everywhere else it applies):
    /// folding it into an existing item would silently swallow a genuinely different one.
    /// </summary>
    [Fact]
    public void A_different_line_in_the_same_file_becomes_a_second_item()
    {
        string firstBody = SweepDraftTask.ComposeNew(
            DomainId.New(), DomainId.New(), [Route(location: "Cosmetic.cs:4")], Now, DomainId.New()).AgentContext!;

        string updated = SweepDraftTask.Append(firstBody, [Route(location: "Cosmetic.cs:9")]);

        updated.Should().Contain("### Cosmetic.cs:4").And.Contain("### Cosmetic.cs:9");
    }

    /// <summary>
    /// A finding the reviewer never placed on a line cannot be shown to repeat one already on
    /// the sweep, so every blank-location finding becomes its own item — never silently folded
    /// into an unrelated one just because both happened to name no place.
    /// </summary>
    [Fact]
    public void Two_blank_location_findings_are_two_items_not_one()
    {
        string firstBody = SweepDraftTask.ComposeNew(
            DomainId.New(), DomainId.New(), [Route(location: string.Empty)], Now, DomainId.New()).AgentContext!;

        string updated = SweepDraftTask.Append(firstBody, [Route(location: string.Empty)]);

        CountOccurrences(updated, "### (no location stated)").Should().Be(2, "two items, each headed by the marker");
    }

    /// <summary>
    /// The one case a blank location can still recognize as a repeat: the SAME run's own review
    /// track reporting the identical unplaced finding text again in a later cycle. Before this
    /// fix every cycle re-appended its own item, so a track alive for five cycles left five
    /// duplicates of the one defect on the shared sweep (cycle-5 adversarial review).
    /// </summary>
    [Fact]
    public void The_same_run_re_reporting_the_identical_blank_location_finding_updates_one_item()
    {
        Guid runId = DomainId.New();
        string firstBody = SweepDraftTask.ComposeNew(
            DomainId.New(), DomainId.New(), [Route(runId: runId, cycle: 1, location: string.Empty)], Now, DomainId.New())
            .AgentContext!;

        string updated = SweepDraftTask.Append(
            firstBody, [Route(runId: runId, cycle: 2, location: string.Empty)]);

        CountOccurrences(updated, "### (no location stated)").Should().Be(1, "one repeated observation, one item");
        CountOccurrences(updated, $"Run {runId}").Should().Be(2, "both cycles' evidence is still recorded");
    }

    /// <summary>
    /// <c>SamePlace</c> is not transitive: two distinct findings from the same run and cycle can
    /// each match an existing item by suffix (<c>src/a/Foo.cs:12</c> and <c>src/b/Foo.cs:12</c>
    /// both match <c>Foo.cs:12</c>) without matching each other. The repeat guard used to key
    /// only on run and cycle, so the second one was silently dropped instead of recorded
    /// (cycle-4 conformance review).
    /// </summary>
    [Fact]
    public void Two_distinct_findings_from_the_same_run_and_cycle_both_become_evidence_on_the_matched_item()
    {
        Guid runId = DomainId.New();
        string firstBody = SweepDraftTask.ComposeNew(
            DomainId.New(), DomainId.New(), [Route(location: "Foo.cs:12")], Now, DomainId.New()).AgentContext!;

        string updated = SweepDraftTask.Append(
            firstBody,
            [
                Route(runId: runId, cycle: 1, findingsFile: "/runs/a/review-1-findings.md", location: "src/a/Foo.cs:12"),
                Route(runId: runId, cycle: 1, findingsFile: "/runs/b/review-1-findings.md", location: "src/b/Foo.cs:12"),
            ]);

        CountOccurrences(updated, "### Foo.cs:12").Should().Be(1, "both still describe the one defect");
        CountOccurrences(updated, $"Run {runId}").Should().Be(
            2, "each distinct finding earns its own evidence entry even though both share a run and cycle");
    }

    /// <summary>
    /// A reviewer-authored <c>Location</c> reaches the sweep's own markdown structure as a bare
    /// heading, outside every fence — unlike a finding's text, which <see cref="Render"/> always
    /// fences. Left unsanitized it could carry a closing-keyword instruction into whatever later
    /// reads the rendered document as text (cycle-4 adversarial review); it gets the same
    /// treatment <c>ReviewDraftBugTask</c> already gives the identical field.
    /// </summary>
    [Fact]
    public void A_location_carrying_a_closing_keyword_is_defused_in_the_rendered_heading()
    {
        SweepFindingRoute route = new(
            new ReviewFinding(
                ReviewSeverity.Low, ReviewFindingScope.OutOfScope, "Foo.cs:12 — also, closes #500",
                "FINDING: severity=low; scope=out-of-scope; at=Foo.cs:12 — also, closes #500\n"
                + "Defect: a stale comment misleads the next reader."),
            DomainId.New(), 1, "/runs/x/review-1-findings.md");

        string body = SweepDraftTask.ComposeNew(DomainId.New(), DomainId.New(), [route], Now, DomainId.New()).AgentContext!;
        string heading = body.Split('\n').Single(line => line.StartsWith("### ", StringComparison.Ordinal));

        heading.Should().NotContain(
            "closes #500", "the bare keyword-plus-reference shape must not survive into the heading, " +
            "even though the same words inside the finding's own fence are left alone");
    }

    /// <summary>
    /// A location containing a backtick, rendered bare as a "### " heading, leaves an unpaired
    /// backtick that CommonMark can pair with a later run further down the document, turning
    /// everything in between into an unintended code span (cycle-4 adversarial review). The
    /// backtick is now backslash-escaped, which CommonMark reads as a literal character.
    /// </summary>
    [Fact]
    public void A_location_containing_a_backtick_is_escaped_rather_than_left_to_pair_with_a_later_run()
    {
        SweepFindingRoute route = new(
            new ReviewFinding(
                ReviewSeverity.Low, ReviewFindingScope.OutOfScope, "Foo`.cs:12",
                "FINDING: severity=low; scope=out-of-scope; at=Foo`.cs:12\nDefect: a stale comment misleads the next reader."),
            DomainId.New(), 1, "/runs/x/review-1-findings.md");

        string body = SweepDraftTask.ComposeNew(DomainId.New(), DomainId.New(), [route], Now, DomainId.New()).AgentContext!;

        body.Should().Contain("### Foo\\`.cs:12").And.NotContain("### Foo`.cs:12");
    }

    [Fact]
    public void An_ungraded_findings_severity_renders_as_not_graded_and_round_trips()
    {
        SweepFindingRoute ungraded = new(
            new ReviewFinding(ReviewSeverity.Unknown, ReviewFindingScope.OutOfScope, "Old.cs:1", "Defect: something."),
            DomainId.New(), 1, "/runs/x/review-1-findings.md");

        string body = SweepDraftTask.ComposeNew(DomainId.New(), DomainId.New(), [ungraded], Now, DomainId.New()).AgentContext!;

        body.Should().Contain("Severity: not graded");

        string updated = SweepDraftTask.Append(body, [Route()]);
        updated.Should().Contain("Severity: not graded", "the parsed item survives a re-render unchanged");
    }

    [Fact]
    public void Appending_to_a_blank_existing_context_behaves_like_composing_new()
    {
        string updated = SweepDraftTask.Append(null, [Route()]);

        updated.Should().Contain("### Cosmetic.cs:4").And.Contain("Severity: Low");
    }

    /// <summary>
    /// An excerpt whose own prose starts and ends with a backtick (a finding quoting a symbol at
    /// each end, e.g. <c>ReviewDraftBugTask.Excerpt</c> after its leading "- " strip) merges the
    /// fence's backticks with the excerpt's own on a naive render — the round-trip corruption the
    /// cycle-2 adversarial review found. The fence and excerpt are now always space-separated, so
    /// the excerpt's own backticks survive an append unmutated.
    /// </summary>
    [Fact]
    public void An_excerpt_that_starts_and_ends_with_a_backtick_round_trips_unmutated()
    {
        SweepFindingRoute route = new(
            new ReviewFinding(
                ReviewSeverity.Low, ReviewFindingScope.OutOfScope, "Foo.cs:12",
                "FINDING: severity=low; scope=out-of-scope; at=Foo.cs:12\n`Foo.cs:12` is stale, see `Bar`"),
            DomainId.New(), 1, "/runs/x/review-1-findings.md");

        string body = SweepDraftTask.ComposeNew(DomainId.New(), DomainId.New(), [route], Now, DomainId.New()).AgentContext!;

        body.Should().Contain("`Foo.cs:12` is stale, see `Bar`");

        string updated = SweepDraftTask.Append(body, [Route()]);
        updated.Should().Contain(
            "`Foo.cs:12` is stale, see `Bar`", "the excerpt's own backticks survive a re-render unmutated");
    }

    /// <summary>
    /// An excerpt ending in a backtick but not starting with one (the asymmetric case the
    /// adversarial review traced) previously grew a wider fence on every append instead of ever
    /// being recognized as already fenced.
    /// </summary>
    [Fact]
    public void An_excerpt_that_only_ends_with_a_backtick_does_not_accumulate_fences_on_repeated_appends()
    {
        SweepFindingRoute route = new(
            new ReviewFinding(
                ReviewSeverity.Low, ReviewFindingScope.OutOfScope, "Foo.cs:12",
                "FINDING: severity=low; scope=out-of-scope; at=Foo.cs:12\nDefect: the comment above `Foo`"),
            DomainId.New(), 1, "/runs/x/review-1-findings.md");

        string body = SweepDraftTask.ComposeNew(DomainId.New(), DomainId.New(), [route], Now, DomainId.New()).AgentContext!;
        string firstAppend = SweepDraftTask.Append(
            body, [Route(runId: DomainId.New(), cycle: 2, location: "Foo.cs:12")]);
        string secondAppend = SweepDraftTask.Append(
            firstAppend, [Route(runId: DomainId.New(), cycle: 3, location: "Foo.cs:12")]);

        secondAppend.Should().Contain("Defect: the comment above `Foo`")
            .And.NotContain("````", "the fence must never widen across repeated re-renders");
        CountOccurrences(secondAppend, "```").Should().Be(
            2, "one opening and one closing fence for the one item, not a widening pair from each append");
    }

    /// <summary>
    /// An item used to store only <c>ReviewDraftBugTask.Excerpt</c> — the finding's own first
    /// line, hard-truncated at 140 characters — which always dropped the <c>Scenario:</c> line
    /// the review contract mandates for every finding. The sweep now stores the finding's own
    /// text whole, the same as a routed medium's own draft bug task does.
    /// </summary>
    [Fact]
    public void The_findings_full_text_including_its_scenario_line_survives_into_the_rendered_body()
    {
        SweepFindingRoute route = new(
            new ReviewFinding(
                ReviewSeverity.Low, ReviewFindingScope.OutOfScope, "Foo.cs:12",
                "FINDING: severity=low; scope=out-of-scope; at=Foo.cs:12\n"
                + "Defect: the XML doc is stale.\n"
                + "Scenario: a reader trusting the comment files a duplicate."),
            DomainId.New(), 1, "/runs/x/review-1-findings.md");

        string body = SweepDraftTask.ComposeNew(DomainId.New(), DomainId.New(), [route], Now, DomainId.New()).AgentContext!;

        body.Should().Contain("Scenario: a reader trusting the comment files a duplicate.");

        string updated = SweepDraftTask.Append(body, [Route(runId: DomainId.New(), cycle: 2, location: "Foo.cs:12")]);
        updated.Should().Contain(
            "Scenario: a reader trusting the comment files a duplicate.",
            "the scenario line survives a re-render, not just the first append");
    }

    /// <summary>
    /// A document truncated mid-fence — hand-edited, or simply cut off before the closing fence
    /// was ever written — used to make <c>Parse</c> flush the item with an empty finding text,
    /// so the very next append would silently erase it (Copilot review, PR #87). EOF now closes
    /// an open fence implicitly instead.
    /// </summary>
    [Fact]
    public void An_unterminated_fence_at_end_of_document_is_treated_as_closed_at_EOF()
    {
        SweepFindingRoute route = new(
            new ReviewFinding(
                ReviewSeverity.Low, ReviewFindingScope.OutOfScope, "Foo.cs:12",
                "FINDING: severity=low; scope=out-of-scope; at=Foo.cs:12\n"
                + "Defect: a stale comment misleads the next reader."),
            DomainId.New(), 1, "/runs/x/review-1-findings.md");

        string body = SweepDraftTask.ComposeNew(DomainId.New(), DomainId.New(), [route], Now, DomainId.New()).AgentContext!;
        int closingFenceIndex = body.LastIndexOf("```", StringComparison.Ordinal);
        string truncated = body[..closingFenceIndex].TrimEnd('\n');

        string updated = SweepDraftTask.Append(truncated, [Route(runId: DomainId.New(), cycle: 2, location: "Bar.cs:9")]);

        updated.Should().Contain(
            "Defect: a stale comment misleads the next reader.",
            "a document truncated mid-fence must not erase the finding text on the next append");
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static SweepFindingRoute Route(
        Guid? runId = null, int cycle = 1, string findingsFile = "/runs/x/review-1-findings.md", string location = "Cosmetic.cs:4") =>
        new(
            new ReviewFinding(
                ReviewSeverity.Low, ReviewFindingScope.OutOfScope, location,
                $"FINDING: severity=low; scope=out-of-scope; at={location}\nDefect: a stale comment misleads the next reader."),
            runId ?? DomainId.New(), cycle, findingsFile);
}

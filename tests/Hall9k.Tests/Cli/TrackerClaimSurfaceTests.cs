using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// How a claim gate's hold reads on the board (idea 64c75e43, Decisions Log #142). The sentences
/// live in one record on purpose — the dispatcher composes them from a read it just made, and the
/// CLI composes them from the measurement the dispatcher published — so what is pinned here is
/// that both directions produce the same words, and that a row says nothing about a tracker
/// nobody looked at.
/// </summary>
public sealed class TrackerClaimSurfaceTests
{
    private static readonly DateTimeOffset Observed = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_card_somebody_else_holds_names_the_key_the_holder_and_the_lever()
    {
        TrackerClaimDecision decision = HeldByOther();

        decision.Passes.Should().BeFalse();
        decision.Holds.Should().BeTrue();
        decision.ReasonLine.Should().Be(
            "waiting for Jira to show PROJ-14 assigned to you — Jane Doe holds it");
        decision.RefusalLine.Should()
            .Contain("Jira shows PROJ-14 assigned to Jane Doe")
            .And.Contain("(5b10)", "the identity compared against is stated, since it is not a name anyone typed")
            .And.Contain("claim gate is tracker-assignee")
            .And.Contain("https://hall9k.atlassian.net/browse/PROJ-14", "the lever is one click");
    }

    [Fact]
    public void A_card_nobody_holds_says_so_rather_than_naming_a_holder()
    {
        TrackerClaimDecision decision = Unassigned();

        decision.ReasonLine.Should().Be(
            "waiting for GitHub to show Hallmanac/hall9k#251 assigned to you — nobody holds it");
        decision.RefusalLine.Should()
            .Contain("assigned to nobody")
            .And.Contain("Assign Hallmanac/hall9k#251 to yourself");
    }

    /// <summary>
    /// The unreadable path is the only one whose row line carries the error and the lever: the
    /// tracker's own sentence is the whole point of it, and a reader has no other way to see it.
    /// </summary>
    [Fact]
    public void An_unreadable_tracker_quotes_its_error_verbatim_and_says_what_ends_the_hold()
    {
        TrackerClaimDecision decision = Unreadable(
            "hall9k.atlassian.net rejected the credentials for brian@example.com.",
            authenticationRefusal: true,
            lever: "The hold ends when the credentials work again — renew the token and register the "
                + "connection again: h9k connection add jira --site https://hall9k.atlassian.net "
                + "--email brian@example.com");

        decision.Holds.Should().BeTrue("the gate fails closed on anything it could not read");
        decision.AuthenticationRefusal.Should().BeTrue();
        decision.ReasonLine.Should()
            .Contain("Jira could not be read, so the gate fails closed")
            .And.Contain("rejected the credentials for brian@example.com.")
            .And.Contain("h9k connection add jira --site");
        decision.RefusalLine.Should().Contain("Jira reported: hall9k.atlassian.net rejected the credentials");
    }

    [Fact]
    public void An_outage_names_a_different_lever_from_a_credential_refusal()
    {
        TrackerClaimDecision outage = Unreadable(
            "hall9k.atlassian.net failed while trying to read the assignee of PROJ-14 (HTTP 503).",
            authenticationRefusal: false,
            lever: "Nothing on this install can end the hold — wait out the outage; the next sweep reads "
                + "again on its own.");

        outage.AuthenticationRefusal.Should().BeFalse();
        outage.ReasonLine.Should().Contain("wait out the outage").And.NotContain("renew the token");
    }

    [Fact]
    public void Nothing_gated_says_nothing_at_all()
    {
        TrackerClaimDecision.NotGated.Passes.Should().BeTrue();
        TrackerClaimDecision.NotGated.Holds.Should().BeFalse();
        TrackerClaimDecision.NotGated.ReasonLine.Should().BeEmpty();
        TrackerClaimDecision.NotGated.RefusalLine.Should().BeEmpty();
    }

    /// <summary>
    /// The daemon writes the hold and the CLI reads it, so a round trip has to come back saying the
    /// same thing — including which of the three verdicts it was, which is reconstructed from the
    /// same two fields that distinguished it when it was written.
    /// </summary>
    [Theory]
    [InlineData("held")]
    [InlineData("unassigned")]
    [InlineData("unreadable")]
    public void A_published_hold_reads_back_as_the_decision_that_wrote_it(string shape)
    {
        TrackerClaimDecision original = shape switch
        {
            "held" => HeldByOther(),
            "unassigned" => Unassigned(),
            _ => Unreadable("Jira said no.", authenticationRefusal: true, lever: "Renew the token."),
        };
        Guid taskId = DomainId.New();
        Guid nodeId = DomainId.New();

        TrackerClaimHold hold = original.ToHold(taskId, nodeId, "this-machine");
        TrackerClaimDecision roundTripped = TrackerClaimDecision.FromHold(hold);

        hold.Id.Should().Be(
            TrackerClaimHold.KeyFor(taskId, nodeId),
            "the key carries the node as well as the task, so two nodes against one database keep "
            + "their own explanations of the same task rather than overwriting each other");
        hold.TaskId.Should().Be(taskId);
        hold.NodeId.Should().Be(nodeId);
        hold.MachineName.Should().Be("this-machine");
        roundTripped.Verdict.Should().Be(original.Verdict);
        roundTripped.ReasonLine.Should().Be(original.ReasonLine);
        roundTripped.RefusalLine.Should().Be(original.RefusalLine);
    }

    [Fact]
    public void A_queued_row_held_by_the_tracker_carries_the_reason_and_the_flag_the_queue_section_reads()
    {
        TaskListItem queued = StatusFixtures.Task(TaskState.Queued);

        TaskStatusRow unheld = StatusFixtures.Compose(queued);
        unheld.WaitingForTracker.Should().BeFalse("nothing observed a tracker wait");
        unheld.Facts.Should().ContainSingle().Which.Should().NotContain("tracker");

        TaskStatusRow held = StatusFixtures.Compose(
            queued, trackerHold: HeldByOther().ToHold(queued.Id, DomainId.New(), StatusFixtures.ThisMachine));

        held.WaitingForTracker.Should().BeTrue();
        held.Facts.Should().HaveCount(2);
        held.Facts[1].Should().Be("waiting for Jira to show PROJ-14 assigned to you — Jane Doe holds it");
    }

    /// <summary>
    /// A card somebody else holds will not be claimed here whatever the ceiling does, so it is the
    /// more specific answer to "why is this not moving" — and it comes first, because the browse
    /// surfaces show only the row's first detail line.
    /// </summary>
    [Fact]
    public void The_tracker_hold_is_stated_ahead_of_the_slot_line_when_both_apply()
    {
        TaskListItem queued = StatusFixtures.Task(TaskState.Queued);

        TaskStatusRow row = StatusFixtures.Compose(
            queued,
            pressure: new DispatchPressure(LiveRuns: 3, MaxConcurrentRuns: 3),
            trackerHold: HeldByOther().ToHold(queued.Id, DomainId.New(), StatusFixtures.ThisMachine));

        row.WaitingForTracker.Should().BeTrue();
        row.WaitingForSlot.Should().BeTrue();
        row.Facts.Should().HaveCount(3);
        row.Facts[1].Should().Contain("waiting for Jira");
        row.Facts[2].Should().Be("waiting for a slot — node 3 of 3 running");
    }

    /// <summary>
    /// A hold only ever describes a task the dispatcher passed over. A task that has since been
    /// claimed — interactively, or on another machine — is not waiting for anything, and the same
    /// gate <see cref="TaskStatusRow.WaitingForSlot"/> already applies keeps a stale document from
    /// explaining a wait that has ended.
    /// </summary>
    [Fact]
    public void A_task_that_is_no_longer_queued_reads_no_tracker_hold()
    {
        TaskListItem claimed = StatusFixtures.Task(TaskState.Claimed, runId: DomainId.New());

        TaskStatusRow row = StatusFixtures.Compose(
            claimed,
            run: StatusFixtures.Run(claimed.CurrentRunId!.Value, RunState.Running),
            trackerHold: HeldByOther().ToHold(claimed.Id, DomainId.New(), StatusFixtures.ThisMachine));

        row.WaitingForTracker.Should().BeFalse();
    }

    /// <summary>
    /// Every field these sentences interpolate that Hall9k did not author — the reference recorded
    /// on the task, this install's own identity as the tracker spelled it, the holder — is relayed
    /// text on its way into a terminal and into a one-line row, so none of it can print a line of
    /// its own choosing or reverse the one it is in (independent pre-PR review, cycle 1,
    /// adversarial lens).
    /// </summary>
    [Fact]
    public void A_reference_or_an_identity_a_terminal_would_obey_cannot_break_the_line_it_is_framed_in()
    {
        TrackerClaimDecision decision = new(
            TrackerClaimVerdict.Unassigned,
            "jira",
            "PROJ-14\nassigned to you",
            null,
            "5b10‮id",
            null,
            null,
            false,
            "Assign it to yourself in the tracker and the claim proceeds on its own.",
            Observed);

        decision.ReasonLine.Should().NotContainAny("\n", "‮")
            .And.Contain("PROJ-14 assigned to you");
        decision.RefusalLine.Should().NotContainAny("\n", "‮")
            .And.Contain("(5b10id)");
    }

    /// <summary>
    /// Two daemons against one database (PLAN.md §6.1's roadmap #5) are refused for the same gated
    /// task on their own tracker identities, and each machine's board reads only its own node's
    /// holds — so the two measurements have to be able to sit side by side rather than the second
    /// sweep erasing the first machine's explanation (independent pre-PR review, cycle 1,
    /// adversarial lens).
    /// </summary>
    [Fact]
    public void Two_nodes_holding_the_same_task_publish_two_rows_rather_than_overwriting_one()
    {
        Guid taskId = DomainId.New();

        TrackerClaimHold here = HeldByOther().ToHold(taskId, DomainId.New(), "this-machine");
        TrackerClaimHold elsewhere = HeldByOther().ToHold(taskId, DomainId.New(), "the-other-machine");

        here.Id.Should().NotBe(elsewhere.Id);
        here.TaskId.Should().Be(taskId).And.Be(elsewhere.TaskId, "both are about the same task");
    }

    private static TrackerClaimDecision HeldByOther() => new(
        TrackerClaimVerdict.HeldByOther,
        "jira",
        "PROJ-14",
        new Uri("https://hall9k.atlassian.net/browse/PROJ-14"),
        "5b10",
        "Jane Doe",
        null,
        false,
        "Assign PROJ-14 to yourself in the tracker and the claim proceeds on its own: "
        + "https://hall9k.atlassian.net/browse/PROJ-14",
        Observed);

    private static TrackerClaimDecision Unassigned() => new(
        TrackerClaimVerdict.Unassigned,
        "github",
        "Hallmanac/hall9k#251",
        new Uri("https://github.com/Hallmanac/hall9k/issues/251"),
        "Hallmanac",
        null,
        null,
        false,
        "Assign Hallmanac/hall9k#251 to yourself in the tracker and the claim proceeds on its own: "
        + "https://github.com/Hallmanac/hall9k/issues/251",
        Observed);

    private static TrackerClaimDecision Unreadable(string error, bool authenticationRefusal, string lever) => new(
        TrackerClaimVerdict.Unreadable,
        "jira",
        "PROJ-14",
        new Uri("https://hall9k.atlassian.net/browse/PROJ-14"),
        null,
        null,
        error,
        authenticationRefusal,
        lever,
        Observed);
}

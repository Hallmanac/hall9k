using System.ComponentModel;
using System.Reflection;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The parts of the reassignment that need no database: who counts as attended, which holders a
/// fresh read still matches, the gh argument list, the sentences, how the names reach a terminal, and
/// the event's registrations. The offer itself, against recorded trackers, is in
/// <c>TrackerAssignmentTests</c> and <c>LinearClaimGateTests</c>.
/// </summary>
public sealed class TrackerReassignmentTests
{
    private static readonly DateTimeOffset Observed = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid ThisNode = new("0191f0a0-0000-7000-8000-00000000a001");
    private static readonly Guid OtherNode = new("0191f0a0-0000-7000-8000-00000000b002");

    // ── attended ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_person_at_a_terminal_with_none_of_the_session_markers_is_attended()
    {
        AttendedSession.IsAttended(interactive: true, _ => null).Should().BeTrue();
        AttendedSession.IsAttended(interactive: true, _ => string.Empty).Should().BeTrue("a blank value is not a marker");
    }

    [Fact]
    public void A_console_that_is_not_a_terminal_is_never_attended()
    {
        AttendedSession.IsAttended(interactive: false, _ => null).Should().BeFalse();
    }

    [Theory]
    [InlineData("HALL9K_DISPATCHED_RUN_ID")]
    [InlineData("HALL9K_DETACHED_SESSION")]
    [InlineData("CLAUDE_PID")]
    public void Each_session_marker_alone_makes_a_terminal_unattended(string marker)
    {
        Func<string, string?> environment = name => name == marker ? "4242" : null;

        AttendedSession.IsAttended(interactive: true, environment).Should().BeFalse();
        TaskAssignCommand.ReassignDoorFor(HeldTask(null), ThisNode, interactive: true, environment)
            .Should().BeNull("an unattended run, a dispatched session and an orchestrator get no reassign offer");
    }

    [Fact]
    public void The_markers_are_exactly_the_three_the_sessions_hall9k_and_claude_code_stamp()
    {
        AttendedSession.UnattendedMarkers.Should().BeEquivalentTo(
            ["HALL9K_DISPATCHED_RUN_ID", "HALL9K_DETACHED_SESSION", "CLAUDE_PID"]);
    }

    [Fact]
    public void An_attended_door_carries_this_node_and_the_ledger_holder_it_was_given()
    {
        TrackerClaimCheck.ReassignDoor door = TaskAssignCommand.ReassignDoorFor(
            HeldTask(OtherNode), ThisNode, interactive: true, _ => null)!;

        door.ThisNodeId.Should().Be(ThisNode);
        door.HolderNodeId.Should().Be(OtherNode);
        door.HeldByAnotherNode.Should().BeTrue();
        (door with { HolderNodeId = ThisNode }).HeldByAnotherNode.Should().BeFalse("this node's own holder is no obstacle");
        (door with { HolderNodeId = null }).HeldByAnotherNode.Should().BeFalse();
    }

    // ── the holders a fresh read must still show ──────────────────────────────────────────────

    [Fact]
    public void Holders_are_matched_by_identity_with_the_providers_own_comparison_and_never_by_name()
    {
        TrackerAssignee jane = new("acct-jane", "Jane");
        TrackerAssignee john = new("acct-john", "John");

        Held([jane, john], StringComparison.Ordinal).HoldersAre([john, jane]).Should().BeTrue("order is not identity");
        Held([jane], StringComparison.Ordinal).HoldersAre([jane, john]).Should().BeFalse("a fresh read with fewer holders is a different item");
        Held([jane, john], StringComparison.Ordinal).HoldersAre([jane]).Should().BeFalse("a holder the operator was never shown");
        Held([new("acct-other", "Jane")], StringComparison.Ordinal).HoldersAre([jane])
            .Should().BeFalse("the same display name on another account is another person");
        Held([new("ACCT-JANE", "Jane")], StringComparison.Ordinal).HoldersAre([jane])
            .Should().BeFalse("a Jira account id is opaque and its case is significant");
        Held([new("Teammate", null)], StringComparison.OrdinalIgnoreCase).HoldersAre([new("teammate", null)])
            .Should().BeTrue("a GitHub login is matched without regard to case, as the gate matches it");
        Held([jane], StringComparison.Ordinal).HoldersAre([]).Should().BeFalse("nobody was shown");
    }

    [Fact]
    public void Only_a_held_read_can_match_the_holders()
    {
        TrackerClaimDecision unassigned = Held([], StringComparison.Ordinal) with { Verdict = TrackerClaimVerdict.Unassigned };

        unassigned.HoldersAre([new("acct-jane", "Jane")]).Should().BeFalse();
    }

    // ── the one gh call ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_github_write_is_one_edit_adding_the_operator_and_removing_every_holder()
    {
        GitHubWorkItemProvider.ReplaceAssigneesArguments("Hallmanac/hall9k", 42, "me", ["teammate", "another"])
            .Should().Equal(
                "issue", "edit", "42", "--repo", "Hallmanac/hall9k", "--add-assignee", "me",
                "--remove-assignee", "teammate", "--remove-assignee", "another");
    }

    [Fact]
    public async Task A_github_write_that_fails_reports_gh_and_says_the_holders_were_left()
    {
        RecordingProcessRunner gh = RecordingProcessRunner.Failing("HTTP 422: not assignable");
        GitHubWorkItemProvider issues = new(gh.Runner);

        TrackerAssignmentWrite write = await issues.ReplaceAssigneesAsync(
            new ExternalReference(WorkItemProvider.GitHub, "Hallmanac/hall9k#42"), "me", ["teammate"],
            Path.GetTempPath(), CancellationToken.None);

        gh.Calls.Should().ContainSingle("a refused write is one call, so nothing was half done");
        write.Error.Should().Contain("HTTP 422: not assignable").And.Contain("current holders were left on it");
    }

    // ── the sentences ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_held_refusal_and_reason_point_at_the_door_that_offers_the_reassignment()
    {
        TrackerClaimDecision decision = Held([new("acct-jane", "Jane Doe")], StringComparison.Ordinal);

        decision.RefusalLine.Should().Contain("h9k task queue, run at a terminal, offers to reassign the item to you");
        decision.RefusalLine.Should().NotContain("nothing on this install claims the task while somebody else holds");
        decision.ReasonLine.Should().Contain("h9k task queue at a terminal offers to reassign it to you");
    }

    [Fact]
    public void The_take_refusal_for_a_held_item_no_longer_says_that_no_flag_takes_it_from_a_person()
    {
        TrackerTake held = TrackerTake.From(Held([new("acct-jane", "Jane Doe")], StringComparison.Ordinal));

        held.Verdict.Should().Be(TrackerTakeVerdict.HeldByOther);
        held.RefusalLine.Should().Contain("only when an operator at a terminal confirms it")
            .And.Contain("h9k task queue and h9k task assign --take")
            .And.Contain("no flag does it, and nothing unattended does")
            .And.NotContain("there is no flag that does")
            .And.NotContain("never takes one from another person");
    }

    [Fact]
    public void A_ledger_holder_on_another_node_is_named_with_the_cooperative_take_in_the_held_line()
    {
        TrackerClaimDecision decision = Held([new("acct-jane", "Jane Doe")], StringComparison.Ordinal) with
        {
            HeldElsewhere = "This task is held by node 0191f0a0 in the ledger.",
        };

        decision.RefusalLine.Should().Contain("This task is held by node 0191f0a0 in the ledger.");
        TrackerTake.From(decision).RefusalLine.Should().Contain("This task is held by node 0191f0a0 in the ledger.");
    }

    [Fact]
    public void An_unconfirmed_or_contested_reassignment_says_in_its_own_sentence_the_holders_may_be_gone()
    {
        TrackerClaimDecision decision = Held([new("acct-jane", "Jane Doe")], StringComparison.Ordinal);
        IReadOnlyList<TrackerAssignee> replaced = [new("acct-jane", "Jane Doe")];

        TrackerTake unconfirmed = TrackerTake.Unconfirmed(decision, "it shows the item assigned to nobody.", Observed)
            .AsReassignmentOf(replaced);
        TrackerTake contested = TrackerTake.Contested(decision, "Late Comer", Observed).AsReassignmentOf(replaced);

        unconfirmed.RefusalLine.Should().Contain("Jane Doe").And.Contain("may be gone from the item");
        contested.RefusalLine.Should().Contain("Jane Doe").And.Contain("Late Comer").And.Contain("may be gone from the item")
            .And.NotContain("assigned to nobody").And.NotContain("never removes an assignment");
    }

    [Fact]
    public void A_take_that_wrote_becomes_a_reassignment_and_passes_while_the_others_keep_their_verdict()
    {
        TrackerClaimDecision decision = Held([new("acct-jane", "Jane Doe")], StringComparison.Ordinal);
        TrackerAssignee me = new("acct-me", "Me");

        TrackerTake reassigned = TrackerTake.Taken(decision, me, Observed).AsReassignmentOf([new("acct-jane", "Jane Doe")]);

        reassigned.Verdict.Should().Be(TrackerTakeVerdict.Reassigned);
        reassigned.Passes.Should().BeTrue();
        reassigned.Wrote.Should().BeTrue();
        reassigned.TookLine.Should().Contain("It replaced Jane Doe");
        TrackerTake.HolderChanged(decision, [new("acct-jane", "Jane Doe")]).Passes.Should().BeFalse();
    }

    // ── what reaches a terminal ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_replaced_persons_name_is_relayed_text_made_one_line_and_escaped_for_the_console()
    {
        TrackerAssignmentReassigned reassigned = new(
            DomainId.New(),
            "PROJ-14",
            [new ReplacedTrackerAssignee("acct-1", "Jane [red]Doe[/]\nsecond line"), new ReplacedTrackerAssignee("acct-2", null)],
            "acct-me",
            "Me",
            Observed);

        string markup = TaskShowCommand.ReassignmentMarkup(reassigned);

        markup.Should().Contain("Jane [[red]]Doe[[/]]").And.NotContain("\n");
        markup.Should().Contain("acct-2", "a holder with no name is shown by the identity the tracker gave");
        markup.Should().Contain("to Me");
    }

    // ── registrations ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_event_is_project_scoped_and_member_safe()
    {
        EventScopeRegistry.ClassificationOf(typeof(TrackerAssignmentReassigned)).Should().Be(EventScope.ProjectScoped);
        TaskActClassificationRegistry.TryClassificationOf(typeof(TrackerAssignmentReassigned))
            .Should().Be(TaskActClassification.MemberSafe);
    }

    // ── the help ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(typeof(TaskQueueCommand.Settings), "Take")]
    [InlineData(typeof(TaskAssignCommand.Settings), "Take")]
    [InlineData(typeof(ProjectSetCommand.Settings), "ClaimGate")]
    public void The_help_names_the_interactive_confirm_and_never_says_no_flag_takes_an_item_from_a_person(
        Type settings, string property)
    {
        string description = settings.GetProperty(property)!.GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should().Contain("interactive confirm").And.Contain("nothing unattended does")
            .And.NotContain("no flag that takes")
            .And.NotContain("no flag takes one");
        if (property == "Take")
        {
            description.Should().Contain("Linear issue", "the flag works for every gated tracker");
        }
    }

    // ── seams ─────────────────────────────────────────────────────────────────────────────────

    private static TrackerClaimDecision Held(IReadOnlyList<TrackerAssignee> holders, StringComparison comparison) => new(
        TrackerClaimVerdict.HeldByOther,
        "jira",
        "PROJ-14",
        new Uri("https://hall9k.atlassian.net/browse/PROJ-14"),
        "acct-me",
        string.Join(", ", holders.Select(holder => holder.Described)),
        null,
        false,
        "Assign PROJ-14 to yourself in the tracker and the claim proceeds on its own.",
        Observed)
    {
        Holders = holders,
        HolderComparison = comparison,
    };

    private static TaskAggregate HeldTask(Guid? holderNodeId)
    {
        Guid ownerId = DomainId.New();
        Guid taskId = DomainId.New();
        TaskAdded added = TaskDecider.Add(
            taskId, DomainId.New(), "held", ["done"], TaskType.Chore, null, null,
            new ExternalReference(WorkItemProvider.Jira, "PROJ-14"), Observed, ownerId);
        (TaskAggregate task, _) = TaskSeed.Start(added, ownerId, Observed);
        if (holderNodeId is { } node)
        {
            task.Apply(new TaskClaimed(taskId, node, ownerId, 1, DomainId.New(), Observed, OwnerRootFingerprint: "root"));
        }

        return task;
    }
}

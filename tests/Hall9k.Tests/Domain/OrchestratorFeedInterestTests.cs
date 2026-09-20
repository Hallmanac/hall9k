using FluentAssertions;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Infrastructure.Persistence;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The orchestrator feed's interest filter (idea 89471598, piece 2): one deterministic table
/// from event type to level, the three bands' membership, and the one rule that reads a payload
/// rather than a type — a message from a person is admitted at every band.
/// </summary>
public sealed class OrchestratorFeedInterestTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 19, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Every_type_the_feed_names_is_a_real_event_type_this_platform_ships()
    {
        // The interest table is deliberately not exhaustive over every event type — it is a
        // curated list, and a type it does not name is simply not in the feed. The other
        // direction still has to hold: an entry naming a type that no longer ships is a stale
        // entry, the same drift EventScopeRegistryTests guards for its own table.
        OrchestratorFeedInterest.InterestingEventTypes.Should()
            .BeSubsetOf(EventScopeRegistry.KnownEventTypes);
    }

    [Fact]
    public void A_type_the_table_does_not_name_is_not_in_the_feed_at_any_level()
    {
        OrchestratorFeedInterest.BandOf(typeof(TokensRecorded)).Should().BeNull();

        foreach (OrchestratorFeedLevel level in OrchestratorFeedLevel.All)
        {
            OrchestratorFeedInterest
                .Admits(new TokensRecorded(Guid.NewGuid(), 1_000, 200, null, At), level)
                .Should().BeFalse($"{level.Value} reads the same table as every other band");
        }
    }

    [Theory]
    // Parks and disputes.
    [InlineData(typeof(ReviewParked))]
    [InlineData(typeof(CloseoutParked))]
    [InlineData(typeof(ReviewDisagreementParked))]
    [InlineData(typeof(HumanThreadReplyParked))]
    [InlineData(typeof(ReviewThreadReplyRefused))]
    [InlineData(typeof(ReviewFindingRouted))]
    [InlineData(typeof(QuestionAsked))]
    // Gate and run failures.
    [InlineData(typeof(VerificationFailed))]
    [InlineData(typeof(SettlingGateRepairCapReached))]
    [InlineData(typeof(PullRequestChecksFailed))]
    [InlineData(typeof(RunFailed))]
    [InlineData(typeof(RunKilled))]
    [InlineData(typeof(RunBudgetExhausted))]
    [InlineData(typeof(ReviewErrored))]
    // A merge that stays failed.
    [InlineData(typeof(PullRequestAutoMergeAttempted))]
    // Daemon trouble.
    [InlineData(typeof(RunSessionErrorRetried))]
    [InlineData(typeof(RunUncommittedWorkRecoveryAttempted))]
    [InlineData(typeof(RunUnattendedExitFlagged))]
    [InlineData(typeof(RunRecordReconstructed))]
    [InlineData(typeof(RunLaunchHeld))]
    // A message from a person or another node's window.
    [InlineData(typeof(MessageReceived))]
    public void The_actionable_band_is_what_somebody_is_owed(Type eventType) =>
        OrchestratorFeedInterest.BandOf(eventType).Should().Be(OrchestratorFeedLevel.Actionable);

    [Theory]
    // The task lifecycle words h9k status prints.
    [InlineData(typeof(TaskPublished))]
    [InlineData(typeof(TaskClaimed))]
    [InlineData(typeof(PullRequestOpened))]
    [InlineData(typeof(TaskCompleted))]
    [InlineData(typeof(PullRequestMerged))]
    [InlineData(typeof(TaskResolved))]
    [InlineData(typeof(TaskFailed))]
    [InlineData(typeof(TaskAbandoned))]
    // Ideas logged or updated.
    [InlineData(typeof(IdeaCaptured))]
    [InlineData(typeof(IdeaRevised))]
    [InlineData(typeof(IdeaAssignedToProject))]
    [InlineData(typeof(IdeaTaskCut))]
    [InlineData(typeof(IdeaConcluded))]
    [InlineData(typeof(IdeaArchived))]
    [InlineData(typeof(IdeaSpikeConcluded))]
    // Retired (backlog 31) and still named: a project's own history carries them, and an entry
    // here is what keeps a drain over that history from going quiet about an idea's ending.
    [InlineData(typeof(IdeaDiscarded))]
    [InlineData(typeof(IdeaPromoted))]
    // Claims and takeovers involving another node.
    [InlineData(typeof(TaskHolderTakenOver))]
    [InlineData(typeof(TaskTakeRequested))]
    [InlineData(typeof(TaskTakeRefused))]
    [InlineData(typeof(TaskHolderReleased))]
    public void The_transitions_band_is_the_works_own_movement(Type eventType) =>
        OrchestratorFeedInterest.BandOf(eventType).Should().Be(OrchestratorFeedLevel.Transitions);

    [Theory]
    [InlineData(typeof(RunDispatched))]
    [InlineData(typeof(RunProcessStarted))]
    [InlineData(typeof(RunResumed))]
    [InlineData(typeof(AgentSessionCompleted))]
    [InlineData(typeof(GateStarted))]
    [InlineData(typeof(GateEnded))]
    [InlineData(typeof(VerificationPassed))]
    [InlineData(typeof(ReviewDispatched))]
    [InlineData(typeof(ReviewPassCompleted))]
    [InlineData(typeof(ReviewCompleted))]
    [InlineData(typeof(ReviewFixDispatched))]
    [InlineData(typeof(ReviewFixCompleted))]
    [InlineData(typeof(ReviewParkResolved))]
    [InlineData(typeof(ReviewBoundaryApproved))]
    [InlineData(typeof(ReviewSettled))]
    [InlineData(typeof(ReviewFeedbackReceived))]
    [InlineData(typeof(PullRequestUpdated))]
    [InlineData(typeof(PullRequestConflictObserved))]
    [InlineData(typeof(PullRequestClosed))]
    [InlineData(typeof(RunPhaseDelegated))]
    [InlineData(typeof(RunSuperseded))]
    [InlineData(typeof(RunCompleted))]
    public void The_everything_band_is_the_machinerys_own_movement(Type eventType) =>
        OrchestratorFeedInterest.BandOf(eventType).Should().Be(OrchestratorFeedLevel.Everything);

    [Fact]
    public void The_bands_nest_so_a_wider_level_admits_everything_a_narrower_one_does()
    {
        object park = new ReviewParked(Guid.NewGuid(), "a human owns the next move", At);
        object publish = new TaskPublished(Guid.NewGuid(), At, Guid.NewGuid());
        object phase = new RunCompleted(Guid.NewGuid(), At);

        OrchestratorFeedInterest.Admits(park, OrchestratorFeedLevel.Actionable).Should().BeTrue();
        OrchestratorFeedInterest.Admits(publish, OrchestratorFeedLevel.Actionable).Should().BeFalse();
        OrchestratorFeedInterest.Admits(phase, OrchestratorFeedLevel.Actionable).Should().BeFalse();

        OrchestratorFeedInterest.Admits(park, OrchestratorFeedLevel.Transitions).Should().BeTrue();
        OrchestratorFeedInterest.Admits(publish, OrchestratorFeedLevel.Transitions).Should().BeTrue();
        OrchestratorFeedInterest.Admits(phase, OrchestratorFeedLevel.Transitions).Should().BeFalse();

        OrchestratorFeedInterest.Admits(park, OrchestratorFeedLevel.Everything).Should().BeTrue();
        OrchestratorFeedInterest.Admits(publish, OrchestratorFeedLevel.Everything).Should().BeTrue();
        OrchestratorFeedInterest.Admits(phase, OrchestratorFeedLevel.Everything).Should().BeTrue();
    }

    [Fact]
    public void A_message_from_a_person_is_admitted_at_every_level()
    {
        MessageReceived note = Message(MessageKind.Note);

        foreach (OrchestratorFeedLevel level in OrchestratorFeedLevel.All)
        {
            OrchestratorFeedInterest.Admits(note, level).Should()
                .BeTrue($"a person's own note is owed an answer whatever {level.Value} filters out");
        }
    }

    [Fact]
    public void Another_nodes_window_nudging_a_handoff_is_a_message_too()
    {
        foreach (OrchestratorFeedLevel level in OrchestratorFeedLevel.All)
        {
            OrchestratorFeedInterest.Admits(Message(MessageKind.Handoff), level).Should().BeTrue();
        }
    }

    [Theory]
    [InlineData("claim-request")]
    [InlineData("claim-granted")]
    [InlineData("claim-refused")]
    public void The_daemons_own_machine_traffic_is_not_a_message_from_a_person(string kind)
    {
        // The identical exclusion h9k messages and h9k status's own unread count already apply:
        // these carry JSON for a reactor, never prose for a reader.
        foreach (OrchestratorFeedLevel level in OrchestratorFeedLevel.All)
        {
            OrchestratorFeedInterest.Admits(Message(MessageKind.Parse(kind)), level).Should().BeFalse();
        }
    }

    [Fact]
    public void A_merge_the_daemon_completed_itself_is_not_actionable_but_a_refused_one_is()
    {
        object succeeded = new PullRequestAutoMergeAttempted(Guid.NewGuid(), true, null, At);
        object refused = new PullRequestAutoMergeAttempted(Guid.NewGuid(), false, "not mergeable", At);

        OrchestratorFeedInterest.Admits(succeeded, OrchestratorFeedLevel.Everything).Should().BeFalse();
        OrchestratorFeedInterest.Admits(refused, OrchestratorFeedLevel.Actionable).Should().BeTrue();
    }

    [Fact]
    public void An_unrecognized_level_reads_as_wide_as_the_default_rather_than_silencing_the_feed()
    {
        OrchestratorFeedLevel fromAnOlderBuild = "SomethingThisBuildHasNeverHeardOf";

        fromAnOlderBuild.Breadth.Should().Be(OrchestratorFeedLevel.Default.Breadth);
        OrchestratorFeedInterest
            .Admits(new TaskPublished(Guid.NewGuid(), At, Guid.NewGuid()), fromAnOlderBuild)
            .Should().BeTrue();
    }

    private static MessageReceived Message(MessageKind kind) => new(
        FromNodeId: Guid.NewGuid(),
        Seq: 1,
        SentAt: At,
        FromOwnerFingerprint: "abcdef0123456789",
        To: "project",
        About: null,
        Kind: kind.Value,
        Body: "are you still on the stacked pair?",
        ReceivedAt: At,
        ProjectId: Guid.NewGuid());
}

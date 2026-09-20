using System.Runtime.CompilerServices;
using FluentAssertions;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks.Events;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Which feed items dispatch the courier at once regardless of the batching wait (idea 89471598,
/// piece 3): parks, disputes, daemon trouble, and a person's message — never a gate or run
/// failure, which the acceptance criteria deliberately leaves out of the urgent set.
/// </summary>
public sealed class OrchestratorFeedUrgencyTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);

    /// <summary>An instance of <paramref name="type"/> with no field values set — this predicate never reads a park or daemon-trouble event's own payload, only its type.</summary>
    private static object UninitializedInstanceOf(Type type) => RuntimeHelpers.GetUninitializedObject(type);

    [Theory]
    [InlineData(typeof(ReviewParked))]
    [InlineData(typeof(CloseoutParked))]
    [InlineData(typeof(ReviewDisagreementParked))]
    [InlineData(typeof(HumanThreadReplyParked))]
    [InlineData(typeof(ReviewThreadReplyRefused))]
    [InlineData(typeof(ReviewFindingRouted))]
    [InlineData(typeof(QuestionAsked))]
    [InlineData(typeof(RunSessionErrorRetried))]
    [InlineData(typeof(RunUncommittedWorkRecoveryAttempted))]
    [InlineData(typeof(RunUnattendedExitFlagged))]
    [InlineData(typeof(RunRecordReconstructed))]
    [InlineData(typeof(RunLaunchHeld))]
    public void A_park_a_dispute_or_daemon_trouble_is_urgent(Type type)
    {
        OrchestratorFeedUrgency.IsUrgent(type, UninitializedInstanceOf(type)).Should().BeTrue();
    }

    [Theory]
    [InlineData(typeof(VerificationFailed))]
    [InlineData(typeof(RunFailed))]
    [InlineData(typeof(RunKilled))]
    [InlineData(typeof(RunBudgetExhausted))]
    [InlineData(typeof(ReviewErrored))]
    [InlineData(typeof(PullRequestChecksFailed))]
    [InlineData(typeof(TaskPublished))]
    public void A_gate_or_run_failure_or_an_ordinary_transition_is_not_urgent(Type type)
    {
        // Deliberately narrower than OrchestratorFeedLevel.Actionable: these are retried
        // automatically and do not need a human paged the instant they land.
        OrchestratorFeedUrgency.IsUrgent(type, UninitializedInstanceOf(type)).Should().BeFalse();
    }

    [Fact]
    public void A_message_from_a_person_is_urgent()
    {
        MessageReceived fromAPerson = new(
            Guid.NewGuid(), 1, At, "fingerprint", "project", null, MessageKind.Note.Value, "are you there?", At,
            Guid.NewGuid());

        OrchestratorFeedUrgency.IsUrgent(typeof(MessageReceived), fromAPerson).Should().BeTrue();
    }
}

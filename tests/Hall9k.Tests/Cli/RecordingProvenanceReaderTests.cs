using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// Whether a human was attending the run a decision or a lesson was recorded from, read off the
/// run's own record plus one fact about the calling process (idea d805fd8b, piece 1). The whole
/// rule is pure — the environment read is an argument, not something the rule performs — so every
/// shape that matters is proved without a store and without touching this process's environment.
/// <para>
/// The shape this exists for is <c>h9k task delegate</c>: it spawns its contractor from inside the
/// operator's own attached session, so the contractor inherits <c>HALL9K_INTERACTIVE_RUN_ID</c>
/// naming a run it is not attended on, and it overwrites that run's recorded session name with the
/// build role for the rest of the claim. Reading the variable would call the contractor attended;
/// reading the session name refused the operator from the first delegation onward. What separates
/// them is <c>HALL9K_DETACHED_SESSION</c>, which only a spawned session carries.
/// </para>
/// </summary>
public sealed class RecordingProvenanceReaderTests
{
    private static readonly Guid TaskId = DomainId.New();
    private static readonly Guid NodeId = DomainId.New();

    [Fact]
    public void An_operators_own_attached_claim_reads_attended()
    {
        HumanAttendance attendance = RecordingProvenanceReader.Classify(
            Task(claimedByNodeId: Guid.Empty),
            Run(SessionRoleName.For(DomainId.Short(TaskId), SessionRoleName.InteractiveClaim)),
            detachedSessionName: null);

        attendance.Should().Be(HumanAttendance.Attended);
    }

    [Fact]
    public void A_re_entered_claim_renamed_to_its_real_session_name_still_reads_attended()
    {
        // h9k task register-session overwrites the recorded session name with the session's own
        // Claude Code name, which carries no role suffix at all. Requiring a positive
        // interactive-claim suffix would refuse an operator sitting right there.
        HumanAttendance attendance = RecordingProvenanceReader.Classify(
            Task(claimedByNodeId: Guid.Empty), Run("brians-afternoon"), detachedSessionName: null);

        attendance.Should().Be(HumanAttendance.Attended);
    }

    [Fact]
    public void A_dispatched_run_the_daemon_claimed_reads_unattended()
    {
        HumanAttendance attendance = RecordingProvenanceReader.Classify(
            Task(claimedByNodeId: NodeId),
            Run(SessionRoleName.For(DomainId.Short(TaskId), SessionRoleName.Build)),
            detachedSessionName: null);

        attendance.Should().Be(HumanAttendance.Unattended);
    }

    /// <summary>
    /// Both shapes <c>HeadlessLaunch.SpawnDetached</c> produces, from inside the spawned session
    /// itself: <c>h9k task start</c>'s own agent on a claim nobody is attached to, and
    /// <c>h9k task delegate</c>'s contractor on a claim an operator IS attached to. The run
    /// document reads identically for the contractor and for that operator, so the caller's own
    /// marker is the only thing that can separate them.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_spawned_detached_session_reads_unattended_whichever_claim_it_runs_on(
        bool isDeliberateHeadlessStart)
    {
        HumanAttendance attendance = RecordingProvenanceReader.Classify(
            Task(claimedByNodeId: Guid.Empty),
            Run(
                SessionRoleName.For(DomainId.Short(TaskId), SessionRoleName.Build),
                isDeliberateHeadlessStart: isDeliberateHeadlessStart),
            detachedSessionName: SessionRoleName.For(DomainId.Short(TaskId), SessionRoleName.Build));

        attendance.Should().Be(HumanAttendance.Unattended);
    }

    /// <summary>
    /// The marker is decisive only for the task it names. A contractor working task X can record
    /// a statement against task Z, and it is attending neither — but nothing about it makes task
    /// Z's own claim unattended, so Z's record decides that. Without this the variable would also
    /// have reached across into any suite running inside such a session.
    /// </summary>
    [Fact]
    public void A_detached_session_working_another_task_leaves_this_claims_own_record_to_decide()
    {
        HumanAttendance attendance = RecordingProvenanceReader.Classify(
            Task(claimedByNodeId: Guid.Empty),
            Run(SessionRoleName.For(DomainId.Short(TaskId), SessionRoleName.InteractiveClaim)),
            detachedSessionName: SessionRoleName.For(DomainId.Short(DomainId.New()), SessionRoleName.Build));

        attendance.Should().Be(HumanAttendance.Attended);
    }

    /// <summary>
    /// The operator the first draft of this refused (independent pre-PR review, cycle 1,
    /// conformance lens): the contractor left the build role on the run's recorded session name
    /// and nothing restores it when it exits, so the still-attached human kept reading unattended
    /// for the rest of their claim — both while the contractor ran and forever after.
    /// </summary>
    [Fact]
    public void An_operator_still_attached_after_delegating_reads_attended()
    {
        HumanAttendance attendance = RecordingProvenanceReader.Classify(
            Task(claimedByNodeId: Guid.Empty),
            Run(SessionRoleName.For(DomainId.Short(TaskId), SessionRoleName.Build)),
            detachedSessionName: null);

        attendance.Should().Be(HumanAttendance.Attended);
    }

    [Fact]
    public void A_deliberate_headless_start_named_from_outside_its_own_session_reads_unattended()
    {
        // A human at their own shell naming this run with --task is not attending it either, and
        // h9k task start's claim carries the same sentinel an operator's own claim does.
        HumanAttendance attendance = RecordingProvenanceReader.Classify(
            Task(claimedByNodeId: Guid.Empty),
            Run(
                SessionRoleName.For(DomainId.Short(TaskId), SessionRoleName.Build),
                isDeliberateHeadlessStart: true),
            detachedSessionName: null);

        attendance.Should().Be(HumanAttendance.Unattended, "h9k task start names its session build");
    }

    [Fact]
    public void A_deliberate_headless_start_an_operator_re_entered_reads_attended()
    {
        // h9k task work re-enters that same run, and register-session's own
        // InteractiveSessionStarted records the human's name — the one write h9k task start's own
        // build agent is deliberately skipped for.
        HumanAttendance attendance = RecordingProvenanceReader.Classify(
            Task(claimedByNodeId: Guid.Empty),
            Run(
                "brians-afternoon",
                isDeliberateHeadlessStart: true,
                registeredInteractiveSessionName: "brians-afternoon"),
            detachedSessionName: null);

        attendance.Should().Be(HumanAttendance.Attended);
    }

    [Fact]
    public void A_headless_pr_review_run_under_the_same_sentinel_reads_unattended()
    {
        HumanAttendance attendance = RecordingProvenanceReader.Classify(
            Task(claimedByNodeId: Guid.Empty, type: TaskType.PrReview),
            Run(SessionRoleName.ReviewAdversarial(1)),
            detachedSessionName: null);

        attendance.Should().Be(
            HumanAttendance.Unattended, "AutoPrReviewEngine's Now speed launches it with nobody on it");
    }

    /// <summary>
    /// The reviewer the first draft of this refused (independent pre-PR review, cycle 1,
    /// conformance lens): <c>h9k pr review</c> dispatches a human's own lap under the identical
    /// type and sentinel the headless shape above uses, so excluding by type alone caught them too.
    /// </summary>
    [Fact]
    public void A_reviewers_own_open_lap_on_a_pr_review_task_reads_attended()
    {
        RunDetails run = Run(SessionRoleName.For(DomainId.Short(TaskId), SessionRoleName.ReviewLap));
        TaskDetails task = Task(claimedByNodeId: Guid.Empty, type: TaskType.PrReview);
        task.ReviewLapOpen = true;
        task.ReviewLapRunId = run.Id;

        RecordingProvenanceReader.Classify(task, run, detachedSessionName: null)
            .Should().Be(HumanAttendance.Attended);
    }

    /// <summary>
    /// A lap that already delivered its verdict, or one open on some other run of the same task,
    /// is not somebody sitting here now — the flag and the run id are read together for that
    /// reason.
    /// </summary>
    [Fact]
    public void A_lap_recorded_against_another_run_of_the_same_task_reads_unattended()
    {
        TaskDetails task = Task(claimedByNodeId: Guid.Empty, type: TaskType.PrReview);
        task.ReviewLapOpen = true;
        task.ReviewLapRunId = DomainId.New();

        RecordingProvenanceReader.Classify(
                task,
                Run(SessionRoleName.For(DomainId.Short(TaskId), SessionRoleName.ReviewLap)),
                detachedSessionName: null)
            .Should().Be(HumanAttendance.Unattended);
    }

    [Fact]
    public void A_run_whose_session_was_never_named_reads_as_not_observed_rather_than_attended()
    {
        HumanAttendance attendance = RecordingProvenanceReader.Classify(
            Task(claimedByNodeId: Guid.Empty), Run(sessionName: string.Empty), detachedSessionName: null);

        attendance.Should().Be(
            HumanAttendance.Unobserved, "an unread fact is not evidence of a human, and this gates a refusal");
    }

    private static TaskDetails Task(Guid claimedByNodeId, TaskType? type = null) => new()
    {
        Id = TaskId,
        ProjectId = DomainId.New(),
        ClaimedByNodeId = claimedByNodeId,
        Type = type ?? TaskType.Feature,
    };

    private static RunDetails Run(
        string sessionName,
        bool isDeliberateHeadlessStart = false,
        string? registeredInteractiveSessionName = null) => new()
    {
        Id = DomainId.New(),
        TaskId = TaskId,
        SessionName = sessionName,
        IsDeliberateHeadlessStart = isDeliberateHeadlessStart,
        RegisteredInteractiveSessionName = registeredInteractiveSessionName,
    };
}

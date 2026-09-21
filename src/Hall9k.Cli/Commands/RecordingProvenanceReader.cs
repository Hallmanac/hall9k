using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// What one h9k decide or h9k learn call could observe about where it is being made from: the
/// provenance it records, and the project of the task it found, when it found one. The project
/// rides along because it is the default scope for a statement recorded inside a run, and
/// resolving it here saves the caller loading the same task document a second time.
/// </summary>
internal sealed record ObservedRecordingContext(RecordedProvenance Provenance, Guid? ProjectId);

/// <summary>
/// Where a <c>h9k decide</c> or <c>h9k learn</c> call is being made from, observed rather than
/// inferred (idea d805fd8b, piece 1). Two signals name a run, and neither of them is ever used to
/// decide attendance:
/// <list type="bullet">
/// <item><c>--task</c>, the explicit form a dispatched agent uses — the same way
/// <see cref="TaskLogInteractionCommand"/> names the run it logs against, through the task's own
/// <see cref="TaskDetails.CurrentRunId"/>.</item>
/// <item><see cref="InteractiveSessionLiveness.InteractiveRunEnvironmentVariable"/>, set on an
/// operator's own attached claim at launch and inherited by every descendant, so an attended
/// session gets its provenance without typing anything.</item>
/// </list>
/// <para>
/// Attendance never comes off that environment variable, and the distinction is load-bearing
/// rather than fastidious: <c>h9k task delegate</c> spawns its contractor from inside the
/// operator's own attended session (<see cref="HeadlessLaunch.SpawnDetached"/> hands the child
/// this process's environment), so the contractor inherits a variable naming a run it is not
/// attended on. It comes instead from the run's own record plus one fact about the calling process
/// itself — <see cref="HeadlessLaunch.DetachedSessionEnvironmentVariable"/>, which only a spawned
/// detached session and its descendants carry, and which is read as decisive only for the task it
/// actually names.
/// </para>
/// <para>
/// That second signal is what the run's own recorded session name cannot supply: the contractor
/// overwrites that name with the <c>-build</c> role for the rest of the claim, and nothing
/// restores it when the contractor exits, so reading it refused the still-attached operator's own
/// decisions from the first delegation onward (independent pre-PR review, cycle 1, conformance
/// lens). <see cref="HeadlessLaunch.DetachedSessionEnvironmentVariable"/>'s own doc has why the
/// two signals already on hand — a <c>CLAUDE_PID</c> match, or <c>RunDispatched</c>'s original
/// name — cannot stand in for it either.
/// </para>
/// </summary>
internal static class RecordingProvenanceReader
{
    /// <summary>
    /// The call's own provenance. <paramref name="taskReference"/> wins over the environment
    /// variable when both are present: one is what the caller typed about this call, the other is
    /// ambient.
    /// </summary>
    public static async Task<ObservedRecordingContext> ObserveAsync(
        IQuerySession session, string? taskReference, Guid ownerId, CancellationToken cancellationToken)
    {
        (TaskDetails Task, RunDetails Run)? observed = taskReference.IsNotBlank()
            ? await FromNamedTaskAsync(session, taskReference, cancellationToken)
            : await FromEnvironmentAsync(session, cancellationToken);

        string? detachedSessionName = Environment.GetEnvironmentVariable(
            HeadlessLaunch.DetachedSessionEnvironmentVariable);

        return observed is not { } pair
            ? new ObservedRecordingContext(RecordedProvenance.FromShell(ownerId), ProjectId: null)
            : new ObservedRecordingContext(
                new RecordedProvenance(
                    ownerId, pair.Run.Id, pair.Task.Id,
                    Classify(pair.Task, pair.Run, detachedSessionName)),
                pair.Task.ProjectId);
    }

    /// <summary>
    /// Whether a human is attached to this run, from the run's own record and one fact about the
    /// calling process. Three outcomes, because two would mean guessing: a run whose session was
    /// never named records nothing this can read, and <see cref="HumanAttendance.Unobserved"/>
    /// says so rather than defaulting to the answer that happens to be convenient. Pure, so the
    /// whole rule is provable without a store — <paramref name="detachedSessionName"/> is the
    /// environment read, made an argument rather than performed here.
    /// </summary>
    public static HumanAttendance Classify(TaskDetails task, RunDetails run, string? detachedSessionName)
    {
        if (detachedSessionName == SessionRoleName.For(DomainId.Short(task.Id), SessionRoleName.Build))
        {
            // This very process descends from the detached claude session spawned for THIS task
            // (HeadlessLaunch.SpawnDetached): h9k task start's own agent, or h9k task delegate's
            // contractor. Checked first and against the caller rather than the run, because a
            // contractor's run IS the operator's own still-attended claim — the two are
            // indistinguishable on the run document, and this is the only fact separating them.
            //
            // Matched against this task's own build name rather than merely "the variable is
            // set": a detached session working task X can perfectly well record a statement
            // against task Z (or against no run at all), and it is attending neither — but
            // nothing about it makes task Z's own claim unattended, so task Z's record decides
            // that, not this. Both SpawnDetached callers compose exactly this name.
            return HumanAttendance.Unattended;
        }

        if (run.SessionName.IsBlank())
        {
            // A run stream written before sessions were named. TaskPhaseComposer reads the same
            // blank as attended, because its job is to word a status line and the historical
            // wording was the attached one. This one gates a refusal, so it fails closed instead:
            // an unread fact is not evidence of a human.
            return HumanAttendance.Unobserved;
        }

        if (task.ClaimedByNodeId != Guid.Empty)
        {
            // A real node id is the dispatcher's own claim: nobody is attached to it by
            // construction. Guid.Empty is the interactive-claim sentinel every operator-held claim
            // carries (TaskAggregate.IsInteractiveClaim).
            return HumanAttendance.Unattended;
        }

        if (task.Type == TaskType.PrReview)
        {
            // Two shapes share the sentinel here and only one is a human's. AutoPrReviewEngine's
            // own Now speed launches a headless pr-review run under it, which TaskPhaseComposer
            // excludes wholesale by type — but h9k pr review dispatches a REVIEWER's own attended
            // lap under the identical type and sentinel (Decisions Log #149), and excluding by
            // type alone refused that reviewer their own decisions (independent pre-PR review,
            // cycle 1, conformance lens). The lap the task itself records is the tell, not the
            // run's session name: the name is a suffix a register-session call overwrites, while
            // ReviewLapOpen/ReviewLapRunId are written by PullRequestReviewLapOpened and cleared
            // by the reviewer's own verdict.
            return task.ReviewLapOpen && task.ReviewLapRunId == run.Id
                ? HumanAttendance.Attended
                : HumanAttendance.Unattended;
        }

        if (run.IsDeliberateHeadlessStart && run.RegisteredInteractiveSessionName is null)
        {
            // h9k task start's own claim carries the same sentinel an operator's h9k task work
            // claim does, and nobody is attached to it. Reached only from OUTSIDE the spawned
            // session (that case never gets past the first check) — a human at their own shell
            // naming this run with --task is not attending it either. The registration is what
            // ends it: h9k task work re-enters this same run, and register-session's own
            // InteractiveSessionStarted sets RegisteredInteractiveSessionName for every attended
            // registration while deliberately skipping the build agent's own (that field's doc).
            return HumanAttendance.Unattended;
        }

        // What is left is an operator's own interactive claim, whatever the run's most recently
        // recorded session name happens to say — a delegated contractor overwrote it and nothing
        // restores it, and h9k task register-session overwrites it again with a freely chosen
        // Claude Code name, so that field answers "which session spoke last", never "is a human
        // on this".
        return HumanAttendance.Attended;
    }

    private static async Task<(TaskDetails, RunDetails)?> FromNamedTaskAsync(
        IQuerySession session, string taskReference, CancellationToken cancellationToken)
    {
        Guid taskId = await TaskIdResolver.ResolveAsync(session, taskReference, cancellationToken);
        TaskDetails task = await session.LoadAsync<TaskDetails>(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        if (task.CurrentRunId is not { } runId)
        {
            throw new DomainConflictException(
                $"Task {task.Id} has no active run, so there is no run to record this against — it is "
                + $"{task.State.Value}. Drop --task to record it against nothing but yourself, which is "
                + "the honest provenance for a statement typed outside a run.");
        }

        RunDetails run = await session.LoadAsync<RunDetails>(runId, cancellationToken)
            ?? throw new DomainConflictException(
                $"Task {task.Id} names run {runId}, which has no record here to read provenance off.");

        return (task, run);
    }

    /// <summary>
    /// The ambient path, and deliberately forgiving: a stale or unreadable variable means this
    /// call simply records no run, never that it fails. Nothing the caller typed is being ignored
    /// — an explicit <c>--task</c> never reaches here — so refusing would turn a leftover
    /// environment variable in some terminal into an outage on a command whose whole point is to
    /// be cheap to reach for.
    /// </summary>
    private static async Task<(TaskDetails, RunDetails)?> FromEnvironmentAsync(
        IQuerySession session, CancellationToken cancellationToken)
    {
        string? value = Environment.GetEnvironmentVariable(
            InteractiveSessionLiveness.InteractiveRunEnvironmentVariable);
        if (!Guid.TryParse(value, out Guid runId))
        {
            return null;
        }

        if (await session.LoadAsync<RunDetails>(runId, cancellationToken) is not { } run)
        {
            return null;
        }

        // The same CurrentRunId check the explicit --task path makes, for the same reason: a
        // process that inherited this variable can outlive the run it names, and a release or a
        // requeue nulls both ClaimedByNodeId and CurrentRunId on the task. Without this, that
        // leftover variable recorded provenance against an ended run and then classified it off a
        // null claim id, refusing a human at a plain shell — the opposite of what this method's
        // own doc promises, which is that a stale variable simply records no run (independent
        // pre-PR review, cycle 1, conformance lens).
        return await session.LoadAsync<TaskDetails>(run.TaskId, cancellationToken) is { } task
            && task.CurrentRunId == run.Id
            ? (task, run)
            : null;
    }
}

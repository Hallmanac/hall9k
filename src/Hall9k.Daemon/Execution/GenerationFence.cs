using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Marten;
using Marten.Events;
using Microsoft.Extensions.Logging;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// The generation fence (PLAN.md decision #7, TASK-MODEL.md's fencing token): a run's
/// LeaseGeneration is stamped once at dispatch and never changes; a task's is bumped by
/// every claim and survives a requeue. A run whose generation no longer matches its task's
/// current one is a lane a requeue-and-reclaim has already superseded — the doctrine "any
/// state change from a stale-generation run is discarded" said in code. Every run-driven
/// task-level write checks this immediately before writing; a mismatch is rejected and
/// logged, never applied. Origin incident (2026-08-21 evening, twice observed): catch-up
/// adopted a detached run and requeued the same task's expired lease in the same startup,
/// and the stale generation's park/fail path wrote the task Failed while the live
/// generation's fix session was still running.
/// </summary>
internal static class GenerationFence
{
    /// <summary>
    /// True when the run is still its task's current lane and the caller may write. False
    /// means stale — the caller must skip the write it was about to make; this has already
    /// logged why, naming both the current run and generation so the operator can see the
    /// stale lane die.
    /// <para>
    /// Compares <see cref="TaskDetails.CurrentRunId"/> rather than
    /// <see cref="TaskDetails.LeaseGeneration"/>: the generation is bumped only by a claim
    /// and deliberately survives a requeue, retry, or reopen (the doc comment above), so
    /// between one of those and the next claim the task sits at its old generation with
    /// <c>CurrentRunId</c> null — a run whose generation happens to still match would pass
    /// a generation-only check in that gap and could fail a task that is Queued, waiting to
    /// be reclaimed, rather than Claimed by it. Identity has no such gap: only the run the
    /// task actually names as current is ever current.
    /// </para>
    /// <para>
    /// Reads through <paramref name="session"/> rather than opening a second connection, and a
    /// task the projection cannot find is not treated as stale — there is nothing observed to
    /// reject against, so the write proceeds and the caller's own guards decide.
    /// </para>
    /// </summary>
    public static async Task<bool> AllowsAsync(
        IQuerySession session,
        ILogger logger,
        Guid taskId,
        Guid runId,
        int runGeneration,
        string attemptedTransition,
        CancellationToken cancellationToken)
    {
        TaskDetails? task = await session.LoadAsync<TaskDetails>(taskId, cancellationToken);
        if (task is null || task.CurrentRunId == runId)
        {
            return true;
        }

        logger.LogWarning(
            "Task {TaskId}, run {RunId}: run at generation {RunGeneration} attempted {Transition}; "
            + "task's current run is {CurrentRunId} at generation {TaskGeneration} - rejected",
            taskId, runId, runGeneration, attemptedTransition, task.CurrentRunId, task.LeaseGeneration);
        return false;
    }

    /// <summary>
    /// The write-time half of the fence (Copilot review, PR #30): <see cref="AllowsAsync"/>
    /// alone is a read-only check with no reservation through the caller's subsequent write —
    /// a claim can commit between that read and the caller's <c>SaveChangesAsync</c>, and an
    /// unversioned append would land on top of it. This pins the task aggregate to the stream
    /// version observed right now; the caller appends its event with
    /// <c>expectedVersion: version + 1</c> and treats
    /// <see cref="JasperFx.Events.EventStreamUnexpectedMaxEventIdException"/> as the same
    /// "lost the generation race" outcome <see cref="AllowsAsync"/> already logs — a claim
    /// that commits in the gap loses the write instead of silently absorbing it.
    /// </summary>
    public static async Task<(TaskAggregate Task, long Version)?> LoadFencedAsync(
        IDocumentSession session, Guid taskId, CancellationToken cancellationToken)
    {
        StreamState? state = await session.Events.FetchStreamStateAsync(taskId, cancellationToken);
        if (state is null)
        {
            return null;
        }

        TaskAggregate? task = await session.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, version: state.Version, token: cancellationToken);
        return task is null ? null : (task, state.Version);
    }
}

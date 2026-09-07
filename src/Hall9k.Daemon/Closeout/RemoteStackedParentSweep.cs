using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Marten;
using Marten.Linq.MatchesSql;

namespace Hall9k.Daemon.Closeout;

/// <summary>What one sweep did, so the monitor can log it.</summary>
/// <param name="ChildrenLooked">Children whose parent pull request this sweep actually read.</param>
/// <param name="ObservationsRecorded">Of those, how many said something the task did not already record.</param>
/// <param name="Failures">
/// Children whose look produced no reading of the pull request at all — the call threw, or it
/// answered without saying anything about that number (a <c>gh</c> exit nobody can read a state
/// out of). Both are counted here rather than one of them riding in
/// <paramref name="ChildrenLooked"/>, because that number's own promise is that the parent was
/// actually read: a summary reporting a clean look for a child whose credential just expired tells
/// an operator the opposite of what happened (independent pre-PR review, cycle 1, conformance
/// lens). The next sweep retries either, and meanwhile the task keeps whatever it last actually
/// saw.
/// </param>
public sealed record RemoteParentSweepResult(int ChildrenLooked, int ObservationsRecorded, int Failures);

/// <summary>
/// The one reader of a stacked child's remote parent (task: a stacked child can stand on a pull
/// request another install owns). A reviewer's node never holds the parent's run, so the parent
/// never reaches Delivered in this install's store and nothing local can release the child; this
/// sweep closes that by reading the pull request itself, on the closeout watcher's own cadence,
/// which is the precedent one pull request over (Brian's ruling, 2026-09-07).
/// <para>
/// Deliberately the <em>single</em> reader. Everything downstream — the Blocked -> Queued release,
/// the base a fresh cut starts from (<c>StackedBaseResolver</c>), the retarget and replay
/// (<c>StackedParentWatch</c>), the in-run checkpoint rebases, and what <c>h9k task show</c> prints
/// — reads the observation this sweep recorded rather than calling the provider itself. Two readers
/// would spend two calls per cadence and could reach different answers inside one sweep, which for
/// a retarget means moving a pull request's base on a fact its own replay does not share.
/// </para>
/// <para>
/// Scoped to this node's owner's tasks, the same ownership the claim guard reads, and to the states
/// where an observation can still change something: waiting to dispatch, in flight, or delivered
/// with a pull request closeout is still watching. A child whose run has closed out is left alone —
/// nothing further arrives on it, so a look would spend a call to learn nothing.
/// </para>
/// </summary>
public sealed class RemoteStackedParentSweep(
    IDocumentStore store,
    NodeContext node,
    IRemoteParentReader remoteParents,
    ILogger<RemoteStackedParentSweep> logger)
{
    /// <summary>
    /// The run states a delivered child's own pull request is still being watched in — closeout's
    /// own watch set (<c>CloseoutEngine.PollOnceAsync</c>), quoted here rather than re-derived so a
    /// child stops being swept at exactly the moment closeout stops watching it.
    /// </summary>
    private static readonly string[] WatchedRunStates =
        [RunState.AwaitingReview.Value, RunState.ReviewPending.Value, RunState.CloseoutParked.Value];

    public async Task<RemoteParentSweepResult> SweepOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<TaskListItem> children;
        await using (IQuerySession query = store.QuerySession())
        {
            Guid ownerId = node.OwnerId;
            children = await query.Query<TaskListItem>()
                // A positive number, which is what "is stacked on a pull request" means everywhere
                // else (TaskAggregate.IsStackedOnRemotePullRequest, both projections' hold rules).
                // Asked the same way here so a row this sweep reads and a row those predicates call
                // stacked can never be different rows, and so nothing that is not a pull request
                // number can ever cost a provider call (Copilot review, pull request #277).
                .Where(task => task.StackedOnPullRequestNumber > 0)
                .Where(task => task.AssignedOwnerId == ownerId)
                .Where(task => task.MatchesSql(
                    "d.data ->> 'state' in (?, ?, ?, ?)",
                    TaskState.Blocked.Value, TaskState.Queued.Value, TaskState.Claimed.Value,
                    TaskState.Done.Value))
                .ToListAsync(cancellationToken);
        }

        int looked = 0;
        int recorded = 0;
        int failures = 0;
        foreach (TaskListItem child in children)
        {
            try
            {
                switch (await LookAtParentAsync(child, cancellationToken))
                {
                    case LookOutcome.Recorded:
                        looked++;
                        recorded++;
                        break;
                    case LookOutcome.Unchanged:
                        looked++;
                        break;
                    case LookOutcome.Unreadable:
                        failures++;
                        break;
                    case LookOutcome.Skipped:
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures++;
                logger.LogWarning(
                    exception,
                    "Remote stacked-parent sweep failed for task {TaskId} (pull request #{Number}); will retry "
                    + "next sweep",
                    child.Id, child.StackedOnPullRequestNumber);
            }
        }

        return new RemoteParentSweepResult(looked, recorded, failures);
    }

    /// <summary>How one child's look ended — an unpersisted in-process outcome, so an enum is right (TASK-MODEL.md §8).</summary>
    private enum LookOutcome
    {
        /// <summary>Nothing was read: this child is past the point where an observation changes anything.</summary>
        Skipped,

        /// <summary>The parent was read and said what the task already records, so nothing was appended.</summary>
        Unchanged,

        /// <summary>
        /// The provider was asked and answered nothing about that pull request — a <c>gh</c> call
        /// that failed without throwing. Deliberately not <see cref="Unchanged"/>: nothing was
        /// read, so the sweep counts it with the failures it retries rather than reporting a look
        /// it never got.
        /// </summary>
        Unreadable,

        /// <summary>The parent was read and said something new, which is now on the task's stream.</summary>
        Recorded,
    }

    private async Task<LookOutcome> LookAtParentAsync(TaskListItem child, CancellationToken cancellationToken)
    {
        if (child.StackedOnPullRequestNumber is not { } parentNumber)
        {
            return LookOutcome.Skipped;
        }

        await using IDocumentSession session = store.LightweightSession();
        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(child.ProjectId, cancellationToken);
        if (project is null)
        {
            logger.LogWarning(
                "Task {TaskId} is stacked on pull request #{Number} but its project {ProjectId} is not on record, "
                + "so there is no repository to read it from",
                child.Id, parentNumber, child.ProjectId);
            return LookOutcome.Skipped;
        }

        // A Done child is only worth a look while closeout is still watching its pull request. Past
        // that its own run has completed — the stack is finished — and a look would spend a
        // provider call to learn something nothing left in the pipeline reads.
        if (child.State == TaskState.Done && !await StillWatchedAsync(session, child, cancellationToken))
        {
            return LookOutcome.Skipped;
        }

        RemoteParentRead read = await remoteParents.ReadAsync(
            project.RepositoryPath, parentNumber, cancellationToken);
        if (!read.State.WasObserved)
        {
            // A failed look is not an observation, and recording one would put a guess in an audit
            // field (AGENTS.md's never-guess rule). The next sweep asks again; meanwhile the task
            // keeps whatever it last actually saw.
            logger.LogInformation(
                "Task {TaskId}: nothing observed about stacked parent pull request #{Number} this sweep — "
                + "{Detail}",
                child.Id, parentNumber, read.Detail);
            return LookOutcome.Unreadable;
        }

        // Loaded fresh rather than trusted from the row: the row is a projection that may lag, and
        // what this decides is whether to append to a stream.
        TaskAggregate? task = await session.Events.AggregateStreamAsync<TaskAggregate>(
            child.Id, token: cancellationToken);
        if (task?.StackedOnPullRequestNumber != parentNumber)
        {
            // The edge was repointed (or the task dropped) between the query and here. Appending
            // would attribute one pull request's state to another.
            return LookOutcome.Skipped;
        }

        if (!SaysSomethingNew(task, read))
        {
            return LookOutcome.Unchanged;
        }

        // Appended without an expected version, the same call TaskDependencyResolver makes and for
        // the same reason: both races this can lose replay to the right answer. Two of an owner's
        // nodes observing the same pull request at once write the same observation twice, and a
        // duplicate replays to the same state. A human command landing between the read and the
        // save (unassign, abandon, a revision repointing the edge) moves the task somewhere this
        // event is a no-op on — Apply guards on the declared number and on Blocked — so the late
        // append replays as nothing rather than smearing a parent's state onto a lifecycle that has
        // moved on. What it CAN lose is a closeout fence race on the same task, and that is by
        // design too: the reopen fails its expected-version check, the whole inspection rolls back,
        // and the next sweep tries again (CloseoutEngine.TryReplayStackedChildAsync's own doc).
        session.Events.Append(child.Id, new RemoteStackedParentObserved(
            child.Id, parentNumber, read.State, read.HeadBranch, read.HeadCommit, read.BaseBranch,
            read.Url, read.LinkedWorkItem, read.Detail, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Task {TaskId}: stacked parent pull request #{Number} — {Detail}",
            child.Id, parentNumber, read.Detail);
        return LookOutcome.Recorded;
    }

    /// <summary>
    /// Whether a delivered child's own run is still one closeout watches. Read off the run rather
    /// than off the task's Done, because Done is where a child sits for its whole review-and-merge
    /// life and says nothing about whether the pull request has landed.
    /// </summary>
    private static async Task<bool> StillWatchedAsync(
        IQuerySession query, TaskListItem child, CancellationToken cancellationToken)
    {
        if (child.CurrentRunId is not { } runId)
        {
            return false;
        }

        RunDetails? run = await query.LoadAsync<RunDetails>(runId, cancellationToken);
        return run is not null && WatchedRunStates.Contains(run.State.Value);
    }

    /// <summary>
    /// Whether this look says anything the task does not already record. Every field that any
    /// consumer downstream reads is compared, not the state alone: a parent that force-pushed
    /// without changing state moves only the head commit, and that move is the entire signal the
    /// replay path keys on (task: a stacked child absorbs its parent's post-delivery churn safely).
    /// </summary>
    private static bool SaysSomethingNew(TaskAggregate task, RemoteParentRead read) =>
        task.RemoteStackedParentState != read.State
        || task.RemoteStackedParentHeadBranch != read.HeadBranch
        || task.RemoteStackedParentHeadCommit != read.HeadCommit
        || task.RemoteStackedParentBaseBranch != read.BaseBranch
        || task.RemoteStackedParentUrl != read.Url
        || task.RemoteStackedParentWorkItem?.ToString() != read.LinkedWorkItem?.ToString();
}

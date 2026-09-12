using Hall9k.Connectors.Processes;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Marten.Linq.MatchesSql;

namespace Hall9k.Daemon.Closeout;

/// <summary>
/// One follow-through sweep's tally. <see cref="Inspected"/> and <see cref="Failures"/> are keyed
/// the same way <see cref="CloseoutSweepResult"/>'s own are, and for the same reason: they feed
/// <see cref="PullRequestMonitor"/>'s backoff verdict, which asks "is <c>gh</c> in trouble for the
/// pull requests this node watches" — and a waiting review's pull request is one of those.
/// </summary>
/// <param name="Inspected">Waiting reviews whose pull request was actually read this sweep.</param>
/// <param name="Concluded">Waiting reviews that reached Done this sweep.</param>
/// <param name="Surfaced">Waiting reviews that surfaced as needs-you this sweep.</param>
/// <param name="Failures">Waiting reviews whose read threw. Retried next tick; never fatal to the sweep.</param>
/// <param name="Skipped">
/// Waiting reviews the sweep passed over without ever calling <c>gh</c> — another node's watch, a
/// project or reference it could not resolve. Counted separately for the reason
/// <see cref="PullRequestMonitor.IsSweepFailure"/> gives at length: a skip says nothing about
/// whether <c>gh</c> is in trouble, so it must neither corroborate nor veto a failure verdict.
/// </param>
public sealed record PrReviewFollowThroughResult(
    int Inspected, int Concluded, int Surfaced, int Failures = 0, int Skipped = 0);

/// <summary>
/// The watch a posted pull-request review gets instead of an immediate Done (task: a pr-review
/// task stays open while the pull request's review threads are unresolved). Swept by
/// <see cref="PullRequestMonitor"/> on the closeout watcher's own cadence, because that is the
/// cadence a pull request actually moves on and there is no doorbell from GitHub in a local-first
/// design.
/// <para>
/// Every waiting review gets one <c>gh</c> read per tick, and that read answers two questions in
/// order:
/// </para>
/// <list type="number">
/// <item>
/// Has the pull request ended? A merge or a close ends the follow-through outright: whatever the
/// author did or did not answer, there is nothing left to wait for. This, and a human's own
/// <c>h9k task abandon</c>, are the only two ways this watch ever ends (Decisions Log
/// #178: one pr-review task per pull request per install, so the wait stays open
/// even when nothing is outstanding — a task that reached Done because it had nothing left to
/// watch would leave a later GitHub mention of the install's own login with no live task to attach
/// to, and would mint a redundant second one instead).
/// </item>
/// <item>
/// Has the pull request moved? Replies in the reviewer's own threads — comments they did not write
/// themselves — commits pushed since the last look, or a re-review newly requested of them,
/// surface the task as needs-you with a line naming what changed. The third is there because it is
/// the one explicit ask of the reviewer in the set: an author who resolves the threads themselves
/// and re-requests review without a word or a push is asking them back, and holding that as a
/// quiet wait left the author waiting on the reviewer while the reviewer's board said the reverse
/// (independent pre-PR review, cycle 1, adversarial lens). Every review thread of the reviewer's
/// own being resolved, with nothing else outstanding, no longer ends the wait by itself — it is
/// recorded as a quiet observation exactly like an unremarkable tick, and the task stays waiting
/// until the pull request itself ends.
/// </item>
/// </list>
/// <para>
/// The order matters and is deliberate: a pull request that merged while carrying unanswered
/// replies is over, and surfacing needs-you on it would ask the reviewer to go and read a
/// conversation nobody can act on. Replies are counted from a thread's own comments rather than
/// gated on a previous watermark, which is what lets the FIRST look count them too: an author who
/// answers inside the first poll interval still wakes the reviewer once, rather than the reply
/// going unremarked because nothing had been observed yet to compare it against (independent
/// pre-PR review, cycle 1, adversarial lens).
/// </para>
/// <para>
/// Deliberately a sweep of its own rather than a fourth arm inside
/// <see cref="CloseoutEngine.PollOnceAsync"/>. That engine's arms all watch runs in a transient
/// state for a MERGE of this platform's own work, dispatching follow-ups and re-requesting
/// reviews; this one watches somebody else's pull request for a conversation, dispatches nothing,
/// and writes only to the task's own stream. Sharing the loop would have meant widening the
/// watched-run query with a state no run of this feature is ever in — a waiting review's run is
/// Completed — and then guarding every one of that engine's merge, check, and follow-up arms
/// against a task type none of them apply to.
/// </para>
/// </summary>
public sealed class PrReviewFollowThroughEngine(
    IDocumentStore store,
    NodeContext node,
    IReviewConversationReader conversations,
    ProcessRunner processRunner,
    ILogger<PrReviewFollowThroughEngine> logger)
{
    private readonly GitHubReviewAssignments reviewAssignments = new(processRunner);

    /// <summary>One look at every pr-review task on this node whose posted review is still being followed through.</summary>
    public async Task<PrReviewFollowThroughResult> SweepOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<TaskListItem> waiting;
        await using (IQuerySession query = store.QuerySession())
        {
            // The flag and the state together, which is the rule TaskDecider.AwaitsPrReviewFollowThrough
            // states once for every reader: NeedsHuman alone would also select an ordinary findings
            // park nobody has walked yet, and polling GitHub for one of those is a read nobody asked
            // for. The state filter is SQL against the stored string because TaskState is a value
            // object and Marten refuses to translate a comparison against one — the way every state
            // filter in this repo is written.
            waiting = await query.Query<TaskListItem>()
                .Where(task => task.PrReviewFollowThroughOpen)
                .Where(task => task.MatchesSql("d.data ->> 'type' = ?", TaskType.PrReview.Value))
                .Where(task => task.MatchesSql(
                    "d.data ->> 'state' in (?, ?)", TaskState.AwaitingAuthor.Value, TaskState.NeedsHuman.Value))
                .ToListAsync(cancellationToken);
        }

        PrReviewFollowThroughResult sweep = new(0, 0, 0);
        foreach (TaskListItem task in waiting)
        {
            sweep = Add(sweep, await LookOnceAsync(task, cancellationToken));
        }

        return sweep;
    }

    /// <summary>
    /// One look at ONE waiting review, by task id. The sweep's own per-task step, exposed so the
    /// whole decision table this engine owns can be driven against a single seeded pull request
    /// rather than against every waiting review the store happens to hold — which, on a shared
    /// test database, is every other test's too.
    /// <para>
    /// A task that is not (or is no longer) following a posted review through answers as a skip
    /// rather than throwing: that is the same answer the sweep gives for it, and it is what a
    /// caller racing a verdict or an abandon should see.
    /// </para>
    /// </summary>
    public async Task<PrReviewFollowThroughResult> FollowThroughOnceAsync(
        Guid taskId, CancellationToken cancellationToken)
    {
        TaskListItem? row;
        await using (IQuerySession query = store.QuerySession())
        {
            row = await query.LoadAsync<TaskListItem>(taskId, cancellationToken);
        }

        return row is { PrReviewFollowThroughOpen: true }
            ? await LookOnceAsync(row, cancellationToken)
            : new PrReviewFollowThroughResult(0, 0, 0, Skipped: 1);
    }

    /// <summary>
    /// One task's look, tallied — and its failures contained, so one unreadable pull request never
    /// costs the sweep the rest of them.
    /// </summary>
    private async Task<PrReviewFollowThroughResult> LookOnceAsync(
        TaskListItem task, CancellationToken cancellationToken)
    {
        try
        {
            return await DecideAsync(task, cancellationToken) switch
            {
                FollowThroughOutcome.Concluded => new PrReviewFollowThroughResult(1, 1, 0),
                FollowThroughOutcome.Surfaced => new PrReviewFollowThroughResult(1, 0, 1),
                FollowThroughOutcome.StillWaiting => new PrReviewFollowThroughResult(1, 0, 0),
                _ => new PrReviewFollowThroughResult(0, 0, 0, Skipped: 1),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Pr-review follow-through failed for task {TaskId} ({Url}); will retry next sweep",
                task.Id, task.PrReviewFollowThroughPullRequestUrl);
            return new PrReviewFollowThroughResult(0, 0, 0, Failures: 1);
        }
    }

    private static PrReviewFollowThroughResult Add(
        PrReviewFollowThroughResult running, PrReviewFollowThroughResult one) => new(
            running.Inspected + one.Inspected,
            running.Concluded + one.Concluded,
            running.Surfaced + one.Surfaced,
            running.Failures + one.Failures,
            running.Skipped + one.Skipped);

    /// <summary>How one waiting review's look ended — an unpersisted in-process outcome, so an enum is right (TASK-MODEL.md §8).</summary>
    private enum FollowThroughOutcome
    {
        Skipped,
        StillWaiting,
        Surfaced,
        Concluded,
    }

    /// <summary>
    /// The decision itself for one waiting review: read the pull request, and either conclude the
    /// follow-through, wake the reviewer, or leave it waiting. Its own name rather than a second
    /// FollowThroughOnceAsync overload (self-review, round one): the two would have differed only
    /// in parameter type while meaning different things and returning different types, which is
    /// exactly the pair a reader resolves wrong.
    /// </summary>
    private async Task<FollowThroughOutcome> DecideAsync(
        TaskListItem row, CancellationToken cancellationToken)
    {
        await using IQuerySession query = store.QuerySession();
        if (row.ExternalReference is not { } externalReference
            || !GitHubPullRequestReference.TryParseCanonical(
                ExternalReference.Parse(externalReference).Reference, out string repository, out int number))
        {
            // A pr-review task always adopts a readable reference, so this is a stream a human has
            // to look at rather than one to guess about. Skipped rather than concluded: closing the
            // task out here would claim a follow-through that never happened.
            logger.LogWarning(
                "Task {TaskId} is waiting on a posted review but carries no readable pull-request reference "
                + "('{Reference}'), so nothing can be polled for it",
                row.Id, row.ExternalReference);
            return FollowThroughOutcome.Skipped;
        }

        ProjectDetails? project = await query.LoadAsync<ProjectDetails>(row.ProjectId, cancellationToken);
        if (project is null || project.RepositoryPath.IsBlank())
        {
            logger.LogWarning(
                "Task {TaskId} is waiting on {Repository}#{Number} but its project has no repository path to "
                + "run gh from", row.Id, repository, number);
            return FollowThroughOutcome.Skipped;
        }

        RunDetails? run = row.CurrentRunId is { } runId
            ? await query.LoadAsync<RunDetails>(runId, cancellationToken)
            : null;
        if (!IsThisNodesWatch(run))
        {
            // The same rule CloseoutEngine's own watched query keeps (RunDetails.NodeId): the task
            // holds no lease here and nothing else records an owner, so run provenance is the only
            // honest answer to "whose watch is this". Without it, every node on a shared store would
            // poll the same pull request and race to notify about the same replies.
            return FollowThroughOutcome.Skipped;
        }

        GitHubLoginRead login = await reviewAssignments.ReadCurrentLoginAsync(
            project.RepositoryPath, cancellationToken);
        if (login.Login is not { } reviewerLogin || reviewerLogin.IsBlank())
        {
            // Read back from gh every sweep, never remembered — the same discipline
            // AutoPrReviewEngine keeps, and for the same reason: it is the account whose threads
            // this watch is about. A read that failed is a failed poll to retry, so it throws
            // rather than skipping: an unreadable login must count toward the monitor's backoff
            // verdict exactly as an unreadable pull request does, and must never be quietly
            // substituted with the login a previous sweep happened to see.
            throw new InvalidOperationException(
                $"gh could not say which login is signed in from {project.RepositoryPath}, so task {row.Id}'s "
                + $"follow-through on {repository}#{number} cannot tell whose review threads to count. "
                + $"gh reported: {login.Error ?? "nothing"}");
        }

        ReviewConversation conversation = await conversations.ReadAsync(
            repository, number, project.RepositoryPath, cancellationToken);

        await using IDocumentSession session = store.LightweightSession();
        StreamState? fence = await session.Events.FetchStreamStateAsync(row.Id, cancellationToken);
        if (fence is null)
        {
            return FollowThroughOutcome.Skipped;
        }

        TaskAggregate? task = await session.Events.AggregateStreamAsync<TaskAggregate>(
            row.Id, version: fence.Version, token: cancellationToken);
        if (task is null || !TaskDecider.AwaitsPrReviewFollowThrough(task))
        {
            // The stream moved since this sweep's own listing read — a verdict, an abandon, another
            // node's tick. A lost race, not a defect: whatever landed is authoritative, and the
            // next sweep reads it fresh.
            return FollowThroughOutcome.Skipped;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string? pullRequestUrl = task.PrReviewFollowThroughPullRequestUrl;

        if (conversation.IsMerged || conversation.IsClosed || !conversation.IsOpen)
        {
            // expectedVersion is the version the stream will be AT once this append commits, so it
            // counts the events being appended — not a fixed +1 wherever the append carries two.
            // Getting that wrong does not throw: it loses the optimistic-concurrency race on every
            // single call, so the write never lands while the outcome still reads as though it had
            // (found by this feature's own integration tests, which is what they are for).
            session.Events.Append(
                row.Id,
                expectedVersion: fence.Version + 1,
                TaskDecider.Complete(task, RunIdFor(task), pullRequestUrl, now));
            if (!await SaveOrLoseTheRaceAsync(session, row.Id, cancellationToken))
            {
                return FollowThroughOutcome.Skipped;
            }

            logger.LogInformation(
                "Task {TaskId}: {Repository}#{Number} is {Ending} — the posted review's follow-through is over",
                row.Id, repository, number,
                conversation.IsMerged ? "merged" : "closed");
            return FollowThroughOutcome.Concluded;
        }

        IReadOnlyList<ReviewThread> ownThreads = conversation.ThreadsStartedBy(reviewerLogin);
        bool reReviewRequested = conversation.ReReviewRequestedOf(reviewerLogin);
        // Replies rather than comments (ReplyCountFor): the reviewer's own comments in their own
        // threads are not answers to them, so counting them would report a posted review back to
        // its author as news and turn a follow-up comment of the reviewer's own into a needs-you
        // asserting somebody had replied (independent pre-PR review, cycle 1, both lenses). It is
        // also what lets the FIRST look count replies at all — see PrReviewAuthorActivity.Between.
        List<PrReviewThreadWatermark> watermark =
        [
            .. ownThreads.Select(thread =>
                new PrReviewThreadWatermark(
                    thread.Id, thread.ReplyCountFor(reviewerLogin), thread.IsResolved)),
        ];

        PrReviewAuthorActivity activity = PrReviewAuthorActivity.Between(
            task.PrReviewThreads, watermark, task.PrReviewObservedHeadSha, conversation.HeadSha,
            task.PrReviewObservedCommitCount, conversation.CommitCount,
            task.PrReviewReReviewRequested, reReviewRequested);

        bool observationIsNews = activity.Any
            || WatermarkChanged(task.PrReviewThreads, watermark)
            || task.PrReviewReReviewRequested != reReviewRequested
            || task.PrReviewObservedHeadSha != conversation.HeadSha
            || task.PrReviewObservedCommitCount != conversation.CommitCount
            || task.PrReviewReviewerLogin != reviewerLogin;

        // Thread resolution alone no longer ends the follow-through (Decisions Log
        // #178: one pr-review task per pull request per install, and later mentions
        // attach to it rather than mint a second one — a task that reached Done the moment its
        // threads happened to be quiet would let a mention arriving after that leave nothing live
        // to attach to). Every review thread resolved, with nothing outstanding, used to reach Done
        // on this very look; now it is recorded exactly like any other quiet observation below and
        // the wait stays open. Only the merge/close branch above, or a human's own
        // <c>h9k task abandon</c>, ever ends it from here on.
        if (!activity.Any)
        {
            if (!observationIsNews)
            {
                // A quiet pull request polled every few minutes for a week must not write a
                // thousand identical observations onto the task's own stream.
                return FollowThroughOutcome.StillWaiting;
            }

            session.Events.Append(
                row.Id,
                expectedVersion: fence.Version + 1,
                TaskDecider.ObservePrReviewFollowThrough(
                    task, reviewerLogin, watermark, reReviewRequested, conversation.HeadSha,
                    conversation.CommitCount, now));
            return await SaveOrLoseTheRaceAsync(session, row.Id, cancellationToken)
                ? FollowThroughOutcome.StillWaiting
                : FollowThroughOutcome.Skipped;
        }

        // The observation FIRST, then the response — the ordering the response event's own doc
        // states as load-bearing: the observation re-baselines the watermark, so the same replies
        // can never fire a second notification on the next tick.
        string summary = activity.Describe(repository, number, watermark.Count(thread => !thread.IsResolved));
        session.Events.Append(
            row.Id,
            expectedVersion: fence.Version + 2,
            TaskDecider.ObservePrReviewFollowThrough(
                task, reviewerLogin, watermark, reReviewRequested, conversation.HeadSha,
                conversation.CommitCount, now),
            TaskDecider.RecordPrReviewAuthorResponse(
                task, summary, activity.ReplyCount, activity.ThreadsWithReplies, activity.NewCommitCount,
                activity.HeadMoved, activity.ReReviewNewlyRequested,
                run?.RegisteredInteractiveSessionName, now));
        if (!await SaveOrLoseTheRaceAsync(session, row.Id, cancellationToken))
        {
            return FollowThroughOutcome.Skipped;
        }

        // The line goes to the log with the address it was for, which is as far as the daemon can
        // carry it: nothing here can reach a live Claude Code session — in this platform an agent
        // sends and the daemon does not (ORCHESTRATOR-WINDOW.md's R5) — so what a registered
        // session receives is this same line off the board it already reads, under the task's own
        // needs-you row. Logged with the address rather than without it so an operator can tell
        // "nobody was registered" from "somebody was, and the board is where they read it".
        logger.LogInformation(
            "Task {TaskId} needs you: {Summary} (addressed to {Session})",
            row.Id, summary, run?.RegisteredInteractiveSessionName ?? "no registered session");
        return FollowThroughOutcome.Surfaced;
    }

    /// <summary>
    /// Whether this node is the one watching. Both node fields are checked because a pr-review run
    /// can carry either: an ordinary dispatch records the executing node on
    /// <see cref="RunDetails.NodeId"/>, while a reviewer's own lap and a deliberate headless start
    /// carry the ceiling-exempt <see cref="Guid.Empty"/> sentinel there and the physical node on
    /// <see cref="RunDetails.DispatchingNodeId"/> (Decisions Log #156's own widening, for the same
    /// reason). A task with no run record at all is nobody's watch by provenance, and is left
    /// alone rather than claimed by whichever daemon happened to sweep first.
    /// </summary>
    private bool IsThisNodesWatch(RunDetails? run) =>
        run is not null && (run.NodeId == node.NodeId || run.DispatchingNodeId == node.NodeId);

    /// <summary>
    /// The run a follow-through's own completion is attributed to: the run whose review this is
    /// following through on, recorded on the opening event, rather than whatever
    /// <see cref="TaskAggregate.CurrentRunId"/> happens to hold by now.
    /// <see cref="Guid.Empty"/> is unreachable — the opening event always carries a run id — and is
    /// here so a stream written before that field existed degrades to the sentinel this codebase
    /// already uses for "no node/run of its own" rather than failing the completion outright.
    /// </summary>
    private static Guid RunIdFor(TaskAggregate task) =>
        task.PrReviewFollowThroughRunId ?? task.CurrentRunId ?? Guid.Empty;

    /// <summary>
    /// Commits, and answers whether it actually landed. The answer is load-bearing: every caller
    /// reports an outcome the monitor counts and the log states, so a lost race that returned as
    /// though it had written would report a task concluded, or a reviewer notified, on the
    /// strength of a write that never happened.
    /// </summary>
    private async Task<bool> SaveOrLoseTheRaceAsync(
        IDocumentSession session, Guid taskId, CancellationToken cancellationToken)
    {
        try
        {
            await session.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            // Another writer committed to this task between the aggregate read and the append —
            // a verdict, an abandon, another node's sweep. The next tick reads it fresh; nothing
            // here is worth retrying inside one sweep.
            logger.LogInformation(
                "Task {TaskId}: lost the race recording a follow-through observation — a newer write "
                + "committed first, and the next sweep reads it fresh", taskId);
            return false;
        }
    }

    private static bool WatermarkChanged(
        IReadOnlyList<PrReviewThreadWatermark> before, IReadOnlyList<PrReviewThreadWatermark> after) =>
        before.Count != after.Count
        || after.Any(thread =>
            before.FirstOrDefault(previous => previous.ThreadId == thread.ThreadId) is not { } match
            || match.ReplyCount != thread.ReplyCount
            || match.IsResolved != thread.IsResolved);
}

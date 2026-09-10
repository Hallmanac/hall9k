using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Project.Projections;
using Marten;
using Marten.Linq.MatchesSql;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// The other half of budget-exhaustion recovery (backlog 40): <c>RunSupervisor</c> and
/// <c>ReviewEngine</c> park a run whose result carried the usage-limit shape, and this is what
/// un-parks it. The window resets on the clock, not on an event the platform can watch for, so
/// a patient poll — driven by <c>TokenBudgetRetryMonitor</c> — is the whole mechanism.
/// <para>
/// A run parked mid-primary-session resumes the same Claude session in the same worktree
/// (<c>--resume</c>, the log #5 pattern) rather than starting a fresh one: the point of parking
/// instead of failing was that the work — including whatever the agent was mid-thought on — is
/// intact, and a fresh session would throw that away for no reason the exhaustion itself gives.
/// </para>
/// <para>
/// A run parked mid-review-loop (a review pass or the fix session hit the limit instead) is
/// different: <c>RunAggregate.Apply(RunBudgetExhausted)</c> already cleared the exhausted leg
/// when the park landed, so there is no session left to resume — the retry is
/// <see cref="RunSupervisor.ResumeReviewLoop"/> re-entering the loop, which redispatches it
/// fresh over the same cycle. <see cref="ReviewPhase"/> on the reloaded aggregate is what tells
/// the two cases apart, since it is <see cref="ReviewPhase.None"/> for every run that never
/// entered the loop.
/// </para>
/// </summary>
public sealed class TokenBudgetRetryEngine(
    IDocumentStore store,
    NodeContext node,
    PrimarySessionResumer primarySessionResumer,
    RunSupervisor supervisor,
    ILogger<TokenBudgetRetryEngine> logger)
{
    /// <summary>
    /// Every budget-parked run this node owns, retried once — a plain node-only filter, unlike
    /// RunSupervisor.ResumeStrandedPipelinesAsync's provenance comment once claimed: h9k task
    /// deliver records the delivering node's own id on AgentSessionCompleted (Decisions Log
    /// #103), so an interactively delivered run parked mid review loop already carries a real
    /// node id by the time it can reach BudgetParked, and DeliveredByNodeId is new alongside
    /// this feature, so no pre-fix stream with an empty NodeId here can exist to widen for
    /// (conformance review, cycle 4). Widened with <see cref="SentinelPrReviewCandidatesAsync"/>
    /// for the one class this plain filter still misses (independent pre-PR review, cycle 10,
    /// conformance lens): a Now-speed auto-pr-review sentinel run carries the ceiling-exempt
    /// <see cref="Guid.Empty"/> on <c>NodeId</c>, so without the widening it never matches
    /// <c>nodeId</c> here and a run this park caught sits <c>BudgetParked</c> forever — adoption
    /// deliberately defers to this sweep for that state (<c>RunSupervisor.AdoptOrphansAsync</c>'s
    /// own comment), so nothing else on the node ever clears it either. Returns how many
    /// actually resumed.
    /// </summary>
    public async Task<int> RetryParkedRunsAsync(CancellationToken cancellationToken)
    {
        await using IQuerySession query = store.QuerySession();
        Guid nodeId = node.NodeId;
        IReadOnlyList<RunDetails> ownNode = await query.Query<RunDetails>()
            .Where(r => r.NodeId == nodeId)
            .Where(r => r.MatchesSql("d.data ->> 'state' = ?", RunState.BudgetParked.Value))
            .ToListAsync(cancellationToken);
        IReadOnlyList<RunDetails> sentinelPrReview = await SentinelPrReviewCandidatesAsync(
            query, nodeId, cancellationToken);
        List<RunDetails> parked = [.. ownNode, .. sentinelPrReview];

        int retried = 0;
        foreach (RunDetails run in parked)
        {
            if (await RetryOneAsync(run, cancellationToken))
            {
                retried++;
            }
        }

        return retried;
    }

    /// <summary>
    /// The same sentinel-run widening <c>RunSupervisor.SentinelPrReviewCandidatesAsync</c>
    /// applies for adoption and stranded-pipeline resumption, applied here for the retry sweep:
    /// every budget-parked run carrying the ceiling-exempt <see cref="Guid.Empty"/> whose own
    /// <see cref="RunDetails.DispatchingNodeId"/> names this node and whose owning task is a
    /// pr-review task — dispatched under that sentinel either by
    /// <c>AutoPrReviewEngine.CreateOneAsync</c>'s "now" speed (via <c>RunLauncher.LaunchAsync</c>)
    /// or by a reviewer's own review lap (<c>h9k pr review</c>, Decisions Log #149). Only the
    /// first is ever retried: <see cref="RetryOneAsync"/> refuses a run an open lap is attached
    /// to, for the collision its own comment describes.
    /// </summary>
    private static async Task<IReadOnlyList<RunDetails>> SentinelPrReviewCandidatesAsync(
        IQuerySession query, Guid nodeId, CancellationToken cancellationToken)
    {
        IReadOnlyList<RunDetails> sentinel = await query.Query<RunDetails>()
            .Where(r => r.NodeId == Guid.Empty)
            .Where(r => r.DispatchingNodeId == nodeId)
            .Where(r => r.MatchesSql("d.data ->> 'state' = ?", RunState.BudgetParked.Value))
            .ToListAsync(cancellationToken);
        if (sentinel.Count == 0)
        {
            return [];
        }

        List<RunDetails> prReview = [];
        foreach (RunDetails run in sentinel)
        {
            TaskDetails? owner = await query.LoadAsync<TaskDetails>(run.TaskId, cancellationToken);
            if (owner?.Type == TaskType.PrReview)
            {
                prReview.Add(run);
            }
        }

        return prReview;
    }

    private async Task<bool> RetryOneAsync(RunDetails run, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        TaskDetails? task = await session.LoadAsync<TaskDetails>(run.TaskId, cancellationToken);
        ProjectDetails? project = task is null
            ? null
            : await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
        if (task is null || project is null || task.State != TaskState.Claimed || task.CurrentRunId != run.Id)
        {
            // The claim moved on — a human intervened, or a later generation took the task
            // — so there is nothing here left to resume.
            return false;
        }

        if (IsUnderOpenReviewLap(task, run))
        {
            // A human reviewer's own review lap is attached to this budget-parked run
            // (h9k pr review, Decisions Log #149): the lap admits a BudgetParked run deliberately,
            // because an exhausted token budget is the platform's problem and not a reason to
            // refuse the human who wants to review by hand — and the lap reuses that run's
            // worktree. Resuming the automated review here would put a second session in the
            // checkout the reviewer is reading, and the reviewer's later verdict would then have
            // PrReviewEngine.FinalizeAsync delete that worktree out from under it. The claim's
            // own state cannot say this: the task is still Claimed and still names this run for
            // the whole lap. RunSupervisor.AdoptOrphansAsync was taught the same exclusion for
            // the same collision; this sweep was not (independent pre-PR review, cycle 1,
            // conformance lens). Nothing is cleared here, and nothing needs to be: the reviewer's
            // verdict moves the run to UnderReview for PrReviewEngine to finalize, and a lap
            // abandoned instead (h9k task release) requeues the task, which the claim check above
            // catches on the next sweep.
            logger.LogInformation(
                "Run {RunId} is budget-parked under task {TaskId}'s open review lap — not retried; a reviewer is reading in that checkout",
                run.Id, run.TaskId);
            return false;
        }

        RunAggregate aggregate = await session.Events.AggregateStreamAsync<RunAggregate>(run.Id, token: cancellationToken)
            ?? throw new InvalidOperationException($"Run {run.Id} is budget-parked with no stream to resume.");

        // Re-read as late as it can be, immediately before the irreversible half: everything
        // above this line is reads, and a lap opening concurrently commits its
        // PullRequestReviewLapOpened at some point during them (h9k pr review's own
        // RecordLapOpenedAsync, whose LapAttachment doc has the other side of this race). The
        // check above is the cheap reject that avoids loading the aggregate at all; this one is
        // the one that matters, and it narrows what is left of the window to this method's own
        // read-to-spawn gap. TaskDetails is an inline projection, so a committed lap is visible
        // here the moment it commits.
        TaskDetails? taskNow = await session.LoadAsync<TaskDetails>(run.TaskId, cancellationToken);
        if (taskNow is not null && IsUnderOpenReviewLap(taskNow, run))
        {
            logger.LogInformation(
                "Run {RunId}: task {TaskId}'s review lap opened while this retry was being prepared — not retried",
                run.Id, run.TaskId);
            return false;
        }
        if (aggregate.ReviewPhase != ReviewPhase.None)
        {
            // The park caught a review pass or the fix session, not the primary agent: the
            // exhausted leg was already cleared when RunBudgetExhausted landed, so there is no
            // process to --resume — re-entering the loop redispatches it fresh (backlog 40).
            supervisor.ResumeReviewLoop(run, cancellationToken);
            logger.LogInformation(
                "Run {RunId}: retried after token-budget exhaustion mid-review — resuming the review loop", run.Id);
            return true;
        }

        if (task.Type == TaskType.PrReview && aggregate.PrReviewConformanceBudgetExhausted)
        {
            // The pr-review task's own loop never touches ReviewPhase (PrReviewEngine's class
            // doc comment explains why), so this is its equivalent of the branch above: the
            // park caught the conformance lens, not the primary adversarial session, and there
            // is no process left to --resume — PrReviewEngine.ReviewAsync redispatches it fresh.
            supervisor.ResumeReviewLoop(run, cancellationToken);
            logger.LogInformation(
                "Run {RunId}: retried after token-budget exhaustion mid-pr-review-conformance — resuming the review loop", run.Id);
            return true;
        }

        try
        {
            SpawnedAgent agent = await primarySessionResumer.ResumeAsync(
                session, run, task, project, AgentPromptBuilder.BuildBudgetRetry(task), cancellationToken);
            await session.SaveChangesAsync(cancellationToken);

            supervisor.StartMonitoring(
                run.Id, run.RunDirectory, run.TaskId, agent.ProcessId, agent.StartedAt, cancellationToken);
            logger.LogInformation(
                "Run {RunId}: retried after token-budget exhaustion (pid {ProcessId})", run.Id, agent.ProcessId);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Run {RunId}: token-budget retry failed; will retry next sweep", run.Id);
            return false;
        }
    }

    /// <summary>
    /// Keyed on the run as well as the flag, the same pair <c>RunSupervisor.AdoptOrphansAsync</c>
    /// reads: a flag alone would shield a LATER, automated run of the same task from this sweep on
    /// the strength of a lap that ended (independent pre-PR review, cycle 1, adversarial lens).
    /// One method rather than two copies of the predicate, because it is asked twice — once
    /// cheaply on entry, once immediately before the resume.
    /// </summary>
    private static bool IsUnderOpenReviewLap(TaskDetails task, RunDetails run) =>
        task.ReviewLapOpen && task.ReviewLapRunId == run.Id;
}

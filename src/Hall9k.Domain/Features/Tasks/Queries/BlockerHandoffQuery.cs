using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Marten;

namespace Hall9k.Domain.Features.Tasks.Queries;

/// <summary>
/// One blocker as the dependent's starting context sees it (Decisions Log #36): what the
/// blocker set out to do, and what the run that actually closed it out handed down. The
/// objective and acceptance criteria travel unconditionally, because they are the honest
/// fallback when there is no handoff — a blocker with nothing to say still says what it was
/// for.
/// </summary>
public sealed record BlockerHandoff(
    Guid TaskId,
    string Objective,
    IReadOnlyList<string> AcceptanceCriteria,
    TaskState State,
    HandoffOutcome Outcome,
    string? Summary)
{
    /// <summary>Whether this blocker contributes real handoff text rather than only its intent.</summary>
    public bool HasSummary => Outcome.HasSummary && Summary.IsNotBlank();
}

/// <summary>
/// Assembles the context a task would receive if it were claimed now, reading its
/// <c>BlockedBy</c> edges at <strong>depth one only</strong> (Decisions Log #36).
/// <para>
/// The depth limit is deliberate, not an optimization. A chain A → B → C hands C what B
/// learned and nothing of A's: if C genuinely needs a fact from A, that is evidence of a
/// missing A → C edge, which is a thing a human can fix, rather than a context gap the
/// platform should paper over by walking further back. Accumulating ancestry would also
/// mean that by the time a long chain reaches its end, most of what the agent reads is
/// about work it will never touch.
/// </para>
/// <para>
/// <see cref="TaskDependencyQuery.LoadGraphAsync"/> still loads the transitive closure —
/// cycle detection at publish needs to see a cycle anywhere in the chain. Context assembly
/// reads the first hop and stops.
/// </para>
/// </summary>
public static class BlockerHandoffQuery
{
    /// <summary>
    /// The handoffs of <paramref name="blockedBy"/>, in declared order. Ids naming no known
    /// task are skipped: a dangling edge is the dependency query's story to tell, and
    /// inventing a blocker here would be a guess.
    /// </summary>
    public static async Task<IReadOnlyList<BlockerHandoff>> LoadAsync(
        IQuerySession session, IReadOnlyList<Guid> blockedBy, CancellationToken cancellationToken)
    {
        if (blockedBy.Count == 0)
        {
            return [];
        }

        Guid[] wanted = [.. blockedBy.Distinct()];
        IReadOnlyList<TaskDetails> blockers = await session.Query<TaskDetails>()
            .Where(task => task.Id.IsOneOf(wanted))
            .ToListAsync(cancellationToken);
        Dictionary<Guid, TaskDetails> byId = blockers.ToDictionary(task => task.Id);

        IReadOnlyDictionary<Guid, RunDetails> closedOut =
            await ClosedOutRunsAsync(session, wanted, cancellationToken);

        return
        [
            .. blockedBy
                .Select(id => byId.GetValueOrDefault(id))
                .OfType<TaskDetails>()
                .Select(blocker => Handoff(blocker, closedOut.GetValueOrDefault(blocker.Id))),
        ];
    }

    private static BlockerHandoff Handoff(TaskDetails blocker, RunDetails? closedOutRun) => new(
        blocker.Id,
        blocker.Objective,
        [.. blocker.AcceptanceCriteria],
        blocker.State,
        // No closed-out run is its own answer, distinct from a run that closed out and had
        // nothing to say: the blocker is still in flight, or a human attested it Done without
        // a merge (log #27). Either way nothing carried it to true closeout, and saying that
        // is honest where NotCaptured would describe "the run that closed this out" — a run
        // this blocker does not have.
        closedOutRun?.HandoffOutcome ?? HandoffOutcome.NotClosedOut,
        closedOutRun?.HandoffSummary);

    /// <summary>
    /// The run that carried each blocker to true closeout — <see cref="RunState.Completed"/>,
    /// which only the closeout monitor's merge observation produces. Selecting on the run's
    /// own terminal state is what makes the retry case correct by construction: a blocker that
    /// died and was later retried to a successful merge has a failed run and a completed one,
    /// and only the completed one can be picked. The newest wins where a task somehow closed
    /// out twice, because the newest merge is the one the dependent builds on.
    /// <para>
    /// This picks one run and still hands down the whole task's work, because the summary that
    /// run carries was composed at closeout from every run on the pull request, superseded ones
    /// included (<c>CloseoutEngine.ComposeHandoffAsync</c>). Reading the completing run is
    /// therefore not the same as reading the run that happened to observe the merge — which,
    /// since review follow-ups are automatic (log #22), is almost never the run that did the
    /// work.
    /// </para>
    /// </summary>
    private static async Task<IReadOnlyDictionary<Guid, RunDetails>> ClosedOutRunsAsync(
        IQuerySession session, Guid[] blockerIds, CancellationToken cancellationToken)
    {
        IReadOnlyList<RunDetails> runs = await session.Query<RunDetails>()
            .Where(run => run.TaskId.IsOneOf(blockerIds))
            .ToListAsync(cancellationToken);

        return runs
            .Where(run => run.State == RunState.Completed)
            .GroupBy(run => run.TaskId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(run => run.FinishedAt ?? run.DispatchedAt).First());
    }
}

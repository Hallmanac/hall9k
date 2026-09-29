using Hall9k.Connectors.Prompts;
using Hall9k.Daemon.AutoPrReview;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Infrastructure.Extensions;
using Marten;

namespace Hall9k.Daemon.Review;

/// <summary>
/// Where a review-park resolution another owner's node replicated here reaches a prompt besides the
/// fix session's findings (security review idea 6be68ee2). <c>ReviewParkResolved</c> is
/// project-scoped, so a teammate's node can resolve a park on a run this node owns, and its reason
/// then reaches three more places worded as this owner's own decision: the guidance a rebase-recovery
/// session is handed, the guidance a settling-gate repair session is handed, and the settled rulings
/// a review pass is told not to re-raise. Each is judged here by the verified sender of the event
/// itself, the same test the fix session applies, and never by a field of the payload.
/// </summary>
internal static class ReplicatedResolutionFencing
{
    /// <summary>
    /// <paramref name="guidance"/> (the reason a park's needs-fixes resolve carried) fenced and
    /// labelled when the run stream's newest <see cref="ReviewParkResolved"/> came from a node outside
    /// the local owner's fleet, and whether it was. Blank guidance passes through without a read, so
    /// the ordinary fresh-conflict dispatches pay nothing; a native resolution never reads the fleet.
    /// </summary>
    public static async Task<(string? Text, bool IsForeign)> FenceGuidanceAsync(
        IQuerySession query, LocalFleetProvider? fleets, Guid projectId, Guid runId, string? guidance,
        CancellationToken cancellationToken)
    {
        if (guidance.IsBlank())
        {
            return (guidance, false);
        }

        ReplicatedFrom from = await ReplicatedSender.OfLatestAsync<ReviewParkResolved>(query, runId, cancellationToken);
        if (from.SenderNodeId is not { } sender)
        {
            return (guidance, false);
        }

        LocalFleet? localFleet = fleets is null ? null : await fleets.GetAsync(projectId, cancellationToken);
        return ReplicatedNote.IsForeign(sender, from.OriginNodeId, localFleet)
            ? (ReplicatedNote.ForeignReviewResolution(guidance, sender, from.OriginNodeId, localFleet), true)
            : (guidance, false);
    }

    /// <summary>
    /// <paramref name="rulings"/> with every resolution a foreign node replicated marked
    /// (<see cref="ReviewParkResolution.ForeignNote"/>), so a review prompt shows it labelled and
    /// fenced instead of as this owner's settled ruling. The projection keeps no sender (a row written
    /// before one existed would read as native, the wrong answer), so each ruling is matched back to
    /// its event on the task's run streams by what the event itself carries. Reads nothing when there
    /// are no rulings, and the fleet only when some event was replicated.
    /// </summary>
    public static async Task<IReadOnlyList<ReviewParkResolution>> FenceRulingsAsync(
        IQuerySession query, LocalFleetProvider? fleets, Guid projectId, Guid taskId,
        IReadOnlyList<ReviewParkResolution> rulings, CancellationToken cancellationToken)
    {
        if (rulings.Count == 0)
        {
            return rulings;
        }

        IReadOnlyList<Guid> runIds = await query.Query<RunDetails>()
            .Where(run => run.TaskId == taskId)
            .Select(run => run.Id)
            .ToListAsync(cancellationToken);
        List<(ReviewParkResolved Resolved, ReplicatedFrom From)> replicated = [];
        foreach (Guid runId in runIds)
        {
            replicated.AddRange((await ReplicatedSender.AllAsync<ReviewParkResolved>(query, runId, cancellationToken))
                .Where(entry => entry.From.SenderNodeId is not null));
        }

        if (replicated.Count == 0)
        {
            return rulings;
        }

        LocalFleet? localFleet = fleets is null ? null : await fleets.GetAsync(projectId, cancellationToken);
        return
        [
            .. rulings.Select(ruling => ForeignSenderOf(ruling, replicated, localFleet) is { SenderNodeId: { } foreignSender } foreignFrom
                ? ruling with
                {
                    ForeignNote = ReplicatedNote.ForeignRuling(
                        ruling.Verdict == ReviewVerdict.MergeReady ? "merge-ready" : "needs-fixes",
                        ruling.Reason, foreignSender, foreignFrom.OriginNodeId, localFleet),
                }
                : ruling),
        ];
    }

    /// <summary>
    /// The sender and origin of a replicated event that matches <paramref name="ruling"/> (same verdict, reason
    /// and time) and is foreign, or null. Any foreign match counts, the fail-closed side of a
    /// coincidence nobody expects.
    /// </summary>
    private static ReplicatedFrom? ForeignSenderOf(
        ReviewParkResolution ruling, IReadOnlyList<(ReviewParkResolved Resolved, ReplicatedFrom From)> replicated,
        LocalFleet? localFleet)
    {
        foreach ((ReviewParkResolved resolved, ReplicatedFrom from) in replicated)
        {
            if (resolved.Verdict == ruling.Verdict
                && resolved.Reason == ruling.Reason
                && resolved.ResolvedAt == ruling.ResolvedAt
                && from.SenderNodeId is { } sender
                && ReplicatedNote.IsForeign(sender, from.OriginNodeId, localFleet))
            {
                return from;
            }
        }

        return null;
    }
}

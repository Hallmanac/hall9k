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
/// a review pass is told not to re-raise. A human-directed interaction logged on a run reaches the
/// same review pass as a standing directive, and is judged the same way. Each is judged here by the
/// verified sender of the event itself, the same test the fix session applies, and never by a field
/// of the payload.
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
    /// (<see cref="ReviewParkResolution.ForeignNote"/>), and <paramref name="interactions"/> with every
    /// human-directed interaction one replicated marked
    /// (<see cref="ExternalInteractionRecord.ForeignNote"/>), so a review prompt shows each labelled and
    /// fenced instead of as this owner's settled ruling or a standing directive from this owner's human.
    /// Reads nothing when both lists are empty, and the fleet only when some event was replicated
    /// (<see cref="ReplicatedRunFencing"/>).
    /// </summary>
    public static async Task<(IReadOnlyList<ReviewParkResolution> Rulings, IReadOnlyList<ExternalInteractionRecord> Interactions)> FencePriorAsync(
        IQuerySession query, LocalFleetProvider? fleets, Guid projectId, Guid taskId,
        IReadOnlyList<ReviewParkResolution> rulings, IReadOnlyList<ExternalInteractionRecord> interactions,
        CancellationToken cancellationToken)
    {
        if (rulings.Count == 0 && interactions.Count == 0)
        {
            return (rulings, interactions);
        }

        IReadOnlyList<Guid> runIds = await query.Query<RunDetails>()
            .Where(run => run.TaskId == taskId)
            .Select(run => run.Id)
            .ToListAsync(cancellationToken);
        ReplicatedRunEvents replicated = await ReplicatedRunFencing.ReadAsync(query, runIds, cancellationToken);
        ValueTask<LocalFleet?> ReadFleet(CancellationToken token) =>
            fleets is null ? ValueTask.FromResult<LocalFleet?>(null) : fleets.GetAsync(projectId, token);

        return (
            await ReplicatedRunFencing.FenceRulingsAsync(rulings, replicated, ReadFleet, cancellationToken),
            await ReplicatedRunFencing.FenceInteractionsAsync(interactions, replicated, ReadFleet, cancellationToken));
    }
}

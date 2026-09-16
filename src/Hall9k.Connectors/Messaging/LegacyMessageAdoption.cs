using Hall9k.Domain.Features.Project.Projections;
using Marten;

namespace Hall9k.Connectors.Messaging;

/// <summary>
/// The one rule both halves of the message seam must agree on identically for a message queued or
/// received before idea 202383dc's M2 shipped (still carrying <see cref="Guid.Empty"/> as its own
/// local project id): which of this node's own eligible projects is "the adopting project" — the
/// lowest-id eligible one, the identical rule the old, single-project sweep always picked by
/// (<c>MessageSweepEngine.SweepOnceAsync</c>'s own doc). <see cref="MessageOutbox.NextSeqAsync"/>
/// uses this to keep a brand-new send from allocating a seq a still-pending or already-sent legacy
/// message already holds, and <see cref="MessageInbox.ReadFromAsync"/> uses it to decide whether a
/// fresh per-project cursor may fall back to the pre-M2 cursor instead of re-reading from zero
/// (independent pre-PR review, cycle 1, both lenses). Deliberately the static, eligibility-only
/// rule — never the sweep's own dynamic per-tick fallback to a later eligible project when the
/// lowest-id one's trust chain read or genesis check fails that tick
/// (<c>MessageSweepEngine.SweepOnceAsync</c>'s own doc) — because neither <see cref="MessageOutbox.QueueAsync"/>
/// nor a receive-side read may ever compute a live trust chain themselves: queueing is deliberately
/// git- and network-free, and a receive-side cursor fallback that guessed at ANY eligible project
/// rather than the one project whose repository actually held the pre-M2 content would risk
/// silently skipping content a project's own first post-upgrade read never saw before. A narrow
/// residual gap follows from that choice: if the lowest-id eligible project's own trust chain read
/// stays broken tick after tick, the sweep's own resilience falls through to a different eligible
/// project as the actual adopter, and a message queued directly at that fallback project in the
/// same window could in principle still collide with a legacy seq — accepted rather than solved
/// here, for the reasons above, and no more likely than the sweep-stuck-forever failure this same
/// fallback exists to fix in the first place.
/// </summary>
internal static class LegacyMessageAdoption
{
    public static async Task<bool> IsAdoptingProjectAsync(
        IQuerySession session, Guid projectId, CancellationToken cancellationToken)
    {
        IReadOnlyList<ProjectDetails> allProjects = await session.Query<ProjectDetails>().ToListAsync(cancellationToken);
        ProjectDetails? lowestEligible = allProjects
            .Where(project => project.IsEligibleForMessaging())
            .OrderBy(project => project.Id)
            .FirstOrDefault();
        return lowestEligible is not null && lowestEligible.Id == projectId;
    }
}

using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Project.Projections;
using Marten;

namespace Hall9k.Connectors.Messaging;

/// <summary>
/// The one rule both halves of the message seam must agree on identically for a message queued or
/// received before idea 202383dc's M2 shipped (still carrying <see cref="Guid.Empty"/> as its own
/// local project id): which of this node's own eligible projects is "the adopting project".
/// <see cref="MessageOutbox.NextSeqAsync"/> uses this to keep a brand-new send from allocating a seq
/// a still-pending or already-sent legacy message already holds, and
/// <see cref="MessageInbox.ReadFromAsync"/> uses it to decide whether a fresh per-project cursor may
/// fall back to the pre-M2 cursor instead of re-reading from zero (independent pre-PR review, cycle
/// 1, both lenses).
/// <para>
/// <see cref="IsAdoptingProjectAsync"/> answers from a persisted, permanent decision
/// (<see cref="LegacyMessageAdoptionDetails"/>) once one exists, and only falls back to a static
/// guess — the lowest-id eligible project, the rule the old, single-project sweep always picked by
/// (<c>MessageSweepEngine.SweepOnceAsync</c>'s own doc) — before it does. Neither
/// <see cref="MessageOutbox.QueueAsync"/> nor a receive-side read may ever compute a live trust
/// chain themselves to answer this question directly: queueing is deliberately git- and
/// network-free, and a receive-side cursor fallback that guessed at ANY eligible project rather
/// than the one project whose repository actually held the pre-M2 content would risk silently
/// skipping content a project's own first post-upgrade read never saw before. The persisted decision
/// is what keeps that static guess from staying wrong forever: <see cref="AssignAsync"/> records it,
/// once, the first time <c>MessageSweepEngine.SweepOnceAsync</c>'s own dynamic per-tick fallback
/// (falling through to a later eligible project when an earlier one's trust chain read or genesis
/// check fails that tick) actually completes a flush with adoption on — from that point forward,
/// every caller here agrees with the sweep's own real behavior instead of the static guess, even
/// when that guess would have named a project whose trust chain read never actually succeeds
/// (independent pre-PR review, cycle 2, verify pass, medium and low).
/// </para>
/// <para>
/// A narrow residual gap remains, deliberately: between the moment a chronically broken lowest-id
/// project first starts failing and the sweep's first tick that actually completes an adopting
/// flush through a healthier project, <see cref="IsAdoptingProjectAsync"/> still has no persisted
/// decision to answer from and falls back to the static guess, which can disagree with what that
/// first successful tick is about to do — a message queued or a sender first read in that exact
/// window could still collide with or misjudge a legacy cursor the same way the whole-lifetime
/// version of this gap used to. Accepted rather than solved: closing it fully would mean
/// <see cref="MessageOutbox.QueueAsync"/> computing a live trust chain itself, which the git- and
/// network-free queueing guarantee above rules out. This window closes for good, forever, the
/// moment the first adopting flush anywhere ever succeeds — never reopens once
/// <see cref="AssignAsync"/> has recorded an answer.
/// </para>
/// </summary>
/// <remarks>Public, not internal: <see cref="AssignAsync"/> is called from
/// <c>Hall9k.Daemon.Messaging.MessageSweepEngine</c>, a different assembly (the reference graph in
/// AGENTS.md already has Daemon depend on Connectors), right after its own adopting flush succeeds
/// — the one place with the live knowledge of which project that was.</remarks>
public static class LegacyMessageAdoption
{
    public static async Task<bool> IsAdoptingProjectAsync(
        IQuerySession session, Guid projectId, CancellationToken cancellationToken)
    {
        LegacyMessageAdoptionDetails? assigned = await session.LoadAsync<LegacyMessageAdoptionDetails>(
            MessageStreamId.ForLegacyAdoption(), cancellationToken);
        if (assigned is not null)
        {
            return assigned.ProjectId == projectId;
        }

        IReadOnlyList<ProjectDetails> allProjects = await session.Query<ProjectDetails>().ToListAsync(cancellationToken);
        ProjectDetails? lowestEligible = allProjects
            .Where(project => project.IsEligibleForMessaging())
            .OrderBy(project => project.Id)
            .FirstOrDefault();
        return lowestEligible is not null && lowestEligible.Id == projectId;
    }

    /// <summary>
    /// Permanently records <paramref name="projectId"/> as this install's own legacy adopter — a
    /// no-op once a decision already exists, whether or not it names the identical project, since
    /// only the FIRST project to ever actually complete an adopting flush ever earns this: the
    /// legacy <see cref="Guid.Empty"/>-project backlog either got folded into that one flush's own
    /// seq accounting and batch or it did not, and a second, later "adopting" flush (the sweep's own
    /// dynamic per-tick election choosing a different, also-healthy project on some later tick, with
    /// nothing left to adopt) must never displace the answer every earlier queue-time and read-time
    /// caller already relied on. Called only by <c>MessageSweepEngine</c>, immediately after its own
    /// <c>MessageOutbox.FlushAsync</c> call with <c>adoptUnassigned: true</c> returns without
    /// throwing — never before that flush is known to have actually succeeded, or a chain read that
    /// merely succeeded while the push itself failed would wrongly pin a project this backlog never
    /// actually reached.
    /// </summary>
    public static async Task AssignAsync(
        IDocumentSession session, Guid projectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Guid streamId = MessageStreamId.ForLegacyAdoption();
        LegacyMessageAdoptionAggregate? existing =
            await session.Events.AggregateStreamAsync<LegacyMessageAdoptionAggregate>(streamId, token: cancellationToken);
        if (existing is not null)
        {
            return;
        }

        session.Events.StartStream<LegacyMessageAdoptionAggregate>(streamId, new LegacyMessageAdoptionAssigned(projectId, now));
    }
}

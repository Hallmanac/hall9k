using Hall9k.Domain.Features.Idea;
using Marten;
using Marten.Events;
using Marten.Linq.MatchesSql;

namespace Hall9k.Domain.Infrastructure.Persistence;

/// <summary>
/// Re-projects idea streams whose documents were last written before the fan-out redesign
/// (backlog 31) renamed <c>IdeaDetails</c>'s ending fields: <c>discardReason</c>/<c>discardedAt</c>/
/// <c>promotedTaskId</c>/<c>promotedAt</c> became <c>archiveReason</c>/<c>archivedAt</c>/
/// <c>cutTaskIds</c>/<c>concludedAt</c>. The projection is Inline, so a document is only ever
/// rewritten when its stream gets a new event — a finished idea (concluded or archived) is
/// terminal and never gets one, so it keeps the old key names forever. Every reader of the new
/// field names — <c>h9k idea show</c>'s <c>AnnounceOutcome</c> and the daemon's
/// <c>ProjectHomeRenderEngine</c> sweep that rewrites <c>idea.md</c> — reads a stale document as
/// though the reason, date, and task link were never recorded, even though the events (and a
/// fresh <see cref="IdeaAggregate"/> replay) still say them plainly. The same class of defect
/// <see cref="TaskLifecycleProjectionBackfill"/> exists for on the task side; this is its idea
/// counterpart (independent pre-PR review — conformance and adversarial lenses, cycle 1).
/// </summary>
public static class IdeaDetailsProjectionBackfill
{
    /// <summary>
    /// What an out-of-date idea document looks like: a key the current projection always writes
    /// — as a value, an explicit null, or an empty array — is simply absent. <c>cutTaskIds</c>
    /// alone would suffice (it never existed under any name before this shape landed), but
    /// <c>archiveReason</c> and <c>concludedAt</c> are checked too so the marker set stays
    /// self-describing about which fields actually moved.
    /// </summary>
    private const string StaleDocument =
        "(not jsonb_exists(d.data, 'cutTaskIds')"
        + " or not jsonb_exists(d.data, 'archiveReason')"
        + " or not jsonb_exists(d.data, 'concludedAt'))";

    /// <summary>
    /// Rebuilds every idea stream still carrying an out-of-date document and returns the ids it
    /// actually rebuilt. Idempotent and self-terminating: a rebuilt document has every key, so
    /// the next call finds nothing.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> RunAsync(
        IDocumentStore store, CancellationToken cancellationToken)
    {
        Guid[] stale = await StaleStreamsAsync(store, cancellationToken);
        if (stale.Length == 0)
        {
            return [];
        }

        List<Guid> rebuilt = [];
        foreach (Guid streamId in stale)
        {
            if (await RebuildAsync(store, streamId, cancellationToken))
            {
                rebuilt.Add(streamId);
            }
        }

        return rebuilt;
    }

    /// <summary>
    /// Replays one stream into the idea document, restored at the stream's own version, and says
    /// whether it did. Deleting and re-storing at the version the events actually reach (rather
    /// than Marten's own <c>RebuildSingleStreamAsync</c>, which stores one version ahead) leaves
    /// the document where ordinary Inline projection left it, so the stream carries on normally
    /// — see <see cref="TaskLifecycleProjectionBackfill.RebuildAsync"/> for why that distinction
    /// matters.
    /// </summary>
    private static async Task<bool> RebuildAsync(
        IDocumentStore store, Guid streamId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();

        StreamState? state = await session.Events.FetchStreamStateAsync(streamId, cancellationToken);
        IdeaDetails? details = state is null
            ? null
            : await session.Events.AggregateStreamAsync<IdeaDetails>(streamId, token: cancellationToken);
        if (state is null || details is null)
        {
            return false;
        }

        session.Delete<IdeaDetails>(streamId);
        session.UpdateRevision(details, (int)state.Version);

        await session.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static async Task<Guid[]> StaleStreamsAsync(
        IDocumentStore store, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();

        return [.. await session.Query<IdeaDetails>()
            .Where(idea => idea.MatchesSql(StaleDocument))
            .Select(idea => idea.Id)
            .ToListAsync(cancellationToken)];
    }
}

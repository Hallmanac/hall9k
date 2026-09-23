using JasperFx.Events;
using Hall9k.Domain.Features.Learning;
using Marten;
using Marten.Events;
using Marten.Linq.MatchesSql;

namespace Hall9k.Domain.Infrastructure.Persistence;

/// <summary>
/// Re-projects lesson streams whose documents were written before
/// <see cref="LearningDetails.RecordedOnNodeId"/> existed (idea d805fd8b, piece 5). The same
/// class of defect <see cref="TaskLifecycleProjectionBackfill"/> and
/// <see cref="IdeaDetailsProjectionBackfill"/> exist for, and repaired the same way: the
/// projection is Inline, so a row is only ever rewritten when its stream gets a new event, and a
/// lesson's only later event is <c>LearningRetired</c>, whose <c>Apply</c> never touches the
/// recording node. A row written before this branch therefore keeps a null node forever.
/// <para>
/// That null is not a harmless gap. <see cref="LessonProvenanceMark.Of"/> reads a lesson recorded
/// from a run on an unnamed node as <see cref="LessonProvenanceMark.AgentOnUnobservedNode"/>, so
/// every agent-recorded lesson already on an upgrading install is held out of every prompt and
/// then described, in the prompt and in <c>h9k learn show</c>, as "recorded by an agent run on a
/// node nobody recorded". That is a positive claim about an unobserved fact, and it is false:
/// <c>EventOriginStampingListener</c> stamped the node into the event's own metadata at the time,
/// and a replay reads it straight back (independent pre-PR review, cycle 3, conformance lens).
/// </para>
/// </summary>
public static class LearningDetailsProjectionBackfill
{
    /// <summary>
    /// What an out-of-date lesson document looks like: the one key the current projection always
    /// writes, as a value or as an explicit null, is simply absent. Written as
    /// <c>jsonb_exists</c> rather than the <c>?</c> operator because <c>?</c> is Marten's own
    /// parameter placeholder, and parenthesised so it stays one predicate whatever Marten
    /// conjoins it with.
    /// <para>
    /// <see cref="LearningDetails.DistilledFrom"/> is deliberately NOT a marker, though it landed
    /// in the same change. Absent reads as null there, and null is exactly what that field means
    /// for every lesson that was not recorded as a distillation, so an old row already answers
    /// correctly and rebuilding it would change nothing.
    /// </para>
    /// </summary>
    private const string StaleDocument = "(not jsonb_exists(d.data, 'recordedOnNodeId'))";

    /// <summary>
    /// Rebuilds every lesson stream still carrying an out-of-date document and returns the ids it
    /// actually rebuilt. Idempotent and self-terminating: a rebuilt document carries the key, so
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
    /// Replays one stream into the lesson document, restored at the stream's own version, and
    /// says whether it did. Deleting and re-storing at the version the events actually reach
    /// (rather than Marten's own <c>RebuildSingleStreamAsync</c>, which stores one version ahead)
    /// leaves the document where ordinary Inline projection left it, so the stream carries on
    /// normally — see <see cref="TaskLifecycleProjectionBackfill.RebuildAsync"/> for why that
    /// distinction matters.
    /// </summary>
    private static async Task<bool> RebuildAsync(
        IDocumentStore store, Guid streamId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();

        StreamState? state = await session.Events.FetchStreamStateAsync(streamId, cancellationToken);
        LearningDetails? details = state is null
            ? null
            : await session.Events.AggregateStreamAsync<LearningDetails>(streamId, token: cancellationToken);
        if (state is null || details is null)
        {
            return false;
        }

        session.Delete<LearningDetails>(streamId);
        session.UpdateRevision(details, (int)state.Version);

        await session.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static async Task<Guid[]> StaleStreamsAsync(
        IDocumentStore store, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();

        return [.. await session.Query<LearningDetails>()
            .Where(learning => learning.MatchesSql(StaleDocument))
            .Select(learning => learning.Id)
            .ToListAsync(cancellationToken)];
    }
}

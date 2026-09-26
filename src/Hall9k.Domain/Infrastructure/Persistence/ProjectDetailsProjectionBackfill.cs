using JasperFx.Events;
using Hall9k.Domain.Features.Project.Projections;
using Marten;
using Marten.Events;
using Marten.Linq.MatchesSql;

namespace Hall9k.Domain.Infrastructure.Persistence;

/// <summary>
/// Re-projects project streams whose documents were written before
/// <see cref="ProjectDetails.TeamSettingStamps"/> existed. The same class of defect
/// <see cref="LearningDetailsProjectionBackfill"/> and <see cref="IdeaDetailsProjectionBackfill"/>
/// exist for, repaired the same way: the projection is Inline, so a stored document is loaded and
/// only new events are applied to it, and its stamp dictionaries deserialize empty.
/// <para>
/// An empty stamp dictionary is not a harmless gap. The stamps are what stop a pre-switch-on head,
/// which a catch-up answer now delivers after the tail, from overwriting a newer team setting,
/// member decision or prompt addendum: with no stamp recorded a key accepts anything, so the older
/// head would win on exactly the nodes the catch-up repair is meant for, and
/// <c>PromptAddendaSweepEngine</c> would then push the stale addendum over the shared ledger file.
/// A replay applies every event already in the stream through the stamping projection, which
/// records the stamps the stored document never had.
/// </para>
/// </summary>
public static class ProjectDetailsProjectionBackfill
{
    /// <summary>
    /// What an out-of-date project document looks like: the one key the current projection always
    /// writes, as an object even when empty, is simply absent. Written as <c>jsonb_exists</c>
    /// rather than the <c>?</c> operator because <c>?</c> is Marten's own parameter placeholder,
    /// and parenthesised so it stays one predicate whatever Marten conjoins it with.
    /// </summary>
    private const string StaleDocument = "(not jsonb_exists(d.data, 'teamSettingStamps'))";

    /// <summary>
    /// Rebuilds every project stream still carrying an out-of-date document and returns the ids it
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
    /// Replays one stream into the project document, restored at the stream's own version, and
    /// says whether it did; see <see cref="LearningDetailsProjectionBackfill"/> for why the
    /// document is deleted and re-stored at that version rather than rebuilt through Marten.
    /// </summary>
    private static async Task<bool> RebuildAsync(
        IDocumentStore store, Guid streamId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();

        StreamState? state = await session.Events.FetchStreamStateAsync(streamId, cancellationToken);
        ProjectDetails? details = state is null
            ? null
            : await session.Events.AggregateStreamAsync<ProjectDetails>(streamId, token: cancellationToken);
        if (state is null || details is null)
        {
            return false;
        }

        session.Delete<ProjectDetails>(streamId);
        session.UpdateRevision(details, (int)state.Version);

        await session.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static async Task<Guid[]> StaleStreamsAsync(
        IDocumentStore store, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();

        return [.. await session.Query<ProjectDetails>()
            .Where(project => project.MatchesSql(StaleDocument))
            .Select(project => project.Id)
            .ToListAsync(cancellationToken)];
    }
}

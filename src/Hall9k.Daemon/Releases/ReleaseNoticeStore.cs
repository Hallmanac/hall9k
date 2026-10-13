using Hall9k.Domain.Features.Node;
using JasperFx.Events;
using Marten;

namespace Hall9k.Daemon.Releases;

/// <summary>What this node last told its person about a release, as <see cref="NodeDetails"/> carries it.</summary>
public sealed record ReleaseNoticeState(string? LastNoticeTag, DateTimeOffset? LastNoticeAt);

/// <summary>
/// Where <see cref="ReleaseNoticeEngine"/> reads the last notice and records the next one. Reading
/// and appending are one call so the decision and the append share a single version-pinned save:
/// the engine hands in its decision and the store runs it against the state it just read.
/// </summary>
public interface IReleaseNoticeStore
{
    /// <summary>
    /// Reads this node's last notice, asks <paramref name="decide"/> for a notice to record, and
    /// appends it to the node's own stream when it returns one. Returns the notice recorded, or null.
    /// </summary>
    Task<ReleaseNoticeIssued?> RecordIfDueAsync(
        Guid nodeId, Func<ReleaseNoticeState, ReleaseNoticeIssued?> decide, CancellationToken cancellationToken);
}

/// <summary>
/// The Marten-backed store. The append is pinned to the stream version it read, the way
/// <c>LaunchHoldEngine</c>'s are, because other writers share this node stream (the launch hold,
/// the gate baseline) and an unpinned append can lose that race and throw
/// <see cref="EventStreamUnexpectedMaxEventIdException"/>; replaying the whole read-decide-append
/// against the now-current stream is always safe for a notice.
/// </summary>
public sealed class MartenReleaseNoticeStore(IDocumentStore store) : IReleaseNoticeStore
{
    private const int MaxAppendAttempts = 5;

    public async Task<ReleaseNoticeIssued?> RecordIfDueAsync(
        Guid nodeId, Func<ReleaseNoticeState, ReleaseNoticeIssued?> decide, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using IDocumentSession session = store.LightweightSession();
                StreamState? streamState = await session.Events.FetchStreamStateAsync(nodeId, cancellationToken);
                NodeDetails? details = await session.LoadAsync<NodeDetails>(nodeId, cancellationToken);
                ReleaseNoticeIssued? notice = decide(new ReleaseNoticeState(
                    details?.LastReleaseNoticeTag, details?.LastReleaseNoticeAt));
                if (notice is null)
                {
                    return null;
                }

                session.Events.Append(nodeId, expectedVersion: (streamState?.Version ?? 0) + 1, notice);
                await session.SaveChangesAsync(cancellationToken);
                return notice;
            }
            catch (EventStreamUnexpectedMaxEventIdException) when (attempt < MaxAppendAttempts && !cancellationToken.IsCancellationRequested)
            {
                // Lost the race to another writer on this node's stream: read and decide again.
            }
        }
    }
}

using Hall9k.Domain.Features.Replication;
using Marten;

namespace Hall9k.Connectors.Replication;

/// <summary>How many streams this node holds only the tail of, and how many of those it has
/// stopped asking about — the two figures <c>h9k status</c> reports.</summary>
/// <param name="StreamsHeldTailOnly">Streams with held records still being chased.</param>
/// <param name="StreamsGivenUp">
/// Streams whose held records reached <see cref="EventCatchUpCoordinator.MaxHeldTailAttempts"/>.
/// Counted apart from, never inside, <paramref name="StreamsHeldTailOnly"/>: the two answer
/// different questions, and a human reading one number that silently included the other could not
/// tell a backlog draining from a backlog nothing is working on.
/// </param>
public sealed record HeldTailSummary(int StreamsHeldTailOnly, int StreamsGivenUp);

/// <summary>
/// The two reads of <see cref="HeldReplicatedEventRecord"/> that both the daemon's held-tail sweep
/// (<see cref="EventCatchUpCoordinator.RequestHeldTailStreamsAsync"/>) and <c>h9k status</c> need,
/// in one place so the sweep's own idea of a given-up stream and the number a human reads can
/// never disagree.
/// <para>
/// Both fold per STREAM rather than per record, which is the unit the whole feature is about: one
/// stream's tail is one missing genesis however many events are waiting on it, and 42 held records
/// over 14 streams is 14 things to chase, not 42.
/// </para>
/// </summary>
public static class HeldTailStreams
{
    /// <summary>
    /// Every stream in <paramref name="projectId"/> carrying at least one held record whose own
    /// give-up still stands against <paramref name="currentBuildVersion"/>
    /// (<see cref="EventCatchUpCoordinator.GivenUpMarkStillStands"/>) — a record given up on an
    /// older build counts as not given up here, the build-version bound lift this node's own sweep
    /// applies before this read ever runs (<see cref="EventCatchUpCoordinator.RequestHeldTailStreamsAsync"/>
    /// resets such a record's own <see cref="HeldReplicatedEventRecord.CatchUpGivenUp"/> and
    /// <see cref="HeldReplicatedEventRecord.CatchUpAttempts"/> before this method is ever called,
    /// so this is normally a belt-and-braces read of state that already agrees).
    /// <para>
    /// The mark is per record but the verdict is per stream, and this is what makes that so: a
    /// held record that arrives AFTER its stream reached the attempt stop carries a count of zero
    /// of its own, and without this set that one fresh record would put its stream back in the ask
    /// rotation and start the whole three-attempt cycle again on every later arrival — which for a
    /// tail that keeps growing behind a genesis nobody holds is the unbounded loop the stop exists
    /// to prevent.
    /// </para>
    /// </summary>
    public static async Task<HashSet<Guid>> GivenUpStreamIdsAsync(
        IQuerySession session, Guid projectId, string currentBuildVersion, CancellationToken cancellationToken)
    {
        // Projected to the two scalar fields GivenUpMarkStillStands actually needs, not the whole
        // document: a held record carries the wire-format event it is waiting to apply
        // (RecordJson), and this read has no business loading hundreds of those just to learn which
        // streams still stand given up (independent pre-PR review, cycle 1, adversarial lens, low —
        // the same reasoning SummarizeAsync below already documents for its own stream-id-only read).
        IReadOnlyList<GivenUpMark> givenUp = await session.Query<HeldReplicatedEventRecord>()
            .Where(record => record.ProjectId == projectId && record.CatchUpGivenUp)
            .Select(record => new GivenUpMark(record.StreamId, record.GivenUpOnBuildVersion))
            .ToListAsync(cancellationToken);
        return [.. givenUp
            .Where(mark => EventCatchUpCoordinator.GivenUpMarkStillStands(mark.GivenUpOnBuildVersion, currentBuildVersion))
            .Select(mark => mark.StreamId)];
    }

    private sealed record GivenUpMark(Guid StreamId, string? GivenUpOnBuildVersion);

    /// <summary>
    /// The node-wide counts, across every project — <c>h9k status</c>'s own scope, which is this
    /// machine rather than one project. Selects the stream ids alone rather than the documents:
    /// a held record carries the whole wire-format event it is waiting to apply, and a status pane
    /// has no business reading hundreds of those to count fourteen streams.
    /// </summary>
    public static async Task<HeldTailSummary> SummarizeAsync(
        IQuerySession session, CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> givenUp = await session.Query<HeldReplicatedEventRecord>()
            .Where(record => record.CatchUpGivenUp)
            .Select(record => record.StreamId)
            .ToListAsync(cancellationToken);
        IReadOnlyList<Guid> all = await session.Query<HeldReplicatedEventRecord>()
            .Select(record => record.StreamId)
            .ToListAsync(cancellationToken);

        HashSet<Guid> givenUpStreams = [.. givenUp];
        HashSet<Guid> allStreams = [.. all];
        return new HeldTailSummary(allStreams.Count - givenUpStreams.Count, givenUpStreams.Count);
    }
}

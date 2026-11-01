namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// How long a session another node dispatched may stand with no outcome before it stops counting as running. A node
/// that is alive stops its own session at the session bound and records the outcome itself, so anything still standing
/// an hour later belongs to a node that is not there (a machine rename gives the same install a new node identity and
/// strands what the old one dispatched). The daemon's <c>ForeignPublicationCeiling</c> option defaults to this, and
/// <c>h9k plan start</c>, which cannot read a daemon option, applies the same default when it asks whether a draft has a
/// session running.
/// </summary>
public static class ForeignSessionCeiling
{
    /// <summary>One hour.</summary>
    public static readonly TimeSpan Default = TimeSpan.FromHours(1);

    /// <summary>
    /// Whether a session recorded as dispatched by <paramref name="dispatchedBy"/> at <paramref name="dispatchedAt"/>
    /// still counts as running to a reader on <paramref name="thisNode"/> at <paramref name="now"/>. A session of this
    /// node's own always counts (its recovery records the outcome on the next start); another node's stops counting once it
    /// has stood past <paramref name="ceiling"/>.
    /// </summary>
    public static bool CountsAsRunning(
        Guid? dispatchedBy, DateTimeOffset? dispatchedAt, Guid thisNode, DateTimeOffset now, TimeSpan ceiling) =>
        dispatchedBy == thisNode
        || dispatchedAt is not { } at
        || now - at <= ceiling;
}

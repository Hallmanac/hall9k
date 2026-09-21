using Hall9k.Domain.Features.Replication;

namespace Hall9k.Connectors.Replication;

/// <summary>
/// What one sweep knows about one stream this node holds only the tail of: the held records
/// (<see cref="HeldReplicatedEventRecord"/>) waiting on a genesis, folded together with whatever
/// ask this node has already made for that stream. The whole input
/// <see cref="EventCatchUpCoordinator.PlanHeldTailAsks"/> decides from, so that decision is pure
/// and testable without a database (AGENTS.md's own unit tier).
/// </summary>
/// <param name="StreamId">The stream whose tail is held.</param>
/// <param name="OldestHeldAt">
/// When the oldest of this stream's held records was held. The settle window is measured from
/// here rather than from the newest, because a stream whose tail keeps growing while its genesis
/// stays missing would otherwise never settle and never be asked for at all.
/// </param>
/// <param name="Attempts">
/// The highest <see cref="HeldReplicatedEventRecord.CatchUpAttempts"/> across this stream's own
/// held records — the maximum rather than the minimum, so a record that arrived after the asks
/// began, carrying a count of zero of its own, cannot reset the stop this stream already reached.
/// </param>
/// <param name="MostRecentAskSentAt">
/// When the most recent broadcast request for this exact stream was sent, whoever sent it — this
/// sweep's own earlier ask, or a human's <c>h9k task pull</c>. Null when no request for this
/// stream has ever been recorded here. The cooldown is measured from this, which is what makes a
/// decline and an answer that landed without the genesis both cool the next ask: neither one
/// moves the time the ask went out.
/// </param>
/// <param name="AskOutstanding">Whether a request for this exact stream is still waiting on an answer.</param>
public sealed record HeldTailStreamState(
    Guid StreamId,
    DateTimeOffset OldestHeldAt,
    int Attempts,
    DateTimeOffset? MostRecentAskSentAt,
    bool AskOutstanding);

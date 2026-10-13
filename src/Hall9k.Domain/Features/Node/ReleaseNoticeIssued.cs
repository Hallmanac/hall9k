namespace Hall9k.Domain.Features.Node;

/// <summary>
/// This node's daemon told its person that a newer hall9k release exists (idea 93c1d24d, card 2):
/// either the first notice for that release, or a reminder because the node is still behind a day
/// after the last notice. The daemon only informs; nothing here installs or restarts anything.
/// Node-scoped and never replicated: what a release means for this node depends on the version
/// this node runs, which no other node can judge.
/// </summary>
/// <param name="Id">The node's own stream id, as <see cref="NodeRegistered.Id"/> already keys it.</param>
/// <param name="Tag">The release tag the lookup found, as GitHub names it (<c>v0.13.1</c>).</param>
/// <param name="PublishedAt">When GitHub published that release; null when the lookup's answer carried no time.</param>
/// <param name="ObservedAt">When this node's daemon saw the release; the clock the 24-hour reminder cadence reads.</param>
/// <param name="RunningVersion">What the daemon itself reported running at the time, so the line reads true when replayed later.</param>
/// <param name="IsReminder">True when an earlier notice named this same tag.</param>
public sealed record ReleaseNoticeIssued(
    Guid Id,
    string Tag,
    DateTimeOffset? PublishedAt,
    DateTimeOffset ObservedAt,
    string RunningVersion,
    bool IsReminder);

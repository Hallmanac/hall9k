using Hall9k.Domain.Infrastructure;

namespace Hall9k.Domain.Features.Node;

/// <summary>What the daemon owes its person for one release check.</summary>
public enum ReleaseNoticeKind
{
    /// <summary>Nothing: the node runs the newest release, or it was told about this one less than a day ago.</summary>
    None,

    /// <summary>A release this node has not been told about yet.</summary>
    First,

    /// <summary>The node is still behind a release it was told about at least a day ago.</summary>
    Reminder,
}

/// <summary>
/// When the daemon notifies about a release (idea 93c1d24d, card 2), as a pure function of what
/// the node last recorded, so the cadence is the same after a restart as before it: the only
/// state is the last notified tag and when, which <see cref="NodeDetails"/> carries.
/// </summary>
public static class ReleaseNoticeCadence
{
    /// <summary>How long a node stays quiet about a release it was already told about.</summary>
    public static readonly TimeSpan ReminderAfter = TimeSpan.FromHours(24);

    /// <summary>
    /// A release newer than the running version is announced at the next check when it differs from
    /// the last one announced, which also restarts the clock; the same release is announced again
    /// on the first check at least <see cref="ReminderAfter"/> after the last notice. A node that
    /// runs the newest release is told nothing.
    /// </summary>
    public static ReleaseNoticeKind Decide(
        string foundTag, string runningVersion, string? lastNoticeTag, DateTimeOffset? lastNoticeAt, DateTimeOffset now)
    {
        if (!ReleaseTagVersion.IsNewerThan(foundTag, runningVersion))
        {
            return ReleaseNoticeKind.None;
        }

        if (!string.Equals(lastNoticeTag, foundTag, StringComparison.Ordinal) || lastNoticeAt is not { } lastAt)
        {
            return ReleaseNoticeKind.First;
        }

        return now - lastAt >= ReminderAfter
            ? ReleaseNoticeKind.Reminder
            : ReleaseNoticeKind.None;
    }
}

namespace Hall9k.Daemon;

/// <summary>
/// Event IDs for daemon log lines an external monitor may want to wake on structurally — the
/// EventId plus the message's own named properties — rather than by matching the message's
/// prose, which is free to reword. The default console formatter
/// (<see cref="DaemonLogging"/>) prints the EventId inline (<c>Category[Id]</c>), so a monitor
/// can grep on the bracketed number without parsing a sentence that may change.
/// <para>
/// Origin (2026-08-26): PR #50 sat Delivered for 23 minutes with no signal, because the only
/// PR-open log line was prose ("PR opened ... awaiting review") with nothing structural to
/// key on. This is the first entry in what is meant to grow into the platform's set of
/// wake-worthy log events, not a one-off.
/// </para>
/// </summary>
public static class DaemonLogEvents
{
    /// <summary>A pull request opened (or a follow-up pushed to an existing one) — the moment closeout starts watching.</summary>
    public static readonly EventId PullRequestOpened = new(2001, nameof(PullRequestOpened));

    /// <summary>
    /// A branch pushed with no pull request opened, because the origin is not GitHub — closeout
    /// starts watching nothing, so this is deliberately a distinct id from <see cref="PullRequestOpened"/>
    /// rather than sharing it: a monitor keyed on 2001 expecting a PR URL must not wake for a
    /// push that carries none.
    /// </summary>
    public static readonly EventId BranchPushedWithNoPullRequest = new(2002, nameof(BranchPushedWithNoPullRequest));
}

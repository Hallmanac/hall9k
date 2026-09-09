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

    /// <summary>
    /// A stacked child's pull request opened against the project's own base branch instead of the
    /// parent branch its run recorded, because that parent branch was already gone from origin — its
    /// pull request merged and the parent's closeout deleted it while this child was still building
    /// (task: a stacked pull-request edge exists as an explicit opt-in dependency). Its own id
    /// because it is the one case where the base a pull request opens against is not the base this
    /// run recorded, and the retarget and replay closeout still owes the child are the reason the
    /// record is deliberately left saying so — an operator reading a stack mid-flight wants to see
    /// this without matching prose.
    /// </summary>
    public static readonly EventId StackedParentBranchGoneAtPullRequestOpen =
        new(2003, nameof(StackedParentBranchGoneAtPullRequestOpen));

    /// <summary>
    /// Composed prose the platform was about to post broke a mechanically checkable writing
    /// convention and was rewritten on the way out (task 412afe6c). Its own id because it is the
    /// signal that a composition prompt is not landing: one of these is a session having a bad
    /// day, and a steady stream of them means the conventions are reaching the agent and being
    /// ignored, which is a prompt problem no rewrite fixes.
    /// </summary>
    public static readonly EventId WritingConventionsRewrote = new(2004, nameof(WritingConventionsRewrote));

    /// <summary>
    /// Composed prose was withheld outright, because the convention it broke had no mechanical fix
    /// (task 412afe6c). Distinct from <see cref="WritingConventionsRewrote"/> deliberately: this is
    /// the id that means something an agent wrote is NOT on the pull request, so an operator
    /// looking for prose that never appeared has one line to grep for.
    /// </summary>
    public static readonly EventId WritingConventionsWithheld = new(2005, nameof(WritingConventionsWithheld));
}

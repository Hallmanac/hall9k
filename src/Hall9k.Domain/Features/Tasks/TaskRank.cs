using Hall9k.Domain.Features.Tasks.Projections;

namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// How far along a queued task is toward merging, and so how urgently it should take the next
/// free dispatch slot (Decisions Log #188): a follow-up lap on a task already past
/// its first pull request outranks a retry or hand-back before any pull request, which outranks a
/// plain first claim. An in-process, unpersisted outcome (AGENTS.md: enums only for unpersisted
/// in-process outcomes) — what is persisted is <see cref="TaskListItem.FollowUpBranch"/>,
/// <see cref="TaskListItem.RetryPending"/> and <see cref="TaskListItem.PullRequestUrl"/>, and
/// <see cref="TaskListItem.Rank"/> resolves this the same way on every reader, so
/// <c>Hall9k.Daemon.Dispatch.ProjectRotation</c> and <c>h9k status</c> can never disagree about
/// which rank a row waits under.
/// <para>
/// Declared here, in <c>Hall9k.Domain</c>, rather than beside <c>ProjectRotation</c> in
/// <c>Hall9k.Daemon</c>: the CLI reads it too (the queued section's own ordering and its per-row
/// phrase), and the reference graph runs <c>Cli → Domain</c>, never <c>Cli → Daemon</c>
/// (AGENTS.md's layout section) — a type only the daemon could see would leave the CLI no honest
/// way to read the identical rank the dispatcher just claimed against.
/// </para>
/// <para>
/// Ordered ascending by urgency (the enum's own numeric order is the rank order): a plain
/// <c>OrderBy(candidate => candidate.Rank)</c> already sorts a follow-up lap ahead of a retry or
/// hand-back ahead of a first claim, with no comparer of its own to keep in step with this list.
/// </para>
/// </summary>
public enum TaskRank
{
    /// <summary>
    /// A follow-up lap on a task already past its first pull request: closest to merging, and the
    /// rank the walk that produced this rule found waiting hours behind brand-new first claims.
    /// </summary>
    FollowUpLap,

    /// <summary>A retry or a hand-back, before any pull request exists yet for this task.</summary>
    RetryOrHandback,

    /// <summary>A first claim: no follow-up lap and no retry pending.</summary>
    FirstClaim,
}

/// <summary>
/// The one phrase each <see cref="TaskRank"/> renders as, shared by the daemon's claim log and
/// every CLI surface that names a row's rank — so an operator reading <c>h9k status</c> and the
/// daemon log at once never sees the same rank worded two ways.
/// </summary>
public static class TaskRankDescriptions
{
    public static string Describe(this TaskRank rank) => rank switch
    {
        TaskRank.FollowUpLap => "a follow-up lap on a task past its first pull request",
        TaskRank.RetryOrHandback => "a retry or hand-back before any pull request",
        _ => "a first claim",
    };
}

/// <summary>
/// The one place the three raw fields resolve into a <see cref="TaskRank"/>, so
/// <see cref="TaskListItem.Rank"/> (read off a materialized document) and the daemon's queue read
/// (read off the same three fields selected directly, never the document — <c>DispatchEngine.ReadQueueAsync</c>)
/// can never disagree about a task it both would answer for.
/// <para>
/// <c>retryPending</c> rather than a branch string: <see cref="Events.TaskRetried.Branch"/> is
/// null both when nothing is pending and when a retry was recorded with no run to resume (the
/// failure predated any run record), so a branch alone cannot tell a clean-start retry from a
/// first claim. <see cref="TaskListItem.RetryPending"/> tracks "is a retry or hand-back pending"
/// directly instead (Copilot review, PR #346), the same discriminator
/// <see cref="TaskDetails.RetryPending"/> already used for the identical reason.
/// </para>
/// </summary>
public static class TaskRankResolution
{
    public static TaskRank Resolve(string? followUpBranch, string? pullRequestUrl, bool retryPending) =>
        followUpBranch.IsNotBlank() && pullRequestUrl.IsNotBlank() ? TaskRank.FollowUpLap
        : retryPending ? TaskRank.RetryOrHandback
        : TaskRank.FirstClaim;
}

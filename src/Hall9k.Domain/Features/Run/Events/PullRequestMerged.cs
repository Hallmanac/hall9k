namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The closeout monitor observed the run's pull request merged — the closeout phase is
/// over and RunCompleted follows immediately (Decisions Log #18/#22). MergedAt is
/// GitHub's merge timestamp as reported by gh; null when the provider did not report one
/// (never guessed). ObservedAt is when this node's monitor saw it.
/// <para>
/// <see cref="KeptWorktreePath"/> is set when the merge was observed on a pull request the owner was
/// iterating on by hand (<c>h9k task drive</c> at the PR stage) and the worktree was left in place
/// because a session was still live in it or it held uncommitted work; closeout removes the worktree
/// and branch of every other merged run. Null on every other merge, and on events recorded before
/// the field existed.
/// </para>
/// </summary>
public sealed record PullRequestMerged(
    Guid Id,
    DateTimeOffset? MergedAt,
    DateTimeOffset ObservedAt,
    string? KeptWorktreePath = null);

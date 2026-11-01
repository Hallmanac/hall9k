namespace Hall9k.Domain.Features.Run;

/// <summary>
/// How the task's human gate was answered on a run, which decides what the daemon still checks before the
/// pull request opens. An enum rather than a value object per TASK-MODEL.md §8: the aggregate rebuilds it
/// from the stream and it never leaves a decision inside the review loop, the unpersisted in-process
/// outcome that discipline allows one for.
/// </summary>
public enum HumanGateAnswer
{
    /// <summary>The gate has not been answered, or its answer was withdrawn by a fix or a fresh park.</summary>
    None,

    /// <summary>
    /// <c>h9k review proceed</c>: approves the tip that stood at the park, so the daemon checks the branch
    /// is still there and the worktree holds nothing uncommitted.
    /// </summary>
    Proceeded,

    /// <summary>
    /// <c>h9k review resolve --merge-ready</c> at the gate: owns a tip that moved (the gates run over it and
    /// no review session is dispatched), but still never opens over uncommitted work.
    /// </summary>
    ResolvedMergeReady,

    /// <summary>
    /// <c>h9k task skip-to-pr</c>, the owner's recorded override: the pull request opens as the branch
    /// stands, with no comparison.
    /// </summary>
    Skipped,
}

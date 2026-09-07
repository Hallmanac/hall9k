namespace Hall9k.Domain.Features.Run;

/// <summary>
/// Which levers actually apply at one of interactive mode's four phase boundaries — the selector
/// <see cref="InteractiveBoundaryChoices"/> renders from (task: a human at the wheel takes the fix
/// role herself). An enum rather than a value object per TASK-MODEL.md §8: it is never persisted
/// and never leaves a rendering decision, exactly the unpersisted in-process outcome that
/// discipline allows one for (the same call <see cref="ReviewPhase"/> already makes).
/// </summary>
public enum InteractiveBoundaryLevers
{
    /// <summary>
    /// The two boundaries where the only questions are "go" and "go somewhere else": build done to
    /// review, and fix to re-review. Nothing is asking for work of the human's own here.
    /// </summary>
    ProceedOrRedirect,

    /// <summary>
    /// The review-verdict-to-fix boundary, where four choices apply: send the agent, do the fix by
    /// hand, send the agent with a redirect, or overrule the finding.
    /// </summary>
    ReviewVerdictToFix,

    /// <summary>
    /// The gates-to-pull-request boundary, which additionally has the hands-off exit: the human can
    /// hand the pull request to the daemon and stop being asked (Brian's ruling, 2026-09-07 — an
    /// option, never the default).
    /// </summary>
    GatesToPullRequest,
}

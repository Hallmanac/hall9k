namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The human took the fix role at interactive mode's review-verdict-to-fix boundary
/// (task: a human at the wheel takes the fix role herself) — <c>h9k review fixed</c>, the fourth
/// choice at that park alongside <see cref="ReviewBoundaryApproved"/>'s bare proceed and
/// <see cref="ReviewParkResolved"/>'s two verdicts, none of whose meanings this changes. That fix
/// is already committed on the run's own branch when this is appended (the command refuses over an
/// uncommitted worktree, and refuses an unmoved tip unless <paramref name="NoChangeReason"/> says
/// why), so this event does not start a session of any kind: it re-enters the loop at the existing
/// fix-to-re-review boundary exactly where <see cref="ReviewFixCompleted"/> with
/// <see cref="ReviewFixOutcome.Fixed"/> would have — the gates run over those commits, then a fresh
/// review pass reads them scoped to the same boundary a fix session's own commits would have been
/// read against (<see cref="RunAggregate.CycleHeadSha"/>).
/// <para>
/// No automatic fix budget is spent: <see cref="RunAggregate.ReviewFixRuns"/> — the count of fix
/// sessions the platform itself dispatched — is deliberately untouched, and so is the
/// repeat-findings escalation state a fix round installs
/// (<see cref="RunAggregate.LastFixRoundFindingLocations"/> and its siblings), whose whole
/// comparison is against the most recent AUTOMATED round. What the review cycle this opens costs
/// is identical, though: <see cref="RunAggregate.FixDispatchedThisCycle"/> is set exactly as a
/// dispatched round sets it, so this cycle still owes a fresh-context read before the run may
/// settle and the per-track cycle caps count the cycle it opens the same way.
/// </para>
/// </summary>
/// <param name="Cycle">The review cycle whose findings the human fixed — the cycle the park caught.</param>
/// <param name="HeadSha">
/// The branch tip the fix is on, as observed when the command ran. Null only when git could not be
/// asked at all (the worktree is gone, git is off PATH) — never a guess, and never the reason the
/// unmoved-tip refusal is skipped silently: the command says so out loud when it degrades.
/// </param>
/// <param name="NoChangeReason">
/// Why nothing changed, when the operator passed <c>--no-change "&lt;reason&gt;"</c> and the
/// command did not observe the branch tip move — either it observed the tip unmoved, or it could
/// not compare the tip at all and said so. Null on an ordinary fix, where the commits are the
/// answer, and null too when the command watched the tip move: that combination is refused outright
/// rather than recorded, because the commits and a no-change dismissal are mutually exclusive
/// answers to the same cycle. Carried into every later fresh-context review pass the way a
/// <see cref="ReviewParkResolved"/> reason is (<c>AgentPromptBuilder.AppendSettledRulings</c>).
/// </param>
/// <param name="PushedToRemote">
/// Whether the command actually pushed the branch. True only where a push was needed — the task's
/// pull request is already open, so this run is a closeout-side lap whose remote branch would
/// otherwise be stale against what the reviewers are about to read. False means no push was
/// needed, never that one was attempted and failed: a failed push refuses before this event is
/// ever appended.
/// </param>
public sealed record ReviewHumanFixApplied(
    Guid Id,
    int Cycle,
    string? HeadSha,
    string? NoChangeReason,
    bool PushedToRemote,
    DateTimeOffset AppliedAt,
    Guid AppliedByOwnerId);

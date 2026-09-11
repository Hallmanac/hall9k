namespace Hall9k.Domain.Features.Run;

/// <summary>
/// Where a run stands inside the pre-PR review loop (Decisions Log #23), derived from
/// the run stream so the daemon's ReviewEngine can resume the loop after a restart.
/// In-process only — never persisted (the events are the record; enum per the §8
/// discipline for unpersisted outcomes).
/// </summary>
public enum ReviewPhase
{
    /// <summary>No review dispatched yet: the loop starts with a fresh review session.</summary>
    None,
    /// <summary>A review session is (or was) in flight; its verdict has not been recorded.</summary>
    AwaitingVerdict,
    /// <summary>The last review verdict was NeedsFixes; a fix run (or a park) is next.</summary>
    FixNeeded,
    /// <summary>The last review ended without a parseable verdict; one same-session re-prompt (or a park) is next.</summary>
    VerdictMissing,
    /// <summary>A fix session is (or was) in flight; its outcome has not been recorded.</summary>
    AwaitingFix,
    /// <summary>The last fix session finished; verification gates re-run, then a fresh review.</summary>
    Reverify,
    /// <summary>
    /// Every review track has concluded and nothing is left to fix (Decisions Log #63): the
    /// loop is over and owes the stream one honest account of how it ended — Clean or Settled,
    /// with the residuals it leaves behind. One <see cref="Events.ReviewSettled"/> away from
    /// MergeReady.
    /// </summary>
    Settling,
    /// <summary>The last review judged the diff merge-ready; PullRequestOpener may proceed.</summary>
    MergeReady,
    /// <summary>The fix run disputed a finding as not-a-defect or human-territory; a park is next.</summary>
    Disputed,
    /// <summary>The run is parked for a human; the loop is over until someone intervenes.</summary>
    Parked,
    /// <summary>
    /// A narrow, non-agentic rebase onto the base branch conflicted immediately before the
    /// mandatory final full pass (task: a run rebases its branch onto the current base branch),
    /// and a recovery session is (or was) in flight resolving it with judgment — the
    /// rebase-onto-main skill, dispatched inside this same run rather than through a task
    /// reopen. Its outcome has not been recorded.
    /// </summary>
    AwaitingRebaseRecovery,
    /// <summary>
    /// The recovery session judged the conflict genuinely undecidable — both sides changed the
    /// same behavior, not just the same lines; a park is next. Kept distinct from
    /// <see cref="Disputed"/> (rather than reusing it) so the park message names the rebase
    /// conflict rather than the ordinary review-finding-disputed text <see cref="Disputed"/>'s
    /// own park reason is written for.
    /// </summary>
    RebaseRecoveryDisputed,
    /// <summary>
    /// A human granted guidance on a disputed pre-final-pass rebase conflict
    /// (h9k review resolve --needs-fixes); a fresh recovery session carrying that guidance is
    /// next.
    /// </summary>
    RebaseRecoveryNeeded,

    /// <summary>
    /// The Settling phase's own mandatory gate failed on a tree whose most recent recorded
    /// <see cref="Events.RunRebasedOntoBase"/> was a real rebase (task: a pre-final-pass rebase
    /// that applies cleanly but breaks the mandatory gate gets a repair lap inside the same run
    /// instead of failing it), and a narrow repair session — dispatched inside this same run,
    /// carrying the gate's own output — is (or was) in flight. Its outcome has not been recorded.
    /// </summary>
    AwaitingSettlingGateRepair,

    /// <summary>
    /// The Settling-gate repair round cap is spent: the gate has failed this many times in a row
    /// since the rebase, and a repair session could never make it pass. Kept distinct from
    /// <see cref="RebaseRecoveryDisputed"/> and its own cap-park (neither applies here — nothing
    /// about this park is a git conflict) so a human's resolve routes through this feature's own
    /// rules rather than either of those.
    /// </summary>
    SettlingGateRepairCapReached,

    /// <summary>
    /// A human's needs-fixes resolve on a spent Settling-gate repair cap bought exactly one more
    /// repair round, carrying their guidance; a fresh repair session is next. Unlike
    /// <see cref="RebaseRecoveryNeeded"/>'s own resolve, reaching this phase deliberately does not
    /// reset the round counter — the bought round is exempted from the cap check itself instead
    /// (task's own criteria, not the rebase-recovery cap's inherited behavior).
    /// </summary>
    SettlingGateRepairNeeded,
}

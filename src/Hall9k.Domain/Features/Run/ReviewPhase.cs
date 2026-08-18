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
    /// <summary>The last review judged the diff merge-ready; PullRequestOpener may proceed.</summary>
    MergeReady,
    /// <summary>The fix run disputed a finding as not-a-defect or human-territory; a park is next.</summary>
    Disputed,
    /// <summary>The run is parked for a human; the loop is over until someone intervenes.</summary>
    Parked,
}

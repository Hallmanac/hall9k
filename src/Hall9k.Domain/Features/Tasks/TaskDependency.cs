using Hall9k.Domain.Features.Run;

namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// One task as a dependency of another (Decisions Log #34). A dependency is met only at
/// <em>true closeout</em> — the closeout monitor observed the merge and appended RunCompleted
/// — so Draft, Published, Queued, Claimed/Running and AwaitingReview dependencies all block
/// by the same rule, and a Done task whose pull request is still open still blocks.
/// </summary>
/// <param name="CurrentRunState">
/// The state of the run the dependency currently hangs on, or null when it has none: what
/// says whether a merge observation can still arrive. Null is the honest answer for "no run
/// to observe", never a stand-in for one (the AGENTS.md never-guess rule).
/// </param>
public sealed record TaskDependency(
    Guid Id,
    string Objective,
    TaskState State,
    bool IsClosedOut,
    RunState? CurrentRunState,
    IReadOnlyList<Guid> BlockedBy)
{
    /// <summary>
    /// This blocker can no longer reach true closeout, so anything waiting behind it waits
    /// forever unless a human is told. There is more than one door out of the closeout
    /// pipeline: the task ended without a closeout (Failed, Abandoned), or it reads Done
    /// while the run that would have carried the merge observation has itself ended without
    /// one — h9k task resolve's attestation exit from Failed (log #27), a pull request closed
    /// unmerged, a killed or superseded run — or it reads Done with no run to observe at all.
    /// Origin incident (2026-08-20): the first cut of this rule enumerated Failed and
    /// Abandoned only, so a resolved dependency stranded its dependents in Blocked silently.
    /// </summary>
    public bool IsDead =>
        State == TaskState.Failed
        || State == TaskState.Abandoned
        || (State == TaskState.Done && !IsClosedOut && !CloseoutCanStillArrive);

    /// <summary>Whether this dependency still holds its dependents back.</summary>
    public bool Blocks => !IsClosedOut;

    /// <summary>
    /// A Done task closes out when the closeout monitor observes its pull request merge, and
    /// that can only happen while the run carrying the pull request is still in the pipeline.
    /// No run, or a run already terminal on some ending other than Completed, means there is
    /// nobody left to observe anything.
    /// </summary>
    private bool CloseoutCanStillArrive => CurrentRunState is { } run && !run.IsTerminal;

    /// <summary>How the dependency reads in an error a human has to act on.</summary>
    public string Describe() => $"{Id.ToString("N")[^8..]} \"{Objective}\" ({State.Value})";

    /// <summary>
    /// Why this blocker will never close out and what moves it, as the first half of the
    /// sentence a human reads on the parked dependent. Only meaningful when
    /// <see cref="IsDead"/> — the caller has already decided that.
    /// </summary>
    public string DescribeDeath() =>
        // Failed still has exits (retry, resolve); Abandoned is a dead end by design, so
        // its only honest remedy is on the dependent's side — never advise a lever the
        // decider will refuse (review finding, 2026-08-20). Each branch carries its own
        // remediation so the caller's trailing mechanics compose grammatically.
        State == TaskState.Failed
            ? $"Dependency {Describe()} ended there and will never close out on its own. "
              + "Retry or resolve it, or revise this task's dependencies"
            : State == TaskState.Abandoned
                ? $"Dependency {Describe()} was abandoned and will never close out on its own; "
                  + "abandoned is a dead end by design, so revise this task's dependencies"
                : $"Dependency {Describe()} reads Done, but {DescribeAbsentMerge()}, so the merge observation "
                  + "true closeout waits on will never arrive. Land its work and let the closeout monitor see "
                  + "the merge, or revise this task's dependencies";

    private string DescribeAbsentMerge() =>
        CurrentRunState is null
            ? "it has no run left to carry a pull request"
            : $"its run ended {CurrentRunState.Value} rather than Completed";
}

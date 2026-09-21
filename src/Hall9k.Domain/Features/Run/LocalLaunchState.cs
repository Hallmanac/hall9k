using Hall9k.Domain.Features.Project;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// A local launch as this run's own stream last left it (idea b9b09779, piece 5) — the thing
/// <c>h9k task run-local</c> reads to decide whether it is starting a launch, resuming one, or
/// refusing because one is already up, and the thing the daemon's own sweep reads to decide
/// whether to tear one down.
/// <para>
/// The plan is carried here verbatim rather than re-parsed from the project's run skill on each
/// call: a launch follows the plan it started with, so a skill rewritten (or re-discovered by the
/// ledger sweep) while a reviewer is paused at step 2 cannot renumber the step underneath them.
/// </para>
/// </summary>
/// <param name="LaunchId">This launch's own id, so a stop or a resume names the launch it means rather than "whatever is current".</param>
/// <param name="TaskId">The task the reviewer named — recorded here so a launch read off a run stream still says which card it belongs to.</param>
/// <param name="NodeId">The node whose machine these processes are on — a pid means nothing without it, and the teardown sweep reads it rather than assuming every launch it can see is its own.</param>
/// <param name="WorktreePath">The checkout every step ran in, as it was at launch time.</param>
/// <param name="Steps">The whole plan, in order.</param>
/// <param name="NextStepNumber">The step a resume starts from. One past the last completed step; one past the end when every step is done.</param>
/// <param name="AwaitingHumanAtStep">The human step this launch stopped at, or null when it is not waiting on anybody.</param>
/// <param name="Processes">What it left running, newest last. Empty before the launch reaches its first launch step.</param>
/// <param name="Walker">
/// The <c>h9k</c> process walking this launch's plan, as last recorded by its start or its resume,
/// or null when no pass ever recorded one. Alive means somebody is mid-walk right now; dead means
/// the pass that opened this record is over, whether it finished or was killed part-way.
/// </param>
/// <param name="Port">The ephemeral port the launch chose, or null when the skill's launch command named none for it to choose (which is an answer, not a gap).</param>
/// <param name="Address">The skill's own address or entry point, with the chosen port applied where that was a fact rather than a guess.</param>
/// <param name="StoppedReason">Why it ended, or null while it is still live.</param>
/// <param name="FailedAtStep">The command step whose failure ended this launch, or null when none did.</param>
/// <param name="FailedReason">What that step reported, quoted rather than summarized.</param>
public sealed record LocalLaunchState(
    Guid LaunchId,
    Guid TaskId,
    Guid NodeId,
    string WorktreePath,
    IReadOnlyList<RunSkillStep> Steps,
    int NextStepNumber,
    int? AwaitingHumanAtStep,
    IReadOnlyList<LocalLaunchProcess> Processes,
    LocalLaunchProcess? Walker,
    int? Port,
    string Address,
    DateTimeOffset StartedAt,
    LocalLaunchStopReason? StoppedReason,
    DateTimeOffset? StoppedAt,
    int? FailedAtStep,
    string? FailedReason)
{
    /// <summary>
    /// Whether this launch is still the live one for its run: nothing has stopped it and no step
    /// failed it. A live launch is not necessarily a running one — a launch paused at a human step
    /// has started nothing yet and is still live, which is exactly why a second
    /// <c>h9k task run-local</c> on the same task is refused rather than quietly starting a
    /// parallel walk of the same plan.
    /// </summary>
    public bool Live => StoppedReason is null && FailedAtStep is null;

    /// <summary>Whether it is waiting on a person right now.</summary>
    public bool AwaitingHuman => Live && AwaitingHumanAtStep is not null;

    /// <summary>Whether every step is walked, so the product should be up.</summary>
    public bool Walked => Live && NextStepNumber > Steps.Count;
}

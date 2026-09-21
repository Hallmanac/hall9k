namespace Hall9k.Domain.Features.Run;

/// <summary>
/// Whether a fresh <c>h9k task run-local</c> may start on a run that already has a launch recorded
/// (idea b9b09779, piece 5), and what to do about the old record either way.
/// </summary>
/// <param name="Refusal">The sentence to refuse with, or null to go ahead.</param>
/// <param name="ClearStaleLaunch">
/// Whether the recorded launch should be closed out first, as a launch whose processes were
/// already gone. True only alongside a null <paramref name="Refusal"/>: there is nothing to refuse
/// over, but the record still says live and leaving it that way would have the next reader think
/// two launches exist.
/// </param>
public sealed record LocalLaunchAdmission(string? Refusal, bool ClearStaleLaunch)
{
    /// <summary>Nothing in the way and nothing to tidy.</summary>
    public static readonly LocalLaunchAdmission Clear = new(null, false);

    /// <summary>
    /// The "another launch of the same task is already up" guard, as a decision rather than a
    /// throw, so the rule is testable without a store and without starting a process.
    /// <para>
    /// A launch waiting on a human counts as up even though it has started nothing: it is
    /// somebody's live walk of this branch, and a second one over the same checkout would be two
    /// launches racing for the same ports and the same files. So does one whose walk is still in
    /// flight — a plan whose first steps are a restore that takes minutes has recorded no
    /// processes yet, and that is a different fact from every process it started being gone
    /// (<see cref="LocalLaunchStopReason.ProcessGone"/> draws the same line). What tells them
    /// apart is the walker: the <c>h9k</c> process the launch recorded as walking its plan, alive
    /// or not, observed the same way as everything else this launch owns.
    /// </para>
    /// <para>
    /// A launch whose processes have all gone and whose walker is over is not up at all, whatever
    /// its record still says, so it is closed out and the new one proceeds — recorded as the
    /// process having been gone, never as though the new launch had stopped the old one.
    /// </para>
    /// </summary>
    /// <param name="isAlive">
    /// Whether one recorded process is still that same process (pid and start time both, Decisions
    /// Log #2). Asked of the launch's own processes and of its walker alike, because they are the
    /// same kind of fact about the same machine.
    /// </param>
    public static LocalLaunchAdmission ForStart(
        LocalLaunchState? launch, Guid taskId, Func<LocalLaunchProcess, bool> isAlive)
    {
        if (launch is not { Live: true })
        {
            return Clear;
        }

        if (launch.AwaitingHumanAtStep is { } waiting)
        {
            return new LocalLaunchAdmission(
                LocalLaunchRefusal.AlreadyLaunched(taskId, $"it is waiting on you at step {waiting}"), false);
        }

        int alive = launch.Processes.Count(isAlive);
        if (alive > 0)
        {
            return new LocalLaunchAdmission(
                LocalLaunchRefusal.AlreadyLaunched(taskId, $"{alive} of its process(es) are still running"),
                false);
        }

        return launch.Walker is { } walker && isAlive(walker)
            ? new LocalLaunchAdmission(
                LocalLaunchRefusal.AlreadyLaunched(
                    taskId, $"it is still walking this project's run skill at step {launch.NextStepNumber}"),
                false)
            : new LocalLaunchAdmission(null, true);
    }
}

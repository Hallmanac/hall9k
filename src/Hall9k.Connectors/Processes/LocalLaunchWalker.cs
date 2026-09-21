using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Run;

namespace Hall9k.Connectors.Processes;

/// <summary>
/// Runs one step's own command to completion. A delegate so a test can walk a whole plan without
/// starting anything, and so the caller can narrate each step as it goes. The step rides along
/// beside the command it carries, rather than the caller matching one back to the other by text:
/// a plan can legitimately repeat a command (a health check run twice), and a match by text would
/// attribute the second run's output to the first step.
/// </summary>
public delegate Task<ShellResult> LocalLaunchStepRunner(
    RunSkillStep step, string workingDirectory, CancellationToken cancellationToken);

/// <summary>
/// Starts one launch step's command detached. A delegate for the same reason as
/// <see cref="LocalLaunchStepRunner"/>. <c>command</c> is what will actually run, which is the
/// step's own command with the ephemeral port substituted where it had somewhere to put one.
/// </summary>
public delegate LaunchedProcess LocalLaunchStepSpawner(
    RunSkillStep step, string command, string workingDirectory, string logFile);

/// <summary>Picks a free port. A delegate so a test gets a fixed one rather than whatever this machine happened to have free.</summary>
public delegate int EphemeralPortSource();

/// <summary>
/// How one pass over a run skill's plan ended (idea b9b09779, piece 5). Exactly one of the three
/// is true, and every one of them can carry processes: a plan whose human step sits after the
/// launch steps pauses with the product already up, and a plan whose last setup step fails after
/// an earlier launch step started something has left that something running.
/// </summary>
/// <param name="PausedAtStep">The human step the walk stopped at, or null when it did not stop at one.</param>
/// <param name="FailedAtStep">The command step that reported failure, or null when none did.</param>
/// <param name="FailureReason">What that step said, quoted.</param>
/// <param name="NextStepNumber">Where the plan stands: the paused or failed step, or one past the end when the walk finished.</param>
/// <param name="Processes">Everything this pass left running, in the order it started them.</param>
/// <param name="Port">The port this launch is on: the one this pass chose, or the one an earlier pass of the same launch had already chosen and handed back in, or null when no launch command anywhere in the plan named somewhere to put one.</param>
public sealed record LocalLaunchWalk(
    int? PausedAtStep,
    int? FailedAtStep,
    string FailureReason,
    int NextStepNumber,
    IReadOnlyList<LocalLaunchProcess> Processes,
    int? Port)
{
    /// <summary>Whether every step is behind it.</summary>
    public bool Completed => PausedAtStep is null && FailedAtStep is null;
}

/// <summary>
/// Walks a run skill's plan on a worktree, in order, stopping at the first step that needs a
/// person and at the first command that fails (idea b9b09779, piece 5).
/// <para>
/// Every side effect it has is one of its three delegates, which is deliberate: this is the piece
/// whose behaviour the acceptance criteria are actually about (stop at the human step, resume
/// after it, report the address), and a test of it should not have to start a real server on the
/// machine running the suite to find out whether it does.
/// </para>
/// </summary>
public static class LocalLaunchWalker
{
    /// <summary>
    /// One pass, from <paramref name="fromStepNumber"/> to the end of the plan or the first stop.
    /// <para>
    /// The port is chosen once, lazily, at the first launch command that has somewhere to put one
    /// — not up front — so a plan that never reaches its launch steps (a human step early, a
    /// prerequisite check that fails) never claims a port it did not use, and a walk resumed by
    /// <c>--continue</c> that does reach them chooses one then.
    /// </para>
    /// </summary>
    /// <param name="chosenPort">
    /// The port an earlier pass of this same launch already chose, or null when none has been. A
    /// launch is one walk however many passes it takes, so the port it settled on before a human
    /// step has to survive the pause: a resume that picked a fresh one would start the worker on a
    /// different number from the API in front of it, and the address printed would be the later
    /// one. Null on a fresh launch, which is the only pass that may choose.
    /// </param>
    public static async Task<LocalLaunchWalk> WalkAsync(
        RunSkillPlan plan,
        int fromStepNumber,
        int? chosenPort,
        string worktreePath,
        string logFile,
        LocalLaunchStepRunner run,
        LocalLaunchStepSpawner spawn,
        EphemeralPortSource freePort,
        CancellationToken cancellationToken)
    {
        List<LocalLaunchProcess> started = [];
        int? port = chosenPort;

        foreach (RunSkillStep step in plan.Steps.Where(step => step.Number >= fromStepNumber))
        {
            if (step.Kind != RunSkillStepKind.Command)
            {
                return new LocalLaunchWalk(step.Number, null, string.Empty, step.Number, started, port);
            }

            if (IsLaunchStep(step))
            {
                // Every launch step gets the same port once one is chosen, so a skill that starts
                // an API and a worker against it does not hand them two different numbers — across
                // a pause and a --continue too, which is why the caller hands the chosen one back
                // in rather than this pass starting over.
                int candidate = port ?? freePort();
                string? substituted = RunSkillLaunchPort.Substitute(step.Command, candidate);
                if (substituted is not null)
                {
                    port = candidate;
                }

                string command = substituted ?? step.Command;
                LaunchedProcess process = spawn(step, command, worktreePath, logFile);
                started.Add(new LocalLaunchProcess(process.ProcessId, process.StartedAt, command));
                continue;
            }

            ShellResult result = await run(step, worktreePath, cancellationToken);
            if (!result.Succeeded)
            {
                return new LocalLaunchWalk(
                    null, step.Number, Quoted(result), step.Number, started, port);
            }
        }

        return new LocalLaunchWalk(null, null, string.Empty, plan.Steps.Count + 1, started, port);
    }

    private static bool IsLaunchStep(RunSkillStep step) =>
        step.Section.Equals(RunSkillDocument.LaunchHeading, StringComparison.OrdinalIgnoreCase);

    private static string Quoted(ShellResult result) =>
        result.Output.IsNotBlank()
            ? $"exit code {result.ExitCode}: {result.Output}"
            : $"exit code {result.ExitCode}, with nothing written to either stream.";
}

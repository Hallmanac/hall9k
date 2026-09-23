using System.ComponentModel;
using System.Diagnostics;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Infrastructure.Storage;
using Spectre.Console;

namespace Hall9k.Cli.Installation;

/// <summary>One planned invocation of the freshly installed <c>h9k</c>, as data a test can assert
/// without spawning anything: what it is for, in the operator's language, and the argument list
/// that does it.</summary>
public sealed record RestartStep(string Purpose, IReadOnlyList<string> Arguments)
{
    /// <summary>How the step is named on the terminal and in a failure's remaining-work list —
    /// always the command an operator could paste, never a prose paraphrase of it.</summary>
    public string CommandLine => $"h9k {string.Join(' ', Arguments)}";
}

/// <summary>
/// What one <see cref="RestartStep"/> did. <see cref="CouldNotLaunch"/> non-null means no child
/// process ever existed — the distinction the hand-off's whole failure story turns on, since a
/// first step that never launched has not signalled the daemon to stop and a first step that ran
/// and failed may well have.
/// </summary>
public sealed record RestartStepResult(int ExitCode, string? CouldNotLaunch)
{
    public static RestartStepResult Exited(int exitCode) => new(exitCode, null);

    public static RestartStepResult NotLaunched(string problem) => new(ExitCodes.Error, problem);
}

/// <summary>Runs one planned step and reports what happened. The real one spawns the binary;
/// a test passes a fake so the planned sequence and the stop-at-first-failure behaviour can be
/// asserted with no migration, no daemon and no process at all.</summary>
public delegate Task<RestartStepResult> RestartChildRunner(
    string binary, IReadOnlyList<string> arguments, CancellationToken cancellationToken);

/// <summary>
/// The <c>--restart</c> path's second half, after <c>h9k install</c>/<c>h9k update</c> has already
/// swapped the new binaries into <see cref="DaemonRuntime.BinDirectory"/>: the old process launches
/// the newly installed <c>h9k</c> as a child, once per planned step, and relays its exit code.
/// <para>
/// It has to work this way. The process running <c>h9k update --restart</c> is the OLD binary, and
/// it keeps executing its own code after the swap — but the swap deliberately leaves the new files
/// at the old absolute paths, and the runtime resolves each assembly lazily on first touch, so the
/// old process's first load of anything it had not already touched comes from the NEW release.
/// Opening a Marten store there is exactly that kind of first touch, and it does not fail as a
/// schema problem: it fails as a load-time one, outside every <c>catch</c> the store-opening code
/// has (origin incident, Windows node hall9k-4a, 2026-09-23 07:59 EDT, v0.10.42 to v0.10.43 —
/// <c>System.TypeLoadException: Method 'TeardownExistingProjectionStateAsync' in type
/// 'Marten.DocumentStore' from assembly 'Marten, Version=8.17.0.0' does not have an
/// implementation</c>, thrown at <c>LiveGateGuard.FindOnThisNodeAsync</c> and reaching
/// <c>Program.Main</c> uncaught; the update ended in a stack trace with the OLD daemon still
/// running). So everything from the stop onward runs in the new binary, in a new process, where the
/// assemblies on disk are the ones it started under.
/// </para>
/// <para>
/// Three steps rather than one: <c>h9k daemon start</c> alone would not do it. Its own
/// <see cref="Hall9k.Cli.Diagnostics.DatabaseDoctor"/> call prompts on a terminal even with
/// <c>staleSchemaRepairedByCaller</c>, and the daemon's own <c>EventStoreSchemaGuard</c> prints
/// nothing on success — so an operator watching a schema-changing update would see either a
/// question they did not ask for or no evidence at all that the store was brought current.
/// <c>h9k doctor --yes</c> in between answers both: it repairs Marten's objects with the same two
/// lines the daemon's guard makes, and it says which of the two happened (<c>Schema updated</c>, or
/// <c>Postgres is healthy</c>). Wolverine's own envelope tables stay the daemon's start-time
/// migration through <c>IntegrateWithWolverine</c>, as docs/operations.md already describes.
/// </para>
/// </summary>
public static class DaemonRestartHandoff
{
    private static readonly string CliFileName = OperatingSystem.IsWindows() ? "h9k.exe" : "h9k";

    /// <summary>Where the just-swapped <c>h9k</c> lives — the same absolute path this process was
    /// itself launched from on an installed machine, now holding the new release's bytes.</summary>
    public static string InstalledCliPath => Path.Combine(DaemonRuntime.BinDirectory, CliFileName);

    /// <summary>
    /// The child sequence, in order, stopping at the first failure. <c>doctor --yes</c> sits
    /// between the stop and the start deliberately: the store has to be repaired while nothing is
    /// holding it, and the daemon has to come up on a schema that is already current rather than
    /// racing its own boot-time guard against the CLI calls that follow the restart.
    /// </summary>
    public static IReadOnlyList<RestartStep> PlanSteps() =>
    [
        new RestartStep("stop the daemon still running on the previous binaries", ["daemon", "stop"]),
        new RestartStep("bring the store schema current", ["doctor", "--yes"]),
        new RestartStep("start the daemon on the new binaries", ["daemon", "start"]),
    ];

    /// <summary>
    /// Runs <see cref="PlanSteps"/> through <paramref name="runChild"/>, stopping at the first
    /// step that fails, and returns that step's own exit code (never zero for a failure).
    /// <para>
    /// The point of no return is the stop signal the first child sends. A first step that never
    /// launched is before it: the old daemon is still running on the previous binaries and this
    /// method says so. Anything from there on is after it: the failure names the step, and lists
    /// only the steps that were never started, so an operator finishing by hand is not told to
    /// re-run work that already succeeded.
    /// </para>
    /// </summary>
    public static async Task<int> RunAsync(
        string binary,
        DaemonProcessDescriptor runningBefore,
        RestartChildRunner runChild,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<RestartStep> steps = PlanSteps();
        for (int index = 0; index < steps.Count; index++)
        {
            RestartStep step = steps[index];
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Restart step {index + 1} of {steps.Count} in the new binary: {step.CommandLine} ({step.Purpose}).[/]");

            RestartStepResult result = await runChild(binary, step.Arguments, cancellationToken);
            if (index == 0 && result.CouldNotLaunch is { } launchProblem)
            {
                await Console.Error.WriteLineAsync(DescribeUnlaunchableHandoff(binary, runningBefore, launchProblem, steps));
                return ExitCodes.Error;
            }

            if (result.CouldNotLaunch is not null || result.ExitCode != ExitCodes.Ok)
            {
                await Console.Error.WriteLineAsync(DescribeFailedStep(steps, index, result));
                return result.ExitCode == ExitCodes.Ok ? ExitCodes.Error : result.ExitCode;
            }
        }

        return ExitCodes.Ok;
    }

    internal static string DescribeUnlaunchableHandoff(
        string binary, DaemonProcessDescriptor runningBefore, string problem, IReadOnlyList<RestartStep> steps) =>
        $"Could not launch the newly installed h9k at {binary} to restart the daemon: {problem}"
        + Environment.NewLine
        + $"Nothing has been stopped — h9kd (pid {runningBefore.ProcessId}) is still running on the previous "
        + "binaries, and the new ones are in place for its next start."
        + Environment.NewLine
        + $"Finish the restart by hand: {DescribeSteps(steps)}.";

    internal static string DescribeFailedStep(IReadOnlyList<RestartStep> steps, int failedIndex, RestartStepResult result)
    {
        RestartStep failed = steps[failedIndex];
        string what = result.CouldNotLaunch is { } problem
            ? $"could not be launched: {problem}"
            : $"exited {result.ExitCode}";
        IReadOnlyList<RestartStep> remaining = [.. steps.Skip(failedIndex + 1)];
        string next = remaining.Count == 0
            ? "No step after it was planned — check h9k daemon status."
            : $"Still to do by hand, once that is dealt with: {DescribeSteps(remaining)}.";

        return $"The restart failed at step {failedIndex + 1} of {steps.Count}, {failed.CommandLine} "
            + $"({failed.Purpose}): it {what}."
            + Environment.NewLine
            + next;
    }

    private static string DescribeSteps(IReadOnlyList<RestartStep> steps) =>
        string.Join(", then ", steps.Select(step => step.CommandLine));

    /// <summary>
    /// The real <see cref="RestartChildRunner"/>: spawns the installed <c>h9k</c> with this
    /// process's own streams inherited, so the child's own reporting — the doctor's
    /// <c>Schema updated</c> line above all — reaches the operator's terminal as it happens rather
    /// than being captured and replayed. Never throws for a child that could not start; a launch
    /// failure is an outcome this hand-off reports differently from a nonzero exit, not an
    /// exception for the caller to unpick.
    /// </summary>
    public static async Task<RestartStepResult> RunInstalledCliAsync(
        string binary, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (!File.Exists(binary))
        {
            return RestartStepResult.NotLaunched("the swap placed no h9k there");
        }

        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = binary,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return RestartStepResult.NotLaunched(exception.Message);
        }

        await process.WaitForExitAsync(cancellationToken);
        return RestartStepResult.Exited(process.ExitCode);
    }
}

using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Marten;
using Marten.Linq.MatchesSql;
using Spectre.Console;

namespace Hall9k.Cli.DaemonControl;

/// <summary>
/// One run's verification gate, still recorded as running, whose process this machine's own
/// process table confirms is genuinely alive — never a stale <see cref="RunDetails.ActiveGate"/>
/// left behind by a gate that already ended without clearing it (task: a daemon restart that
/// adopts a run mid-gate ends that gate's orphaned tree instead of racing it — this is the CLI
/// side of the same incident: an ungated <c>h9k daemon stop</c> or restart is what orphans the
/// tree in the first place).
/// </summary>
public sealed record LiveGate(Guid RunId, Guid TaskId, string GateName, int ProcessId);

/// <summary>
/// Shared by <c>h9k daemon stop</c> (warns and proceeds — a human's own call to make) and the
/// <c>h9k update --restart</c> / <c>h9k install --restart</c> shared restart path (waits, since a
/// restart is a machine deciding on its own to end whatever it finds running). Both read the same
/// fact — a live gate process this node's own daemon would otherwise orphan by stopping — from
/// <see cref="RunDetails.ActiveGate"/> across every non-terminal run on this physical node, Working
/// and Delivered alike: a gate runs during either, a first verification pass before the pull
/// request exists or a later review-cycle reverify after it, and <see cref="RunDetails.ActiveGate"/>
/// itself carries only what the run's own stream last recorded, with nothing distinguishing the two.
/// </summary>
public static class LiveGateGuard
{
    // Mirrors DaemonOptions.VerifyGateTimeout's own default (30 minutes) — this project cannot
    // reference Hall9k.Daemon (Reference graph: Cli -> Domain + Connectors), the same reason
    // TaskVerifyCommand.GateTimeout duplicates it instead of reading it.
    public static readonly TimeSpan VerifyGateLimit = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    // Filtered server-side, the same reason RunSupervisor.AdoptOrphansAsync and
    // ResumeStrandedPipelinesAsync do (independent pre-PR review, cycle 1, both lenses): a
    // --restart wait re-issues this query every five seconds for up to VerifyGateLimit, and a
    // node with a long run history would otherwise load and deserialize every run ever recorded
    // for it, on every poll, only to discard all but the handful still live with a gate.
    private static readonly string[] TerminalRunStates =
    [
        RunState.Completed.Value, RunState.Failed.Value, RunState.Killed.Value, RunState.Superseded.Value,
    ];

    /// <summary>
    /// Every live gate this node's own store currently records, or null when the store itself
    /// could not be reached — never guessed at as "none found" (AGENTS.md's never-guess rule):
    /// a caller that cannot tell "checked, found nothing" apart from "could not check at all"
    /// would otherwise report a clean bill of health for a check that never actually ran.
    /// </summary>
    public static async Task<IReadOnlyList<LiveGate>?> FindOnThisNodeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using DocumentStore store = CliStore.Open();
            await using IDocumentSession session = store.LightweightSession();
            BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);

            Guid nodeId = context.NodeId;
            IReadOnlyList<RunDetails> candidates = await session.Query<RunDetails>()
                .Where(run => run.NodeId == nodeId)
                .Where(run => run.MatchesSql(
                    "d.data ->> 'state' not in (?, ?, ?, ?) and d.data -> 'activeGate' is not null",
                    TerminalRunStates[0], TerminalRunStates[1], TerminalRunStates[2], TerminalRunStates[3]))
                .ToListAsync(cancellationToken);

            return [.. candidates
                .Where(run => DaemonProcess.IsAlive(run.ActiveGate!.ProcessId, run.ActiveGate.StartedAt))
                .Select(run => new LiveGate(run.Id, run.TaskId, run.ActiveGate!.GateName, run.ActiveGate.ProcessId))];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Could not check for a live verification gate on this node ({exception.Message}) — the check was skipped.[/]");
            return null;
        }
    }

    /// <summary>
    /// <c>h9k daemon stop</c>'s own half: warns by name of every live gate this stop would orphan
    /// and proceeds regardless — a human explicitly asking to stop has already made the call this
    /// method cannot make for them.
    /// </summary>
    public static async Task WarnAboutLiveGatesAsync(
        Func<CancellationToken, Task<IReadOnlyList<LiveGate>?>> findLiveGates, CancellationToken cancellationToken)
    {
        IReadOnlyList<LiveGate>? live = await findLiveGates(cancellationToken);
        if (live is null)
        {
            return;
        }

        foreach (LiveGate gate in live)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Run {gate.RunId} (task {gate.TaskId})'s '{gate.GateName}' verification gate (pid {gate.ProcessId}) is still running on this node — stopping now leaves it orphaned. The next h9k daemon start ends it and re-runs the gate from the start.[/]");
        }
    }

    /// <summary>
    /// The <c>--restart</c> shared path's own half: waits for every live gate this node's store
    /// records to end on its own, re-querying on every poll so a gate that ends mid-wait while a
    /// different run's own gate starts inside the same window is still caught, up to
    /// <paramref name="deadline"/> total. Returns true once nothing is left running (immediately,
    /// without ever polling, when the very first check already finds nothing — including when the
    /// check itself could not be performed at all, since there is nothing left to wait on in either
    /// case), false when the deadline passed with something still running.
    /// <para>
    /// Announces, once, the moment the wait actually finds something to wait on (independent
    /// pre-PR review, cycle 1, both lenses): without it, this whole wait — up to
    /// <paramref name="deadline"/>, thirty minutes by <see cref="VerifyGateLimit"/> — prints
    /// nothing at all, and an operator or an agent-run invocation watching the command has no way
    /// to tell a deliberate wait from a hung CLI, or that <c>--now</c> exists to skip it.
    /// </para>
    /// </summary>
    public static async Task<bool> WaitForClearAsync(
        Func<CancellationToken, Task<IReadOnlyList<LiveGate>?>> findLiveGates,
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadlineAt = DateTimeOffset.UtcNow + deadline;
        bool announced = false;
        while (true)
        {
            IReadOnlyList<LiveGate>? live = await findLiveGates(cancellationToken);
            if (live is null || live.Count == 0)
            {
                return true;
            }

            if (!announced)
            {
                AnnounceWait(live, deadline);
                announced = true;
            }

            if (DateTimeOffset.UtcNow >= deadlineAt)
            {
                return false;
            }

            await delay(PollInterval, cancellationToken);
        }
    }

    private static void AnnounceWait(IReadOnlyList<LiveGate> live, TimeSpan deadline)
    {
        foreach (LiveGate gate in live)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Waiting for run {gate.RunId} (task {gate.TaskId})'s '{gate.GateName}' verification gate (pid {gate.ProcessId}) to finish before restarting.[/]");
        }

        AnsiConsole.MarkupLineInterpolated(
            $"[dim]Waiting up to {deadline.TotalMinutes:0} minutes — pass --now to restart immediately instead.[/]");
    }

    /// <summary>
    /// <see cref="WaitForClearAsync"/>, gated by the <c>--now</c> override (Brian, 2026-09-20:
    /// wait, flag as override) — <paramref name="now"/> true skips the wait (and every call to
    /// <paramref name="findLiveGates"/> or <paramref name="delay"/>) entirely, restarting at once
    /// regardless of what is still running. What <c>h9k update --restart</c> and
    /// <c>h9k install --restart</c> both call in their own shared restart path, before the stop.
    /// </summary>
    public static async Task WaitUnlessNowAsync(
        bool now,
        Func<CancellationToken, Task<IReadOnlyList<LiveGate>?>> findLiveGates,
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        if (now)
        {
            return;
        }

        bool cleared = await WaitForClearAsync(findLiveGates, delay, deadline, cancellationToken);
        if (!cleared)
        {
            AnsiConsole.MarkupLine(
                "[yellow]A verification gate on this node is still running after waiting the full "
                + "verify-gate limit — restarting anyway.[/]");
        }
    }
}

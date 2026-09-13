using System.ComponentModel;
using Hall9k.Cli.DaemonControl;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Stop a run's live agent session without ending the task it belongs to (task: a run can be
/// killed without killing its task) — the run-level companion to <see cref="TaskAbandonCommand"/>,
/// which is the task-level walk-away and deliberately does not touch a live headless process
/// (its own doc names why). This command is the one that does: it ends the OS process tree the
/// run's own <see cref="ActiveSession"/> records, then records <see cref="RunKilled"/> with
/// <see cref="KillReason.HumanRequested"/> — never <see cref="RunFailed"/>, so the record is
/// honest about why the run stopped. The task itself is left exactly where every other run
/// failure leaves it (<see cref="TaskDecider.Fail"/>, the same pairing
/// <c>RunSupervisor</c> makes everywhere a run fails): Failed, with retry, resolve, and abandon
/// all open — never abandoned, never silently requeued.
/// </summary>
public sealed class RunKillCommand : Hall9kAsyncCommand<RunKillCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id or run id (full, or an unambiguous fragment) — a task id kills its current run")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--reason <REASON>")]
        [Description(
            "Why this run is being killed mid-session; recorded on the task's own TaskFailed "
            + "(h9k task show) and left as an honest default when omitted, never inferred (Decisions Log #27)")]
        public string? Reason { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (DaemonProcess.Probe() is null)
        {
            throw new DomainConflictException(
                "h9kd is not running — a stopped daemon supervises nothing, so there is nobody to confirm this "
                + "kill against and nothing left running an in-flight gate or review pass over it. A detached "
                + "agent process from before the stop keeps running unsupervised until the next h9k daemon "
                + "start adopts it — start the daemon, then retry h9k run kill.");
        }

        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        (Guid taskId, Guid runId) = await RunIdResolver.ResolveTaskOrRunAsync(session, settings.Id, cancellationToken);

        StreamState? fence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        RunDetails run = await session.LoadAsync<RunDetails>(runId, cancellationToken)
            ?? throw new DomainNotFoundException($"No run {runId}.");

        // Captured now, before the process tree is actually torn down: killing an entire process
        // tree and NodeBootstrap.EnsureAsync's own queries below both take real wall-clock time, and
        // the daemon's own monitors (RunSupervisor, ReviewEngine, PrReviewEngine) poll on a 1-second
        // cadence and can drive this exact run forward — a completion, a fresh review dispatch — in
        // that gap. Appending RunKilled below against this version means a daemon write that lands
        // first is detected as a conflict instead of being silently stacked on top of (adversarial
        // review, cycle 1).
        StreamState? runFence = await session.Events.FetchStreamStateAsync(runId, cancellationToken)
            ?? throw new DomainNotFoundException($"No run {runId}.");

        if (run.ActiveSessions.Count == 0)
        {
            throw new DomainConflictException(
                $"Run {runId} has no live agent session to kill — it is {run.State.Value}. "
                + $"h9k task show {taskId} to see where it actually stands.");
        }

        NodeDetails? node = await session.LoadAsync<NodeDetails>(
            ResolvePhysicalNodeId(run.NodeId, run.DispatchingNodeId), cancellationToken);
        bool runOnThisMachine = node?.MachineName == Environment.MachineName;
        (IReadOnlyList<ActiveSession> killable, IReadOnlyList<ActiveSession> unreachable) =
            PartitionSessions(run.ActiveSessions, runOnThisMachine, Environment.MachineName);

        if (killable.Count == 0)
        {
            // Every recorded session is unreachable from here (another machine, or no start time
            // to verify by) — nothing was actually terminated, so recording Killed anyway would
            // be exactly the guess AGENTS.md's never-guess rule forbids: a live process might
            // still be running right where it was, unsupervised, with this run's own record now
            // falsely claiming otherwise.
            string names = string.Join(", ", unreachable.Select(DescribeSession));
            throw new DomainConflictException(
                $"Run {runId}'s active session(s) — {names} — are not verifiable from this machine, so "
                + "nothing was terminated and nothing is recorded as killed. Run this from the machine that "
                + "actually holds the process, or confirm by hand and use h9k task abandon if the task itself "
                + "should end instead.");
        }

        List<ActiveSession> terminated = [];
        List<ActiveSession> alreadyEnded = [];
        foreach (ActiveSession activeSession in killable)
        {
            // StartedAt is guaranteed non-null for everything PartitionSessions placed in
            // killable — see that method's own doc for why an unverifiable identity is never
            // killed blindly (the pid-reuse guard, Decisions Log #2). Pattern-matched rather
            // than asserted with `!` so a violation of that guarantee falls into alreadyEnded
            // instead of throwing.
            if (activeSession.StartedAt is { } startedAt && DaemonProcess.Terminate(activeSession.ProcessId, startedAt))
            {
                terminated.Add(activeSession);
            }
            else
            {
                alreadyEnded.Add(activeSession);
            }
        }

        if (terminated.Count == 0)
        {
            // Every killable session had already ended on its own by the time this reached it
            // (Terminate's own false, discarded before this fix) — recording RunKilled anyway
            // would be exactly the guess the killable.Count == 0 branch above already refuses:
            // nobody performed this kill, so nothing is recorded as killed.
            string endedNames = string.Join(", ", alreadyEnded.Select(DescribeSession));
            throw new DomainConflictException(
                $"Run {runId}'s active session(s) — {endedNames} — had already ended on their own by the time "
                + $"this reached them, so nothing was terminated and nothing is recorded as killed. "
                + $"h9k task show {taskId} to see where it actually stands.");
        }

        DateTimeOffset killedAt = DateTimeOffset.UtcNow;
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        session.Events.Append(
            runId, expectedVersion: runFence.Version + 1,
            new RunKilled(runId, KillReason.HumanRequested, context.OwnerId, killedAt));

        // Only when this run is still the task's own current attempt — a stale run id named
        // explicitly (an earlier retry's own run, say) is killed on its own stream without
        // reaching for a task that has long since moved on.
        if (task.State == TaskState.Claimed && task.CurrentRunId == runId)
        {
            session.Events.Append(
                taskId, expectedVersion: fence.Version + 1,
                TaskDecider.Fail(task, runId, BuildFailureReason(settings.Reason), killedAt));

            // Every other TaskFailed emitter deletes the task's own lease in the same
            // transaction (RunSupervisor.AppendFencedTaskFailureAsync, ReviewEngine.FailAsync,
            // PrReviewEngine.FailAsync) — this command's own TaskFailed must too, since the
            // daemon's own FailRunAsync, which would otherwise have deleted it, now returns
            // early over an already-terminal run (the guard this branch's own kill deliberately
            // set up).
            session.Delete<TaskLease>(taskId);
        }

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            // Either stream can be the one that moved: the daemon can drive the run forward (a
            // completion, a fresh review dispatch) in the gap this command's own process-tree
            // kill and NodeBootstrap.EnsureAsync spend before this append, and the task stream
            // races the same way any other fenced append here does. The process this command
            // terminated is already dead either way — h9k status says where the run and task
            // actually landed.
            throw new DomainConflictException(
                $"Run {runId} or task {taskId} changed while killing run {runId} — the daemon (or another "
                + "command) got there first, so this kill was not recorded. The process was already "
                + $"terminated regardless. Check h9k status and try again.");
        }

        await Doorbell.RingAsync($"run-killed:{runId}", cancellationToken);

        if (unreachable.Count > 0)
        {
            string names = string.Join(", ", unreachable.Select(DescribeSession));
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Could not verify or terminate: {names} — not on this machine, or recorded with no start time to check. Recorded as killed regardless; confirm by hand if one lingers.[/]");
        }

        if (alreadyEnded.Count > 0)
        {
            string names = string.Join(", ", alreadyEnded.Select(DescribeSession));
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Already ended on their own before this reached them: {names}.[/]");
        }

        AnsiConsole.MarkupLineInterpolated(
            $"[dim]Run {runId} killed — task {taskId} stays open: h9k task retry, resolve, or abandon it next.[/]");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Which of a run's active sessions this machine can safely terminate: on this machine (its
    /// own <see cref="ActiveSession.MachineName"/> when it carries one, the run's own node
    /// otherwise — mirrors <c>TaskStatusComposer.SessionOnThisMachine</c>) and carrying a
    /// recorded start time, the other half of the pid-reuse-safe identity every kill here checks
    /// (Decisions Log #2). A session missing either is left alone rather than guessed at — never
    /// killed on a bare pid, and never reached for on a machine this process cannot see the
    /// process table of.
    /// </summary>
    internal static (IReadOnlyList<ActiveSession> Killable, IReadOnlyList<ActiveSession> Unreachable) PartitionSessions(
        IReadOnlyList<ActiveSession> sessions, bool runOnThisMachine, string thisMachine)
    {
        List<ActiveSession> killable = [];
        List<ActiveSession> unreachable = [];
        foreach (ActiveSession session in sessions)
        {
            bool onThisMachine = session.MachineName.IsNotBlank()
                ? session.MachineName == thisMachine
                : runOnThisMachine;
            if (onThisMachine && session.StartedAt is not null)
            {
                killable.Add(session);
            }
            else
            {
                unreachable.Add(session);
            }
        }

        return (killable, unreachable);
    }

    /// <summary>
    /// Which node's <see cref="NodeDetails"/> actually names the machine a run's own daemon-dispatched
    /// sessions live on. <paramref name="nodeId"/> carries only the ceiling-exempt
    /// <see cref="Guid.Empty"/> sentinel for a daemon-dispatched sentinel run (<c>h9k task start</c>'s
    /// deliberate claim, auto-pr-review's "now" speed) — the same gap
    /// <c>RunSupervisor.SentinelPrReviewCandidatesAsync</c> closes for adoption, since the sentinel
    /// itself names no physical daemon. <paramref name="dispatchingNodeId"/> names it in that case
    /// and equals <paramref name="nodeId"/> for every ordinary (non-sentinel) dispatch (<see
    /// cref="Hall9k.Domain.Features.Run.Events.RunDispatched.DispatchingNodeId"/>'s own doc), so
    /// preferring it only when <paramref name="nodeId"/> is the sentinel never changes the ordinary
    /// answer.
    /// </summary>
    internal static Guid ResolvePhysicalNodeId(Guid nodeId, Guid dispatchingNodeId) =>
        nodeId == Guid.Empty ? dispatchingNodeId : nodeId;

    internal static string BuildFailureReason(string? reason) =>
        reason.IsNotBlank() ? $"Killed by h9k run kill: {reason}" : "Killed by h9k run kill.";

    private static string DescribeSession(ActiveSession session) =>
        session.Name.IsNotBlank() ? session.Name : $"pid {session.ProcessId}";
}

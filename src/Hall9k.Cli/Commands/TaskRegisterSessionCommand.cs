using System.ComponentModel;
using System.Diagnostics;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The self-registration observation gate the starting prompt tells a pasted-in Claude Code
/// session to call as its first act (R4, idea fcaded0b's design rulings, Take the Wheel epic
/// 9272e514's slice 7): h9k task work no longer launches an operator's interactive session
/// itself — it prints a prompt to paste into a session the operator starts on their own — so
/// there is no <c>Process.Start()</c> for <c>TaskWorkCommand</c>'s own <c>onStarted</c> callback
/// to read a pid off of. The session has to tell the platform who it is instead, through the
/// same trust model an agent-facing command already extends elsewhere
/// (<see cref="TaskLogInteractionCommand"/>'s own doc has the precedent: structured fields land
/// on the stream rather than transcript prose). This appends the exact same
/// <see cref="InteractiveSessionStarted"/> event <c>TaskWorkCommand</c>'s launch-time recording
/// did, so every downstream reader — <see cref="InteractiveSessionLiveness"/>'s double-booking
/// guard, <c>h9k task show</c>'s Sessions block, the interactive-claim staleness nudge — sees an
/// identical shape regardless of which path produced it.
/// <para>
/// The process identity comes from <see cref="InteractiveSessionLiveness.ClaudeCodePidEnvironmentVariable"/>
/// (<c>CLAUDE_PID</c>) — Claude Code's own environment variable, not this platform's, verified
/// empirically against a live Claude Code CLI session (2026-09-03): it is the actual OS process
/// id of the running <c>claude</c> process, inherited by every Bash-tool child it spawns, which is
/// exactly the identity <see cref="InteractiveSessionLiveness.EnsureNotAttachedElsewhere"/> needs
/// to answer "is this claim's session still attached" from another terminal. This command cannot
/// mint that identity itself the way a direct launch does (there is no <c>Process.Start()</c>
/// here to read a pid off of) and cannot verify it against anything external either — it is
/// taken at the calling session's word, the same honest limit <see cref="TaskLogInteractionCommand"/>'s
/// own doc states for its own claims.
/// </para>
/// <para>
/// When <c>CLAUDE_PID</c> is absent — an older Claude Code version, or a process that is not
/// actually a Claude Code CLI session — this refuses outright rather than recording a session
/// nothing can ever check. A fabricated or absent process id would let
/// <see cref="InteractiveSessionLiveness.EnsureNotAttachedElsewhere"/> either wrongly block a
/// genuine second session (if it collided with a live pid by coincidence) or silently stop
/// protecting anything (a sentinel that can never match), and AGENTS.md's "never guess at
/// unobserved facts" says the honest answer is not to record at all. That is exactly the
/// degradation a session that never calls this command at all already gets — no
/// <see cref="RunDetails.ActiveSessions"/> entry, every guard's existing no-op path for an absent
/// record — so refusing here does not introduce a second, weaker kind of unprotected claim.
/// </para>
/// </summary>
public sealed class TaskRegisterSessionCommand : Hall9kAsyncCommand<TaskRegisterSessionCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        TaskDetails task = await session.LoadAsync<TaskDetails>(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        if (task.State != TaskState.Claimed || !task.IsInteractiveClaim || task.CurrentRunId is not { } runId)
        {
            throw new DomainConflictException(
                $"Task {taskId} is {task.State.Value} — only a task with an active interactive claim "
                + "(h9k task work) registers a session against it.");
        }

        RunDetails run = await session.LoadAsync<RunDetails>(runId, cancellationToken)
            ?? throw new DomainConflictException(
                $"Task {taskId} is claimed interactively but run {runId} has no record — the process likely died "
                + $"while preparing the worktree. h9k task release {taskId} to give the claim back to the "
                + "dispatch queue.");

        // Mirrors TaskVerifyCommand's own guard: once h9k task deliver or handback hands the run
        // to the standard pipeline, the task can still read Claimed+interactive for the whole
        // review loop, but the worktree now belongs to the daemon's own gates and review sessions —
        // registering a fresh interactive session against it would reset RunDetails.State back to
        // Running underneath whichever pipeline stage now owns it.
        if (run.State != RunState.Dispatched && run.State != RunState.Running)
        {
            throw new DomainConflictException(
                $"Task {taskId}'s run {runId} is already {run.State.Value} — it was handed off with "
                + $"h9k task deliver (or handback) and is now in the standard pipeline. h9k task show {taskId} "
                + "to see where it stands.");
        }

        (int processId, DateTimeOffset startedAt) = ReadClaudeProcess(taskId);
        Guid claudeSessionId = ReadClaudeSessionId();
        string sessionName = SessionRoleName.For(DomainId.Short(taskId), SessionRoleName.InteractiveClaim);

        session.Events.Append(runId, new InteractiveSessionStarted(
            runId, claudeSessionId, startedAt, processId, Environment.MachineName, sessionName));
        await session.SaveChangesAsync(cancellationToken);

        // Plain MarkupLine, not the Interpolated overload: taskId and processId are a Guid and an
        // int, neither of which can carry a stray '[' that would need escaping, and the message is
        // built with ordinary string concatenation across lines, which an interpolated-string
        // literal cannot be split across while still binding to the FormattableString overload.
        AnsiConsole.MarkupLine(
            $"[green]Registered.[/] Task {taskId}'s run now shows a live interactive session (pid {processId}) — "
            + "the double-booking and liveness guards (re-entry, verify, deliver, handback, release) key off it "
            + "from here.");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Reads this session's own process identity out of the environment — the honest-refusal half
    /// of this command's doc comment. Pulled out of <see cref="ExecuteAsync"/> so the refusal path
    /// is testable without a store, the same shape <see cref="TaskLogInteractionCommand.Validate"/>
    /// already is for its own local-only checks.
    /// </summary>
    internal static (int ProcessId, DateTimeOffset StartedAt) ReadClaudeProcess(Guid taskId)
    {
        string? claudePid = Environment.GetEnvironmentVariable(InteractiveSessionLiveness.ClaudeCodePidEnvironmentVariable);
        if (claudePid.IsBlank() || !int.TryParse(claudePid, out int processId))
        {
            throw new DomainConflictException(
                $"Could not determine this session's own process id — {InteractiveSessionLiveness.ClaudeCodePidEnvironmentVariable} "
                + "is not set in this environment. h9k task register-session has to run from inside a Claude "
                + "Code CLI session's own Bash tool, where Claude Code sets it; if this is one and it is still "
                + "missing, the Claude Code version may predate it. "
                + $"h9k task work {taskId} --direct-launch remains available for one release if this environment "
                + "cannot support the prompt-handoff model.");
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            return (processId, InteractiveSessionLiveness.ReadStartedAt(process));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            throw new DomainConflictException(
                $"{InteractiveSessionLiveness.ClaudeCodePidEnvironmentVariable} names process {processId}, but it "
                + $"could not be found on this machine's process table ({exception.Message}) — nothing to register.");
        }
    }

    /// <summary>
    /// Best-effort only, unlike <see cref="ReadClaudeProcess"/>: nothing downstream keys liveness
    /// off this value (<see cref="RunDetails.InteractiveClaudeSessionId"/> is audit/display only —
    /// <see cref="InteractiveSessionLiveness"/> never reads it), so a missing or unparsable
    /// <c>CLAUDE_CODE_SESSION_ID</c> degrades to a freshly minted id rather than refusing
    /// registration over a fact no guard is actually built on.
    /// </summary>
    internal static Guid ReadClaudeSessionId()
    {
        string? sessionId = Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID");
        return sessionId.IsNotBlank() && Guid.TryParse(sessionId, out Guid parsed) ? parsed : DomainId.New();
    }
}

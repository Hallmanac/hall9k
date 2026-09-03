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

        [CommandOption("--force")]
        [Description("Register even though the claim's interactive session was recorded on another machine this one cannot check — attests you confirmed by hand that it has exited")]
        public bool Force { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        TaskDetails task = await session.LoadAsync<TaskDetails>(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        (Guid runId, int processId) = await RegisterAsync(session, task, settings.Force, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        // Plain MarkupLine, not the Interpolated overload: taskId and processId are a Guid and an
        // int, neither of which can carry a stray '[' that would need escaping, and the message is
        // built with ordinary string concatenation across lines, which an interpolated-string
        // literal cannot be split across while still binding to the FormattableString overload.
        AnsiConsole.MarkupLine(
            $"[green]Registered.[/] Task {taskId}'s run {runId} now shows a live interactive session (pid {processId}) — "
            + "the double-booking and liveness guards (re-entry, verify, deliver, handback, release) key off it "
            + "from here.");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// The store round trip behind this command's guards and its append — split out of
    /// <see cref="ExecuteAsync"/> exactly as <c>TaskLogInteractionCommand.AppendInteractionAsync</c>
    /// is, so it is testable against a real store without going through <see cref="CliStore.Open"/>
    /// or task-id fragment resolution. Does not save; the caller does, exactly like
    /// <c>AppendInteractionAsync</c>'s own contract.
    /// </summary>
    internal static async Task<(Guid RunId, int ProcessId)> RegisterAsync(
        IDocumentSession session, TaskDetails task, bool force, CancellationToken cancellationToken)
    {
        if (task.State != TaskState.Claimed || !task.IsInteractiveClaim || task.CurrentRunId is not { } runId)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is {task.State.Value} — only a task with an active interactive claim "
                + "(h9k task work) registers a session against it.");
        }

        RunDetails run = await session.LoadAsync<RunDetails>(runId, cancellationToken)
            ?? throw new DomainConflictException(
                $"Task {task.Id} is claimed interactively but run {runId} has no record — the process likely died "
                + $"while preparing the worktree. h9k task release {task.Id} to give the claim back to the "
                + "dispatch queue.");

        // Mirrors TaskVerifyCommand's own guard: once h9k task deliver or handback hands the run
        // to the standard pipeline, the task can still read Claimed+interactive for the whole
        // review loop, but the worktree now belongs to the daemon's own gates and review sessions —
        // registering a fresh interactive session against it would reset RunDetails.State back to
        // Running underneath whichever pipeline stage now owns it.
        if (run.State != RunState.Dispatched && run.State != RunState.Running)
        {
            throw new DomainConflictException(
                $"Task {task.Id}'s run {runId} is already {run.State.Value} — it was handed off with "
                + $"h9k task deliver (or handback) and is now in the standard pipeline. h9k task show {task.Id} "
                + "to see where it stands.");
        }

        // The second, unguarded door into the exact overwrite TaskWorkCommand.ReenterAsync's own
        // check exists to prevent (adversarial + conformance review, cycle 1): without this, a
        // prompt pasted twice — into a second terminal, deliberately or by re-pasting stale
        // scrollback — registers a second session here with no check at all, and
        // RunDetailsProjection.StartSession's single-slot ActiveSessions record silently
        // overwrites the first session's liveness record with the second's, making the first
        // invisible to verify/deliver/handback/release exactly as ReenterAsync's own comment
        // warns. Skipped when this invocation is the same session re-registering (a retry, or a
        // resumed prompt) — IsSelfInvocation's own CLAUDE_PID-plus-start-time match recognises
        // that case — and a no-op when the prior session already exited (IsAlive reads Gone), the
        // same honest degradation this command's own doc comment already promises for a task
        // nobody ever recorded a session against.
        if (!InteractiveSessionLiveness.IsSelfInvocation(run))
        {
            InteractiveSessionLiveness.EnsureNotAttachedElsewhere(run, task.Id, "register a session", force);
        }

        (int processId, DateTimeOffset startedAt) = ReadClaudeProcess(task.Id);
        Guid claudeSessionId = ReadClaudeSessionId();
        string sessionName = SessionRoleName.For(DomainId.Short(task.Id), SessionRoleName.InteractiveClaim);

        session.Events.Append(runId, new InteractiveSessionStarted(
            runId, claudeSessionId, startedAt, processId, Environment.MachineName, sessionName));
        return (runId, processId);
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

        DateTimeOffset startedAt;
        try
        {
            using Process process = Process.GetProcessById(processId);
            startedAt = InteractiveSessionLiveness.ReadStartedAt(process);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            throw new DomainConflictException(
                $"{InteractiveSessionLiveness.ClaudeCodePidEnvironmentVariable} names process {processId}, but it "
                + $"could not be found on this machine's process table ({exception.Message}) — nothing to register.");
        }

        // InteractiveSessionLiveness.ReadStartedAt itself swallows the identical exceptions the
        // catch above guards against and returns DateTimeOffset.MinValue rather than rethrowing
        // (its own doc comment: never guess at an unobserved fact), so the catch above never
        // actually sees them for a pid the process table can find but whose start time it cannot
        // read (a root-owned or elevated process this user cannot query, most commonly). Recording
        // that sentinel here would be exactly the "sentinel that can never match" this command's
        // own doc comment says it refuses to record — no liveness check would ever match it, and
        // RunDetails.Apply(InteractiveSessionStarted) would stamp LastInteractiveActivityAt at
        // MinValue, which AttentionComposer's stale-claim arm reads as centuries of silence
        // (independent pre-PR review, adversarial lens, cycle 1).
        if (startedAt == DateTimeOffset.MinValue)
        {
            throw new DomainConflictException(
                $"{InteractiveSessionLiveness.ClaudeCodePidEnvironmentVariable} names process {processId}, but its "
                + "start time could not be read on this machine (often a root-owned or elevated process a "
                + "regular user cannot query) — nothing to register.");
        }

        return (processId, startedAt);
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

using System.Collections.ObjectModel;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// SessionArtifactName is null for the run's main agent session (files named per log #2:
/// stream.jsonl, prompt.md, stderr.log). Review and fix sessions (log #24) pass a
/// per-session name so their files live beside the main session's without colliding.
/// ResumeSessionId, when set, re-enters that existing session (claude -p --resume, the
/// log #5 pattern) instead of starting a fresh one — SessionId then only names this
/// leg's artifacts.
/// Model is required and has no default: every caller resolves the chain (Decisions Log
/// #33) and states the answer, so no spawn can quietly fall back to the human's personal
/// Claude Code setting. On a resumed session it is the model that session already runs
/// on, carried so the milestone can record it and never re-applied to the process.
/// RunDirectory is the run's own directory (backlog 49) — resolved once at dispatch and
/// carried here rather than rederived, exactly like WorktreePath.
/// UntrustedWorkingDirectory is true only for a pr-review task's spawn (RunLauncher's
/// pr-review branch, PrReviewEngine.DispatchConformanceAsync): WorktreePath is then a
/// checkout of another contributor's pull-request head, which the child process treats as
/// its own project configuration the same way it would this platform's own worktrees
/// (adversarial review, cycle 1) — a `.claude/settings.json` with a hook, an `.mcp.json`
/// naming a server, or a `CLAUDE.md`/`AGENTS.md` read as authoritative doctrine, in that
/// checkout would otherwise run or load under the owner's credentials the moment the run
/// spawns, before the prompt's own read-only instructions are ever read.
/// <see cref="ClaudeExecutor"/> reads this to keep the child from loading any of them.
/// </summary>
public sealed record AgentSpawnRequest(
    Guid RunId,
    Guid SessionId,
    string WorktreePath,
    string RunDirectory,
    string Prompt,
    ExecutorMode Mode,
    AgentModel Model,
    bool SkipPermissions,
    string? SessionArtifactName = null,
    Guid? ResumeSessionId = null,
    bool UntrustedWorkingDirectory = false)
{
    /// <summary>
    /// Environment variables layered onto the owner's environment for this session only.
    /// Empty by default: a spawn inherits the owner's shell and states a variable here only
    /// when this particular session needs something the owner's environment does not say.
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        ReadOnlyDictionary<string, string>.Empty;
}

public sealed record SpawnedAgent(int ProcessId, DateTimeOffset StartedAt);

/// <summary>
/// The executor seam (PLAN.md §6.3): spawn an agent working a prepared worktree, output
/// captured to the run's stream file. v0 implements Claude Code headless only.
/// </summary>
public interface IExecutor
{
    Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken);
}

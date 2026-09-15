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
/// MaxTurns, when set, passes <c>claude -p</c>'s own <c>--max-turns</c> bound (task: when a
/// session ends with finished work uncommitted, the daemon recovers on its own) — a hard,
/// mechanically-enforced cap for a session that must stay cheap and narrow BY CONSTRUCTION, not
/// by a prompt's own promise to be quick. Null for every ordinary dispatch, which keeps the
/// platform's usual unbounded-turns session exactly as it always was; the one caller that sets
/// it (the uncommitted-files pre-gate recovery) is the one session on this whole seam whose job
/// is narrow enough that a small, fixed turn count is actually the right shape for it.
/// <para>
/// GuardsReviewThreadReplies installs the in-thread reply guard in this session's settings file
/// (<see cref="Hall9k.Connectors.Prompts.ClaudeSettingsFile.ReviewThreadReplyGuardHook"/>, task:
/// a review-feedback follow-up never answers a human reviewer in the owner's name on its own):
/// the shell routes that write inside somebody's review thread are refused, so the only way this
/// session reaches one is <c>h9k pr reply</c>, which knows whose thread it is. Set by every
/// spawn site whose session works a FOLLOW-UP run's worktree, which is the only shape that holds
/// an open pull request's branch for the whole of its life: the launcher's own dispatch, the
/// resumer behind an error retry, and every session the review loop and the gates put into that
/// same run afterwards (<c>ReviewEngine</c>, <c>VerificationRunner</c>'s uncommitted-work
/// recovery). False everywhere else, so a fresh build session's settings file is byte-for-byte
/// what it was.
/// </para>
/// <para>
/// Three spawn paths deliberately never set it, and none of them can reach a follow-up: an
/// interactive claim and a deliberate kick-off both refuse a reopened task outright
/// (<c>TaskWorkCommand</c>, <c>TaskStartCommand</c>), and a pr-review lap writes nothing to the
/// pull request at all, under a deny list of its own
/// (<see cref="Hall9k.Connectors.Prompts.ClaudeSettingsFile.ReviewLapDeniedTools"/>).
/// </para>
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
    bool UntrustedWorkingDirectory = false,
    int? MaxTurns = null,
    bool GuardsReviewThreadReplies = false)
{
    /// <summary>
    /// Environment variables layered onto the owner's environment for this session only.
    /// Empty by default: a spawn inherits the owner's shell and states a variable here only
    /// when this particular session needs something the owner's environment does not say.
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        ReadOnlyDictionary<string, string>.Empty;

    /// <summary>
    /// The name this session's Claude Code process launches under (task: every dispatched agent
    /// session launches under a human-readable id-and-role name) — passed straight through to
    /// <c>claude -n/--name</c>, verified against <c>claude --help</c> and confirmed empirically
    /// to be what <c>~/.claude/sessions/&lt;pid&gt;.json</c> records as <c>name</c> and what
    /// <c>claude agents --json</c> and another session's cross-session mesh
    /// (<c>ListAgents</c>/<c>SendMessage</c>) address a session by. Required — not optional with
    /// a default — because every dispatch site knows its own role
    /// (<see cref="Hall9k.Domain.Features.Run.SessionRoleName"/>) and a session with no name is
    /// exactly the accidental-worktree-name gap this field exists to close.
    /// </summary>
    public required string SessionName { get; init; }
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

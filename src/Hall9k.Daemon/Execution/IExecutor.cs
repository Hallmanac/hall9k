using System.Collections.ObjectModel;
using Hall9k.Domain.Features.Project;
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
/// Effort is required beside it for the same reason: every caller resolves the chain
/// (<c>DaemonOptions.ResolveEffort</c>: task, project, the node's role value, the node-wide value)
/// and states the answer, so no site can forget it and a spawn never reads the node-wide level by
/// default. <see cref="AgentEffort.Unknown"/> means nothing set one, and the executor then leaves the
/// level out of the session's settings so the model's own default decides. It is vendor-neutral here;
/// only the executor maps it to Claude Code's <c>effortLevel</c>.
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
/// The reply guard is not a per-request choice (<see cref="Hall9k.Connectors.Prompts.ClaudeSettingsFile.ReviewThreadReplyGuardHook"/>,
/// task: a dispatched session never speaks to a person at the top level of a pull request on its
/// own): <see cref="ClaudeExecutor"/> installs it in every settings file it writes, because a
/// headless session has no operator watching its shell. The shell routes that put text on a pull
/// request, inside a review thread or at the top level (<c>gh pr comment</c>, <c>gh issue comment</c>,
/// the issue-comment REST endpoint and the GraphQL comment mutations), are refused, so the only way
/// any dispatched session reaches one is <c>h9k pr reply</c>, which knows whose thread or review it
/// is. That covers fresh build sessions and every follow-up kind alike, and the pr-review settings
/// file (<see cref="UsesReviewPermissions"/>) carries the same hook. It used to follow the run
/// being a follow-up, which left a fresh session free to address a person at the top level, and
/// <c>gh pr comment</c> was named as a deliberate hole.
/// </para>
/// <para>
/// Not guarded: the interactive <c>h9k task work</c> session, where the operator is present, and
/// the daemon's own provider writes (the pull-request opener, re-requests, the merge note and
/// closeout comments), which run in the daemon rather than in a session's shell. The hook fails
/// open and reads command text, so it is not a sandbox: a client library that spells no recognized
/// route or host, or a program hidden behind an indirection the text does not show, is outside it.
/// </para>
/// <para>
/// <see cref="UsesReviewPermissions"/> is what <see cref="ClaudeExecutor"/> (security review idea
/// 6be68ee2, process-injection finding 1) dispatches on to build this session's settings file
/// from <see cref="Hall9k.Connectors.Prompts.ClaudeSettingsFile.BuildForPrReview"/> instead of
/// <see cref="Hall9k.Connectors.Prompts.ClaudeSettingsFile.Build"/>: true for every pr-review
/// session, its mention follow-up, and every follow-on persona session — the three real spawn
/// sites are <c>RunLauncher</c>'s own <c>isPrReview</c> branch, its mention follow-up launch, and
/// <c>PrReviewEngine.DispatchFollowOnSessionAsync</c>. Set together with <see cref="SkipPermissions"/>
/// false — a project's own <c>SkipPermissions</c> setting is never consulted for any of the three
/// (Brian's ruling, 2026-09-27: no pr-review session ever runs with permissions skipped, member or
/// not). <see cref="QaGateCommands"/> rides beside it only for a QA persona session.
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
    AgentEffort Effort,
    bool SkipPermissions,
    string? SessionArtifactName = null,
    Guid? ResumeSessionId = null,
    bool UntrustedWorkingDirectory = false,
    int? MaxTurns = null,
    bool UsesReviewPermissions = false,
    IReadOnlyList<VerifyCommand>? QaGateCommands = null)
{
    /// <summary>
    /// Environment variables layered onto the owner's environment for this session only.
    /// Empty by default: a spawn inherits the owner's shell and states a variable here only
    /// when this particular session needs something the owner's environment does not say.
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        ReadOnlyDictionary<string, string>.Empty;

    /// <summary>
    /// The task this session's own worktree belongs to, when it has one — stamped onto
    /// <see cref="Hall9k.Domain.Features.Run.DispatchedRunEnvironment.TaskIdVariable"/> alongside
    /// <see cref="RunId"/> (task: a dispatched session cannot drive the project's own lifecycle), so
    /// <c>DispatchedSessionInterceptor</c>'s own refusal can name the task and not only the run. Null
    /// for the sessions this platform spawns with no owning task — card publication, courier
    /// delivery, and project-scoped run-skill discovery — which carry no task worktree to name.
    /// </summary>
    public Guid? TaskId { get; init; }

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

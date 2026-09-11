namespace Hall9k.Domain.Features.Run;

/// <summary>
/// The human-readable session name every dispatched agent process carries, verified against
/// `claude --help` and confirmed empirically (task: every dispatched agent session launches
/// under a human-readable id-and-role name): the Claude Code CLI's <c>-n, --name &lt;name&gt;</c>
/// flag sets the session's display name, which is exactly what lands in
/// <c>~/.claude/sessions/&lt;pid&gt;.json</c> as <c>name</c>/<c>nameSource: user</c> — the same
/// record <c>claude agents --json</c> reads and the cross-session mesh (another Claude session's
/// <c>ListAgents</c>/<c>SendMessage</c>) addresses a session by. Without an explicit name, that
/// record instead carries a <c>nameSource: derived</c> name scraped from the launch directory
/// (the "accidental worktree-suffix name" the Take the Wheel epic's discovery idea, fcaded0b,
/// found dispatched review-lens sessions already answering to).
/// <para>
/// Shape: <c>&lt;task-shortid&gt;-&lt;role&gt;</c>, composed by <see cref="For"/>. The role
/// vocabulary is fixed and lives here — one place — because the epic's later slices (start-it-
/// mine dispatch, the mid-run interaction rules, the escape-hatch logging invariant) key their
/// own behavior off exactly these strings: <see cref="Build"/>, <see cref="Fix"/>,
/// <see cref="ReviewConformance"/>, <see cref="ReviewAdversarial"/>, <see cref="ReviewVerify"/>,
/// <see cref="Rebase"/>, <see cref="Checks"/>, <see cref="StackReplay"/>, <see cref="CardPublication"/>, and
/// <see cref="InteractiveClaim"/>. A role outside that list (<see cref="Synthesis"/>) still gets
/// a name — every dispatched session does — just not one the interaction rules key on yet.
/// </para>
/// </summary>
public static class SessionRoleName
{
    /// <summary>The ordinary primary session: a fresh dispatch, or a plain follow-up (review feedback) resumed onto its existing branch.</summary>
    public const string Build = "build";

    /// <summary>A follow-up dispatched to resolve a rebase conflict against the base branch (the rebase-onto-main skill).</summary>
    public const string Rebase = "rebase";

    /// <summary>A follow-up dispatched to fix failing pull-request checks.</summary>
    public const string Checks = "checks";

    /// <summary>
    /// A stacked child's mechanical replay onto a moved parent head or onto the project's base
    /// after the parent merged (task: a stacked pull-request edge exists as an explicit opt-in
    /// dependency). Distinct from <see cref="Rebase"/>, which resolves a conflict with judgment:
    /// this one carries no new intent, runs the gates, and never enters a review cycle.
    /// </summary>
    public const string StackReplay = "stack-replay";

    /// <summary>Composes a task up as a card in an external tracker (Decisions Log #102).</summary>
    public const string CardPublication = "card-publication";

    /// <summary>An operator's own attached session (h9k task work) — held by the human, not a spawned agent.</summary>
    public const string InteractiveClaim = "interactive-claim";

    /// <summary>
    /// A human reviewer's own review lap over somebody else's pull request (<c>h9k pr review</c>,
    /// Decisions Log #149) — held by the reviewer, not a spawned agent, the same way
    /// <see cref="InteractiveClaim"/> is. Its own role rather than that one's because a lap never
    /// builds anything: nothing it does reaches a branch, a pull request, or a merge, and a
    /// reader of a session list should be able to tell the two apart at a glance.
    /// </summary>
    public const string ReviewLap = "review-lap";

    /// <summary>Condenses a fan-in of blocker handoffs into one context document (Decisions Log #36). Not part of the epic's named vocabulary; still named.</summary>
    public const string Synthesis = "synthesis";

    /// <summary>
    /// The one bounded, commit-only session an uncommitted-files pre-gate failure may spawn onto
    /// the SAME worktree before the run fails (task: when a session ends with finished work
    /// uncommitted, the daemon recovers on its own). Not part of the epic's named vocabulary;
    /// still named.
    /// </summary>
    public const string CommitRecovery = "commit-recovery";

    /// A narrow recovery session resolving a conflict the pre-final-pass rebase check hit (task:
    /// a run rebases its branch onto the current base branch) — dispatched inside the build run
    /// itself, unlike <see cref="Rebase"/>'s own post-PR follow-up. The one shared prefix both
    /// <c>ReviewEngine.RebaseRecoveryArtifactName</c> (Daemon) and <c>TaskPhaseComposer</c> (Cli)
    /// key off, so the phase line can tell this session apart from an ordinary
    /// <see cref="Fix"/> session sharing the same <c>AgentRole</c> without the two ever drifting
    /// out of sync with each other.
    /// </summary>
    public const string PreFinalPassRebasePrefix = "pre-final-pass-rebase";

    /// <summary>One dispatch of the pre-final-pass rebase-recovery session, distinguished by session id since it can redispatch more than once (an error retry, or a human's needs-fixes guidance).</summary>
    public static string PreFinalPassRebase(string sessionIdShort) => $"{PreFinalPassRebasePrefix}-{sessionIdShort}";

    /// <summary>
    /// A narrow repair session dispatched when the Settling phase's own mandatory gate fails over
    /// a tree whose most recent recorded rebase was real (task: a pre-final-pass rebase that
    /// applies cleanly but breaks the mandatory gate gets a repair lap inside the same run instead
    /// of failing it) — its own prefix, distinct from <see cref="PreFinalPassRebasePrefix"/>, even
    /// though both share the rebase-recovery leg and dispatch path: this session fixes what the
    /// gate reports broken, never a git conflict.
    /// </summary>
    public const string SettlingGateRepairPrefix = "settling-gate-repair";

    /// <summary>One dispatch of the Settling-gate repair session, distinguished by session id since it can redispatch more than once (an error retry, or a human's needs-fixes guidance).</summary>
    public static string SettlingGateRepair(string sessionIdShort) => $"{SettlingGateRepairPrefix}-{sessionIdShort}";

    /// <summary>A fix session applying a cycle's review findings.</summary>
    public static string Fix(int cycle) => $"fix-{cycle}";

    /// <summary>The conformance lens's pass for a given cycle — "does the work meet its objective, its acceptance criteria, and repo doctrine?"</summary>
    public static string ReviewConformance(int cycle) => $"review-conformance-{cycle}";

    /// <summary>The adversarial lens's pass for a given cycle — "where is this code wrong, regardless of what it was asked to do?"</summary>
    public static string ReviewAdversarial(int cycle) => $"review-adversarial-{cycle}";

    /// <summary>The single reviewer a Verify-mode cycle dispatches, standing in for every still-active track (Decisions Log #59).</summary>
    public static string ReviewVerify(int cycle) => $"review-verify-{cycle}";

    /// <summary>
    /// <see cref="ReviewConformance"/>, <see cref="ReviewAdversarial"/>, or <see cref="ReviewVerify"/>,
    /// selected by <paramref name="lens"/> — <see cref="ReviewLens.Unknown"/> (a pass recorded before
    /// lenses existed) reads as conformance, the same precedent <see cref="ReviewLens.Covers"/>
    /// already sets for that lens.
    /// </summary>
    public static string Review(ReviewLens lens, int cycle) =>
        lens == ReviewLens.Adversarial ? ReviewAdversarial(cycle)
        : lens == ReviewLens.Verify ? ReviewVerify(cycle)
        : ReviewConformance(cycle);

    /// <summary>Composes the full <c>&lt;task-shortid&gt;-&lt;role&gt;</c> name from a role string produced by one of this class's own members.</summary>
    public static string For(string taskShortId, string role) => $"{taskShortId}-{role}";
}

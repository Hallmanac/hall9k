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
/// <see cref="Rebase"/>, <see cref="Checks"/>, <see cref="CardPublication"/>, and
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

    /// <summary>Composes a task up as a card in an external tracker (Decisions Log #102).</summary>
    public const string CardPublication = "card-publication";

    /// <summary>An operator's own attached session (h9k task work) — held by the human, not a spawned agent.</summary>
    public const string InteractiveClaim = "interactive-claim";

    /// <summary>Condenses a fan-in of blocker handoffs into one context document (Decisions Log #36). Not part of the epic's named vocabulary; still named.</summary>
    public const string Synthesis = "synthesis";

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

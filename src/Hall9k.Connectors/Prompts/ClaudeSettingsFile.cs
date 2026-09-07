namespace Hall9k.Connectors.Prompts;

/// <summary>
/// The platform-imposed Claude Code settings (PLAN.md §6.6, and the 2026-09-01 timeout
/// finding below): agents never author co-authored-by trailers, and a session's command tool
/// gets timeout headroom sized for this platform's own gates rather than Claude Code's stock
/// defaults. Shared between the daemon's headless spawn
/// (<c>Hall9k.Daemon.Execution.ClaudeExecutor</c>) and an operator's interactive claim
/// (<c>h9k task work</c>) so both settings reach a session identically regardless of who
/// launched it — the CLI cannot reference <c>Hall9k.Daemon</c>, so this is the shared home
/// both sides read the content from, the same pattern <see cref="WorkPromptBuilder"/> already
/// uses for the prompt itself.
/// <para>
/// <b>The command timeout, and why (2026-09-01 finding, 399 fix-session transcripts mined):</b>
/// Claude Code's Bash tool defaults a command's timeout to <c>BASH_DEFAULT_TIMEOUT_MS</c> (stock
/// 120000ms = 2 minutes) and caps any explicit per-command timeout a session requests at
/// <c>BASH_MAX_TIMEOUT_MS</c> (stock 600000ms = 10 minutes, verified empirically against the
/// installed Claude Code 2.1.258 binary rather than assumed from memory — its bundled CLI
/// resolves exactly those two defaults). The stock 2-minute default killed obedient dispatched
/// sessions running this platform's own 8-minute-and-growing foreground <c>dotnet test</c> gate;
/// sessions adapted by detaching the suite into the background and then dying waiting on a
/// result that was never coming. The stock 10-minute cap made foreground compliance impossible
/// outright on the days the (at the time unbounded-parallelism) suite ran longer than that. Both
/// env vars are read by every Claude Code session — there is no dedicated settings.json field for
/// either, so this is the mechanism, set here through the generic <c>env</c> passthrough
/// documented in the settings schema.
/// </para>
/// <para>
/// <b>Sizing (2026-09-02 finding: a compile-time constant went stale the moment an operator
/// raised the live option it claimed to mirror):</b> <see cref="Build"/> takes the command
/// timeout to ship as <c>BASH_DEFAULT_TIMEOUT_MS</c>, so a caller with a live-configured ceiling
/// in reach — <c>ClaudeExecutor</c>, which resolves <c>IOptions&lt;Hall9k.Daemon.DaemonOptions&gt;
/// .Value.VerifyGateTimeout</c> exactly as <c>VerificationRunner</c> already does — hands that
/// value straight through, and a foreground gate run is sized for whatever ceiling the daemon
/// itself is actually enforcing, not a number frozen at build time. <c>BASH_MAX_TIMEOUT_MS</c> is
/// always double whatever default is requested, so a session can still ask for more than the
/// default via its own explicit per-command timeout on a day the suite runs long — the platform
/// sizes the floor; the ceiling stays a session's own call. <see cref="DefaultCommandTimeout"/>
/// (30 minutes, mirroring <c>DaemonOptions.VerifyGateTimeout</c>'s own default) is what a caller
/// with no live option in reach falls back to — today, only <c>h9k task work</c>
/// (<c>TaskWorkCommand</c>, in <c>Hall9k.Cli</c>), which structurally cannot reference
/// <c>Hall9k.Daemon</c> at all (Reference graph: Cli -> Domain + Connectors). That the platform's
/// 30 minutes is written down more than once is a choice about which project owns the number
/// rather than a reference the compiler forbids — see <see cref="DefaultCommandTimeout"/> for
/// which direction is open and why it is not taken. <c>TaskVerifyCommand</c>'s own hardcoded
/// 30-minute gate timeout is a third copy of the same number, pinned to nothing.
/// An operator who raises <c>VerifyGateTimeout</c> past 30 minutes therefore gets the raised
/// ceiling on every headless dispatch. Two CLI surfaces stay unmoved by that setting, because
/// neither can reach <c>DaemonOptions</c>: a foreground gate run inside an interactive
/// <c>h9k task work</c> claim still falls back to the 30-minute <see cref="DefaultCommandTimeout"/>
/// above, and <c>h9k task verify</c>'s own gate timeout (<c>TaskVerifyCommand</c>, a separate
/// hardcoded 30-minute <c>CancelAfter</c> on the gate process itself, not a
/// <see cref="Build"/> caller at all) stays pinned regardless of the option.
/// </para>
/// </summary>
public static class ClaudeSettingsFile
{
    /// <summary>
    /// Mirrors <c>Hall9k.Daemon.DaemonOptions.VerifyGateTimeout</c>'s own default, pinned to it by
    /// <c>ClaudeSettingsFileTests</c>. The duplication is deliberate rather than forced
    /// (2026-09-02 adversarial review, which caught this comment claiming otherwise): this project
    /// cannot reference <c>Hall9k.Daemon</c>, but <c>Hall9k.Daemon</c> does reference this one, so
    /// <c>VerifyGateTimeout</c>'s initializer could name this constant and collapse the two into
    /// one. It deliberately does not, because that direction leaves the daemon's own gate ceiling
    /// defined by a Claude Code settings type: the gate timeout is the number that means
    /// something on its own, and this constant is the mirror of it, not the reverse. A test holds
    /// the mirror true instead. This is the fallback for a caller with no live-configured ceiling
    /// in reach.
    /// </summary>
    public static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Builds the settings-file body a session should launch with, sizing
    /// <c>BASH_DEFAULT_TIMEOUT_MS</c> to <paramref name="commandTimeout"/> and
    /// <c>BASH_MAX_TIMEOUT_MS</c> to double that.
    /// </summary>
    public static string Build(TimeSpan commandTimeout)
    {
        long defaultMilliseconds = (long)commandTimeout.TotalMilliseconds;
        long maxMilliseconds = defaultMilliseconds * 2;
        return $$$"""{"includeCoAuthoredBy": false, "env": {"BASH_DEFAULT_TIMEOUT_MS": "{{{defaultMilliseconds}}}", "BASH_MAX_TIMEOUT_MS": "{{{maxMilliseconds}}}"}}""";
    }

    /// <summary>
    /// A review lap's own settings (<c>h9k pr review</c>, Decisions Log #149): everything
    /// <see cref="Build"/> imposes, plus the push guard. A lap reads somebody else's pull request
    /// in a checkout it does not own, so the one thing a session in it must never be able to do
    /// is write to that pull request or to its remote — and a rule in the prompt is a request,
    /// while a <c>permissions.deny</c> entry is refused by Claude Code's own permission engine
    /// before the tool runs.
    /// <para>
    /// <b>Why deny is the mechanism and a git hook is not.</b> The obvious guard is a
    /// <c>pre-push</c> hook in the review worktree, and it cannot be installed there: git resolves
    /// hooks from the clone's single shared hooks directory, so a hook written for this one
    /// worktree fires for every other worktree of the same clone — including the build sessions
    /// whose pushes the daemon legitimately makes. Making it per-worktree means enabling
    /// <c>extensions.worktreeConfig</c>, which changes how <c>core.bare</c> is read on the bare
    /// clone this platform's worktrees hang off, repository-wide, to serve one temporary checkout.
    /// The session-level deny costs nothing and is scoped to exactly the session it is for.
    /// </para>
    /// <para>
    /// <c>git commit</c> is deliberately NOT denied. The lap's checkout is detached with no local
    /// branch, so a commit there moves nothing, and the reviewer's own end-to-end tests have to be
    /// committable somewhere — denying commit outright would block the one kind of writing a lap
    /// is explicitly for. What is denied is every way the work leaves this machine: the push
    /// itself, the GitHub write surfaces that would let a session post a review, a comment or a
    /// merge the reviewer never ran, and this platform's own two verdict commands, which reach
    /// that same surface under that same login. The verdict travels through <c>h9k pr approve</c>
    /// / <c>h9k pr request-changes</c> run by the reviewer in their own terminal, which is the
    /// only thing that makes it theirs.
    /// </para>
    /// </summary>
    public static string BuildForReviewLap(TimeSpan commandTimeout)
    {
        long defaultMilliseconds = (long)commandTimeout.TotalMilliseconds;
        long maxMilliseconds = defaultMilliseconds * 2;
        string deny = string.Join(", ", ReviewLapDeniedTools.Select(tool => $"\"{tool}\""));
        return $$$"""{"includeCoAuthoredBy": false, "env": {"BASH_DEFAULT_TIMEOUT_MS": "{{{defaultMilliseconds}}}", "BASH_MAX_TIMEOUT_MS": "{{{maxMilliseconds}}}"}, "permissions": {"deny": [{{{deny}}}]}}""";
    }

    /// <summary>
    /// The exact rules <see cref="BuildForReviewLap"/> denies. Public so the lap's own guard file
    /// in the worktree and the guard the run directory's settings file carries are built from one
    /// list rather than two that can drift — and so a test can assert the list itself rather than
    /// a substring of rendered JSON.
    /// <para>
    /// <c>git push</c> is matched on the subcommand rather than on the whole command line, so it
    /// catches every form of it, including one with the remote and refspec spelled out. The
    /// <c>gh</c> rules name the four write verbs that reach a pull request; a read
    /// (<c>gh pr view</c>, <c>gh pr diff</c>) is untouched, because reading the pull request is
    /// most of what a lap does.
    /// </para>
    /// <para>
    /// <c>gh api</c> is denied alongside them, and it is the rule this list was first written
    /// without (independent pre-PR review, cycle 1, both lenses). It is not a fifth write verb —
    /// it is the write surface this list's whole point reaches through:
    /// <c>GitHubPullRequestSurface.PostReviewAsync</c>, this platform's own poster, uses
    /// <c>gh api .../pulls/&lt;n&gt;/reviews</c> precisely because <c>gh pr review</c> takes no
    /// line comments — so a session denied the four verbs could still post a review, or start a
    /// thread with <c>gh api .../issues/&lt;n&gt;/comments</c>, under the reviewer's own login
    /// (AGENTS.md's never-start-a-review-thread rule, origin incident 2026-08-20). Denying the
    /// whole subcommand costs a lap nothing, because every read it makes goes through
    /// <c>gh pr view</c> / <c>gh pr diff</c>.
    /// </para>
    /// <para>
    /// <c>h9k pr approve</c> and <c>h9k pr request-changes</c> are denied for exactly the same
    /// reason as <c>gh api</c>, and they were the hole left when only the <c>gh</c> side was
    /// closed (independent pre-PR review, cycle 1, conformance lens): they are this platform's
    /// OWN poster, they shell out to <c>gh</c> under the reviewer's own login, and they
    /// additionally finalize the task — so a session that ran one would post a verdict the
    /// reviewer never gave AND end their lap, which is strictly worse than the raw <c>gh api</c>
    /// call this list already refuses. <c>PullRequestReviewVerdict.DeliverAsync</c> cannot tell
    /// an agent caller from a human one, and the lap's own briefing prints both commands with the
    /// task id filled in, so the prompt hands a session the exact spelling — which is a request
    /// not to run it, where this is a refusal. It costs a lap nothing: the reviewer runs their
    /// verdict in their own terminal, never through the session.
    /// <br/>
    /// Deliberately NOT extended to the other <c>h9k</c> commands that could end a lap
    /// (<c>h9k review resolve --merge-ready</c>, <c>h9k task release</c>,
    /// <c>h9k task abandon</c>): none of them writes anything to the pull request under the
    /// reviewer's login, which is the authorship invariant this list defends (AGENTS.md's
    /// never-start-a-review-thread rule, origin incident 2026-08-20), and a lap ended early is
    /// recoverable in one command — <c>h9k pr review</c> again, since a Done pr-review task does
    /// not hold its pull request hostage. Those stay the prompt's business.
    /// </para>
    /// <para>
    /// What this list is, stated plainly so nothing downstream describes it as more: a
    /// session-level permission deny, matched by Claude Code's own engine on the command as it is
    /// spelled. It refuses the ordinary way to reach each of these, which is what a session
    /// actually reaches for — and it is not a sandbox, because a command spelled around the
    /// prefix (<c>git -C &lt;path&gt; push</c>) does not match it, and
    /// <c>--dangerously-skip-permissions</c> voids the whole engine (which is why the lap's own
    /// handoff never prints that flag, unlike <c>h9k task work</c>'s, whose project setting can
    /// ask for it). The honest enforcement story stays node-signed authorship in the P2P identity
    /// layer (PLAN.md §16 #38-#58).
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> ReviewLapDeniedTools =
    [
        "Bash(git push:*)",
        "Bash(gh pr review:*)",
        "Bash(gh pr comment:*)",
        "Bash(gh pr merge:*)",
        "Bash(gh pr close:*)",
        "Bash(gh api:*)",
        "Bash(h9k pr approve:*)",
        "Bash(h9k pr request-changes:*)",
    ];
}

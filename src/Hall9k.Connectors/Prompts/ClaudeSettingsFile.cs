using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Shared.ValueObjects;

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
    /// <param name="guardReviewThreadReplies">
    /// Whether to install the in-thread reply guard (task: a review-feedback follow-up never
    /// answers a human reviewer in the owner's name on its own) — see
    /// <see cref="ReviewThreadReplyGuardHook"/>. Set for a follow-up run, which is the only kind
    /// of session that works an open pull request's threads; false everywhere else, so a fresh
    /// build session's settings are byte-for-byte what they were.
    /// </param>
    /// <param name="effort">
    /// The node's configured reasoning effort level, written as <c>effortLevel</c>. A headless
    /// session ignores the owner's user-level <c>effortLevel</c> and honors this file, so this is the
    /// one place the level can be carried. Null, or a value outside <see cref="AgentEffort.All"/>,
    /// leaves the key out, so the file is byte-for-byte what it is without one. The key is preferred
    /// over the <c>CLAUDE_CODE_EFFORT_LEVEL</c> environment variable, which hard-locks the level so a
    /// session cannot lower it.
    /// </param>
    public static string Build(
        TimeSpan commandTimeout, bool guardReviewThreadReplies = false, AgentEffort? effort = null)
    {
        long defaultMilliseconds = (long)commandTimeout.TotalMilliseconds;
        long maxMilliseconds = defaultMilliseconds * 2;
        string hooks = guardReviewThreadReplies ? $", {ReviewThreadReplyGuardHook}" : string.Empty;
        string effortLevel = effort is { IsWellFormed: true } ? $", \"effortLevel\": \"{effort.Value}\"" : string.Empty;
        return $$$"""{"includeCoAuthoredBy": false{{{effortLevel}}}, "env": {"BASH_DEFAULT_TIMEOUT_MS": "{{{defaultMilliseconds}}}", "BASH_MAX_TIMEOUT_MS": "{{{maxMilliseconds}}}"}{{{hooks}}}}""";
    }

    /// <summary>
    /// The command a session's shell must reach an in-thread reply through, once the guard below
    /// is installed. Public so the prompt that teaches it and the guard that enforces it are one
    /// string rather than two that can drift.
    /// </summary>
    public const string ReviewThreadReplyCommand = "h9k pr reply";

    /// <summary>
    /// The <c>PreToolUse</c> hook that makes the reply park enforcement rather than instruction
    /// (task: a review-feedback follow-up never answers a human reviewer in the owner's name on
    /// its own). It refuses the shell routes that put a comment inside somebody's review thread,
    /// so the only way a follow-up reaches one is <see cref="ReviewThreadReplyCommand"/> — which
    /// knows, from closeout's own provider read, whose thread it is, and refuses a decline or a
    /// route into a person's.
    /// <para>
    /// <b>Why a hook and not a <c>permissions.deny</c> entry.</b> The review lap's guard is a
    /// deny list (<see cref="ReviewLapDeniedTools"/>) and could not be one here. A deny matches
    /// the command as it is spelled, and the reply route is routinely spelled with the path in
    /// quotes (<c>gh api "repos/$SLUG/pulls/$N/comments/$ID/replies"</c> — the exact line the
    /// resolve-review-threads skill teaches), which no prefix rule matches. Worse, every
    /// dispatched session this platform spawns may carry
    /// <c>--dangerously-skip-permissions</c>, and that flag voids the permission engine outright
    /// while leaving hooks running. A deny would have been a rule that looked like enforcement
    /// and was not.
    /// </para>
    /// <para>
    /// <b>Every shell the session has, not just Bash.</b> The matcher names both shell tools
    /// Claude Code exposes, because on this platform's own primary host a dispatched session is
    /// handed a <c>PowerShell</c> tool beside its <c>Bash</c> one — and the same
    /// <c>gh api …/replies</c> line runs in either. A Bash-only matcher was therefore a guard with
    /// the ordinary Windows shell left open beside it (independent pre-PR review, cycle 1,
    /// conformance and adversarial lenses). The routes are recognized from the command text
    /// (<see cref="ReviewThreadReplyRoutes"/>), which is shell-agnostic, so one hook body covers
    /// both.
    /// </para>
    /// <para>
    /// <b>What it does not stop, stated plainly.</b> A session can still answer a person at the
    /// top level with <c>gh pr comment</c>, which is deliberately left alone because it is the
    /// only way to answer a review BODY (GitHub makes one unthreadable) and that path is not what
    /// the two origin incidents were. A session that reaches GitHub's API through a client library
    /// of its own, spelling neither a route this recognizes nor a host, is outside what any of
    /// this sees. This refuses the routes a session
    /// actually reaches for, which is the same honest claim <see cref="ReviewLapDeniedTools"/>
    /// makes for its own list; the long-term answer is still node-signed authorship in the P2P
    /// identity layer (PLAN.md §16 #38-#58).
    /// </para>
    /// <para>
    /// The guard fails OPEN: <c>h9k</c> missing from the session's PATH, a crash, or a malformed
    /// answer all leave the tool call to run. A guard that failed closed would block every shell
    /// call in every follow-up the first time it mis-parsed something, which is a worse failure
    /// than the one it prevents.
    /// </para>
    /// </summary>
    public const string ReviewThreadReplyGuardHook =
        "\"hooks\": {\"PreToolUse\": [{\"matcher\": \"" + ReviewThreadReplyGuardMatcher + "\", \"hooks\": "
        + "[{\"type\": \"command\", \"command\": \"h9k pr reply-guard\"}]}]}";

    /// <summary>
    /// The tool names the guard above is attached to — a <c>PreToolUse</c> matcher, which Claude
    /// Code reads as a regex, so the two shells are one alternation. Public so the hook that is
    /// registered and the command that decides (<c>PullRequestReplyGuardCommand.Denies</c>) read
    /// the same list rather than two that can drift: a tool name in the matcher and not in the
    /// command is a hook that fires and always allows.
    /// </summary>
    public const string ReviewThreadReplyGuardMatcher = "Bash|PowerShell";

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
    /// <c>gh pr</c> rules are the complete write half of that subcommand as <c>gh pr --help</c>
    /// itself lists it — <c>create</c>, <c>review</c>, <c>comment</c>, <c>edit</c>, <c>merge</c>,
    /// <c>close</c>, <c>reopen</c>, <c>ready</c>, <c>lock</c>, <c>unlock</c>, <c>revert</c>,
    /// <c>update-branch</c> — and enumerating all of them rather than the obvious few is the
    /// correction this list needed twice over (independent pre-PR review, cycle 1, adversarial
    /// lens). It first named only <c>review</c>, <c>comment</c>, <c>merge</c> and <c>close</c>,
    /// which are the verbs a session reaching for a *review* would use, and left the ones a
    /// session reaching for something helpful would: <c>gh pr update-branch</c> merges the base
    /// into somebody else's branch server-side, <c>gh pr edit --body</c> rewrites their pull
    /// request's description, and both are ordinary first-class verbs rather than the
    /// spelled-around commands the "not a sandbox" paragraph below admits this cannot stop. A
    /// reviewer asking the session to "bring it current with main" is enough to reach the first
    /// one, and the briefing quotes a foreign pull request's body and findings report verbatim,
    /// so the ask need not even be the reviewer's. The reads (<c>gh pr view</c>,
    /// <c>gh pr diff</c>, <c>gh pr list</c>, <c>gh pr status</c>, <c>gh pr checks</c>) are
    /// untouched, because reading the pull request is most of what a lap does;
    /// <c>gh pr checkout</c> is left alone with them, as it writes nothing outside this machine
    /// and the push it would make possible is denied on its own line.
    /// </para>
    /// <para>
    /// The <c>gh issue</c> rules are the same write half of the same resource, reached under the
    /// other subcommand's name, and they were the hole left when only <c>gh pr</c> was closed
    /// (independent pre-PR review, cycle 2, adversarial lens). Issues and pull requests share one
    /// number sequence and one underlying REST resource — <c>GitHubWorkItemProvider</c> carries
    /// the same observation for the other direction, where <c>gh issue view 1 --repo cli/cli</c>
    /// returns a merged pull request (origin observation 2026-08-21) — so
    /// <c>gh issue comment &lt;pull-request-number&gt;</c> posts on the pull request's own
    /// conversation and starts a thread there, which is precisely the write the paragraph above
    /// denies <c>gh api .../issues/&lt;n&gt;/comments</c> for: <c>gh issue comment</c> is the
    /// first-class verb wrapping that exact endpoint. <c>gh issue edit</c>, <c>close</c>,
    /// <c>reopen</c>, <c>lock</c>, <c>unlock</c>, <c>delete</c>, <c>transfer</c>, <c>pin</c>,
    /// <c>unpin</c> and <c>develop</c> sit on the same shared resource (or, for <c>develop</c>,
    /// create a branch on the remote), and <c>gh issue create</c> writes under the reviewer's
    /// login exactly as <c>gh pr create</c> does, so the write half is enumerated whole here for
    /// the same reason it is there. The reads (<c>gh issue view</c>, <c>gh issue list</c>,
    /// <c>gh issue status</c>) are untouched: a lap reads the issues a pull request cites.
    /// <br/>
    /// The boundary this stops at, named so nothing reads the list as wider than it is: the
    /// invariant defended is authorship of writes to the pull request itself (AGENTS.md's
    /// never-start-a-review-thread rule), not every repository write <c>gh</c> can make.
    /// <c>gh release</c>, <c>gh repo edit</c>, <c>gh label</c> and their kin are not denied by
    /// name — they touch nothing on the pull request and start no thread on it, and the REST
    /// route to any of them is already refused by the <c>gh api</c> line.
    /// </para>
    /// <para>
    /// <c>gh api</c> is denied alongside them, and it is the rule this list was first written
    /// without (independent pre-PR review, cycle 1, both lenses). It is not one more write verb —
    /// it is the write surface this list's whole point reaches through:
    /// <c>GitHubPullRequestSurface.PostReviewAsync</c>, this platform's own poster, uses
    /// <c>gh api .../pulls/&lt;n&gt;/reviews</c> precisely because <c>gh pr review</c> takes no
    /// line comments — so a session denied the verbs could still post a review, or start a
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
        "Bash(gh pr create:*)",
        "Bash(gh pr review:*)",
        "Bash(gh pr comment:*)",
        "Bash(gh pr edit:*)",
        "Bash(gh pr merge:*)",
        "Bash(gh pr close:*)",
        "Bash(gh pr reopen:*)",
        "Bash(gh pr ready:*)",
        "Bash(gh pr lock:*)",
        "Bash(gh pr unlock:*)",
        "Bash(gh pr revert:*)",
        "Bash(gh pr update-branch:*)",
        "Bash(gh issue create:*)",
        "Bash(gh issue comment:*)",
        "Bash(gh issue edit:*)",
        "Bash(gh issue close:*)",
        "Bash(gh issue reopen:*)",
        "Bash(gh issue lock:*)",
        "Bash(gh issue unlock:*)",
        "Bash(gh issue delete:*)",
        "Bash(gh issue transfer:*)",
        "Bash(gh issue pin:*)",
        "Bash(gh issue unpin:*)",
        "Bash(gh issue develop:*)",
        "Bash(gh api:*)",
        "Bash(h9k pr approve:*)",
        "Bash(h9k pr request-changes:*)",
    ];

    /// <summary>
    /// Every pr-review session's own settings (security review idea 6be68ee2, process-injection
    /// finding 1): the real permission file every pr-review session, mention follow-up, and
    /// follow-on persona session now launches under, in place of
    /// <c>--dangerously-skip-permissions</c> — Brian's ruling 2026-09-27, no pr-review session
    /// ever runs with permissions skipped, member or not.
    /// <para>
    /// <b>Why <c>defaultMode: dontAsk</c>.</b> The owner's own <c>~/.claude/settings.json</c>
    /// carries <c>defaultMode: acceptEdits</c>, and every pr-review session's own
    /// <c>--setting-sources user</c> (<see cref="Hall9k.Daemon.Execution.ClaudeExecutor"/>'s own
    /// untrusted-checkout isolation) still loads that file — verified empirically (four throwaway
    /// <c>claude -p</c> probes, Claude Code 2.1.283): with no mode stated in this file, a session
    /// ran under the owner's own <c>acceptEdits</c> and a <c>Write</c> call created a file neither
    /// list allowed. <c>dontAsk</c> in this file wins over the owner's own mode, and under it a
    /// headless session cannot be asked, so a tool this file's own lists do not name is refused
    /// outright rather than silently accepted.
    /// </para>
    /// <para>
    /// <b>The allow list</b> is exactly what a pr-review lens actually runs: the diff and its
    /// history (<c>git diff origin/&lt;base&gt;...HEAD</c>, <c>git log</c> —
    /// <c>review-mechanics.md</c>'s own ordinary-diff-range), the pull request itself
    /// (<c>gh pr view</c>, <c>gh pr diff</c>, <c>gh pr checks</c>), the issues it cites
    /// (<c>gh issue view</c> — all four named in the QA rules' own never-post line), and ordinary
    /// reading (<c>Read</c> and <c>Grep</c>, both scoped to the checkout and the run directory so
    /// an injected session cannot read outside them — an unscoped <c>Read</c> can reach
    /// <c>~/.config/gh/hosts.yml</c>, and an unscoped <c>Grep</c> can read that same file's own
    /// content a line at a time — plus <c>Glob</c>, left unscoped, since it returns matching file
    /// names only and never reads content on its own). <c>gh api</c> is deliberately NOT allowed —
    /// the reused deny list below holds <c>Bash(gh api:*)</c> and deny beats allow, verified — so
    /// even a read-only <c>gh api GET</c> is refused; the lenses read through <c>gh pr view</c>/
    /// <c>gh pr diff</c> instead.
    /// </para>
    /// <para>
    /// <b>The deny list</b> is <see cref="ReviewLapDeniedTools"/> — the identical write-surface
    /// refusal a reviewer's own local lap already carries — plus <c>Bash(claude:*)</c>: the
    /// owner's own user settings allow it, and under <c>dontAsk</c> a user-level allow still wins
    /// unless this file denies it too (verified: without this line, <c>claude --version</c> ran
    /// under the flag file alone), which would otherwise let an injected session launch its own
    /// nested <c>claude --dangerously-skip-permissions</c> and step around every rule above it.
    /// </para>
    /// <para>
    /// <b>Driving is refused everywhere by construction.</b> Nothing in this file's allow list
    /// reaches a way to start the product or run its end-to-end tests — <paramref
    /// name="qaGateCommands"/> is the one earned exception, added only for a QA session on a
    /// non-fork head (idea b9b09779; QA's own job is to build and run this project's tests, `qa
    /// checks.md`), and only ever as the project's own recorded gate commands, never a run-skill
    /// or drive command. A QA or designer session told by its prompt that driving is authorised
    /// still has every such attempt refused here; the denial is what lets the allow list grow by
    /// evidence instead of by guess.
    /// </para>
    /// <para>
    /// The reply-guard hook is installed unconditionally, defense in depth alongside the deny
    /// list above, on the same terms <see cref="ReviewThreadReplyGuardHook"/>'s own doc states:
    /// a pr-review session never posts to GitHub at all, but the hook costs nothing to carry and
    /// nothing here should depend on the deny list alone. The git read-only guard beside it
    /// (<see cref="GitReadOnlyGuardMatcher"/>) refuses what the allow list's own prefix rules
    /// cannot, in either direction (<see cref="GitReadOnlyGuardRoutes"/>'s own doc): <c>git
    /// diff</c>/<c>git log</c>'s own <c>--output=&lt;path&gt;</c> flag writes arbitrary content to
    /// any path the owner can write, and either subcommand's own <c>--no-index</c> mode — entered
    /// explicitly or implicitly, by naming an absolute path — reads one instead of the checkout's
    /// own history. Neither is something a prefix-only allow rule (<c>Bash(git diff:*)</c>) has
    /// any way to refuse — a deny list matches a command as spelled, and both are still spelled
    /// "git diff" (independent pre-PR review, cycle 1, both lenses; verified in a throwaway repo:
    /// <c>git log -1 --format='format:...' --output=&lt;path&gt;</c> wrote the formatted text to
    /// that path, and <c>git diff /dev/null ~/.config/gh/hosts.yml</c> printed that file's own
    /// content).
    /// </para>
    /// </summary>
    /// <param name="worktreePath">The checkout this session reads — the one directory, besides the run directory, its own <c>Read</c> may reach.</param>
    /// <param name="runDirectory">This run's own directory, where a session's prompt, settings and findings files live.</param>
    /// <param name="qaGateCommands">
    /// The project's own recorded verify gate commands, allowed verbatim as additional
    /// <c>Bash(&lt;command&gt;:*)</c> rules — set only for a QA persona session, and only on a
    /// non-fork head (a fork head skips the QA persona outright, so this never actually
    /// co-occurs with a fork checkout). Null or empty for every other session.
    /// </param>
    /// <param name="effort">Identical to <see cref="Build"/>'s own parameter of the same name — carried through so a pr-review session is not silently run at Claude Code's default effort.</param>
    public static string BuildForPrReview(
        TimeSpan commandTimeout, string worktreePath, string runDirectory,
        IReadOnlyList<VerifyCommand>? qaGateCommands = null, AgentEffort? effort = null)
    {
        long defaultMilliseconds = (long)commandTimeout.TotalMilliseconds;
        long maxMilliseconds = defaultMilliseconds * 2;
        string effortLevel = effort is { IsWellFormed: true } ? $", \"effortLevel\": \"{effort.Value}\"" : string.Empty;
        List<string> allow =
        [
            AbsolutePathRule("Read", worktreePath),
            AbsolutePathRule("Read", runDirectory),
            AbsolutePathRule("Grep", worktreePath),
            AbsolutePathRule("Grep", runDirectory),
            // The one write this session's own prompt can ask for (RunLauncher's mint addendum,
            // MentionFollowUpPromptBuilder.BuildMintAddendum): a mention-minted primary session
            // drafts its answer to `<runDirectory>/mention-answer.md`, which
            // PrReviewEngine.ComposeReportAndParkAsync reads back into the findings report. With
            // no allow rule for it the write is refused under dontAsk and the whole "You were
            // asked" section silently disappears from every mention-minted review (independent
            // pre-PR review, cycle 1, adversarial lens). Harmless for every other pr-review
            // session, which never asks to write here at all. Built from the POSIX-normalised
            // run directory (see AbsolutePathRule's own doc) so this rule matches on a Windows
            // node too, not only the /tmp paths every test uses (independent pre-PR review,
            // cycle 1, both lenses).
            $"Write(/{EscapeJsonString($"{NormalizeForPermissionRule(runDirectory)}/mention-answer.md")})",
            .. PrReviewAllowedTools,
            .. (qaGateCommands ?? []).Select(gate => $"Bash({EscapeJsonString(gate.Command)}:*)"),
        ];
        string allowJson = string.Join(", ", allow.Select(rule => $"\"{rule}\""));
        string denyJson = string.Join(", ", PrReviewDeniedTools.Select(tool => $"\"{tool}\""));
        return $$$"""{"includeCoAuthoredBy": false{{{effortLevel}}}, "env": {"BASH_DEFAULT_TIMEOUT_MS": "{{{defaultMilliseconds}}}", "BASH_MAX_TIMEOUT_MS": "{{{maxMilliseconds}}}"}, "permissions": {"defaultMode": "dontAsk", "allow": [{{{allowJson}}}], "deny": [{{{denyJson}}}]}, {{{PrReviewHooksJson}}}}""";
    }

    /// <summary>
    /// One <c>Read</c>/<c>Grep</c> rule scoped to an absolute path, in the form Claude Code's own
    /// path-rule resolver actually reads as absolute. A single leading slash (which every absolute
    /// path on this platform already carries) is read as relative to the settings file's own
    /// root, not the filesystem root — the bundled docs' own example is <c>Edit(//etc/*)</c> for
    /// the absolute path <c>/etc/*</c> — so an unscoped rule built with only the path's own leading
    /// slash silently matched nothing and every read inside <paramref name="path"/> fell through
    /// to a refusal (independent pre-PR review, cycle 1, both lenses; reads inside the checkout
    /// only ever worked because the checkout is the process's own working directory, not because
    /// of this rule). The extra slash prepended here is what turns it into the platform's
    /// filesystem-root form.
    /// </summary>
    private static string AbsolutePathRule(string tool, string path) =>
        $"{tool}(/{EscapeJsonString(NormalizeForPermissionRule(path))}/**)";

    /// <summary>
    /// A path rule spliced into this file's hand-built JSON is only ever matched by Claude Code in
    /// forward-slash form (lesson ccc37c9c) — on a Windows node it POSIX-normalises the path it is
    /// actually checking (<c>C:\Users\x</c> becomes <c>/c/Users/x</c>) before comparing it against
    /// a rule, and a rule built from the native backslash path therefore never matches there. This
    /// mirrors that normalisation so a rule built here matches on every node this platform
    /// dispatches to, not only the POSIX ones every existing test exercises (independent pre-PR
    /// review, cycle 1, both lenses; decisions.md lists Windows as a supported daemon host).
    /// A path with no drive letter and no backslash — every POSIX path — passes through unchanged.
    /// </summary>
    private static string NormalizeForPermissionRule(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.Length >= 2 && normalized[1] == ':' && char.IsLetter(normalized[0])
            ? $"/{char.ToLowerInvariant(normalized[0])}{normalized[2..]}"
            : normalized;
    }

    /// <summary>
    /// The tool rules <see cref="BuildForPrReview"/> allows on every pr-review session, beside the
    /// <c>Read</c>/<c>Grep</c> rules it scopes itself (worktree and run directory) and whatever
    /// <paramref name="qaGateCommands"/> a QA session earns on top. What the lenses actually run:
    /// <c>review-mechanics.md</c>'s own ordinary-diff-range for <c>git diff</c>/<c>git log</c>,
    /// and the QA rules' own never-post line for the four <c>gh</c> reads.
    /// <para>
    /// <c>Glob</c> is the one tool left unscoped: it returns matching file NAMES only, never file
    /// contents, so an unscoped rule cannot read anything outside the checkout the way an unscoped
    /// <c>Read</c> or <c>Grep</c> could (both scoped above — a session's own <c>Grep</c> reaches
    /// matching lines of file content, which is exactly what an unscoped rule would have let it
    /// read out of <c>~/.config/gh/hosts.yml</c>, independent pre-PR review, cycle 1, adversarial
    /// lens).
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> PrReviewAllowedTools =
    [
        "Glob",
        "Bash(git diff:*)",
        "Bash(git log:*)",
        "Bash(gh pr view:*)",
        "Bash(gh pr diff:*)",
        "Bash(gh pr checks:*)",
        "Bash(gh issue view:*)",
    ];

    /// <summary>
    /// Every <c>PreToolUse</c> hook a pr-review session's own settings file installs: the reply
    /// guard every follow-up already carries (<see cref="ReviewThreadReplyGuardHook"/>'s own doc),
    /// plus the git read-only guard below — both live under the one <c>hooks</c> key a settings
    /// file may hold, so they are assembled together here rather than as two independent splices
    /// that would collide.
    /// </summary>
    private static string PrReviewHooksJson =>
        "\"hooks\": {\"PreToolUse\": ["
        + $"{{\"matcher\": \"{ReviewThreadReplyGuardMatcher}\", \"hooks\": "
        + $"[{{\"type\": \"command\", \"command\": \"h9k pr reply-guard\"}}]}}, "
        + $"{{\"matcher\": \"{GitReadOnlyGuardMatcher}\", \"hooks\": "
        + $"[{{\"type\": \"command\", \"command\": \"h9k pr review-git-guard\"}}]}}"
        + "]}";

    /// <summary>
    /// The tool names <see cref="PrReviewHooksJson"/> attaches the git read-only guard to — see
    /// <see cref="ReviewThreadReplyGuardMatcher"/>'s own doc for why both shells are named rather
    /// than <c>Bash</c> alone.
    /// </summary>
    public const string GitReadOnlyGuardMatcher = "Bash|PowerShell";

    /// <summary>
    /// <see cref="ReviewLapDeniedTools"/> plus <c>Bash(claude:*)</c> — see
    /// <see cref="BuildForPrReview"/>'s own doc for why the addition is load-bearing rather than
    /// belt-and-suspenders.
    /// </summary>
    public static readonly IReadOnlyList<string> PrReviewDeniedTools =
    [
        .. ReviewLapDeniedTools,
        "Bash(claude:*)",
    ];

    /// <summary>
    /// Minimal JSON string escaping for a value spliced into this file's own hand-built JSON
    /// (a worktree or run-directory path, or a project's own gate command) — never trusted to be
    /// free of the two characters that would otherwise break the surrounding string literal.
    /// </summary>
    private static string EscapeJsonString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}

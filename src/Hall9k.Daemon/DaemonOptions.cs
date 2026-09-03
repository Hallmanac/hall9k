using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Daemon;

public sealed class DaemonOptions
{
    public const string SectionName = "Hall9k";

    /// <summary>
    /// How many agent sessions this node may have resident at once, as configured — retired as
    /// the node's own admission unit by Decisions Log #111, which moved that job to
    /// <see cref="MaxConcurrentTaskRuns"/>. Nothing in the daemon's own admission math reads this
    /// bound property directly any more; the legacy-key conversion and <c>h9k daemon status</c>'s
    /// own naming of it both read the raw environment variable and config file themselves, through
    /// <see cref="OperatingSettingsResolver"/>, so a pre-#111 value stays diagnosable without this
    /// property being in the loop at all. Excluded from Program.cs's own <c>Bind()</c> call for
    /// exactly that reason: an unparseable value for a setting nothing reads would otherwise still
    /// crash startup for free (the property's own setter accessibility does not stop
    /// <c>ConfigurationBinder</c> from attempting — and failing — the conversion; see
    /// <see cref="MaxConcurrentTaskRuns"/>'s own doc).
    /// </summary>
    public int MaxConcurrentAgentSessions { get; set; } = OperatingSettings.DefaultMaxConcurrentAgentSessions;

    /// <summary>
    /// How many task runs may be live on this node at once (Decisions Log #111, superseding the
    /// session-denominated <see cref="MaxConcurrentAgentSessions"/> as the node's own admission
    /// unit): the thing an operator actually reasons about, so every value is meaningful — no two
    /// settings admit the identical number of runs the way 2 and 3 sessions both did under the old
    /// whole-life reservation. <c>NodeLoad</c> claims directly in this unit now; the old peak-
    /// sessions-per-run reservation this setting used to be divided by dissolves along with it —
    /// phases spawn what they need up to the run's own <see cref="SessionCapPerRun"/>, not a
    /// number reserved for the run's whole life.
    /// <para>
    /// Resolved through <see cref="OperatingSettingsResolver"/> rather than plain
    /// <c>ConfigurationBinder</c> binding — see this property's <see langword="internal"/> setter
    /// — because the retired-key conversion needs the same per-precedence-level walk
    /// <c>h9k config show</c> and <c>h9k daemon status</c> already perform, which
    /// <c>IConfiguration</c>'s own merged view cannot express on its own. The internal setter alone
    /// does not keep <c>ConfigurationBinder</c> from touching this key — it still converts a
    /// section's raw value before checking whether it can assign it, so an unparseable value would
    /// crash <c>Bind()</c> regardless — which is why Program.cs also excludes this key from the
    /// section its generic <c>Bind()</c> call sees.
    /// </para>
    /// </summary>
    public int MaxConcurrentTaskRuns { get; internal set; } = OperatingSettings.DefaultMaxConcurrentTaskRuns;

    /// <summary>
    /// How many agent sessions one run may hold simultaneously, globally by default (Decisions Log
    /// #111, Brian's ruling 2026-08-30) — overridable per task from the CLI at any time via
    /// <c>h9k task set-session-cap</c>, even mid-run, in which case <c>ReviewEngine</c> reads the
    /// task's own override in place of this default. A cap of 1 serializes the two review lenses
    /// instead of dispatching them together; the shipped default of 3 is deliberate headroom above
    /// today's routine peak of 2, inert until a future coded activity actually overlaps a third
    /// session — see <see cref="MaxConcurrentTaskRuns"/>'s own doc for why this property is also
    /// resolved through <see cref="OperatingSettingsResolver"/> rather than plain binding.
    /// </summary>
    public int SessionCapPerRun { get; internal set; } = OperatingSettings.DefaultSessionCapPerRun;

    /// <summary>
    /// This node's periodic token-spend budget (backlog: spend-governor step three), or null for
    /// no budget — the shipped default, unlike every other setting above, since "no budget" is
    /// the compiled default itself rather than a ceiling. Resolved through
    /// <see cref="OperatingSettingsResolver"/> rather than plain binding, for the identical reason
    /// <see cref="MaxConcurrentTaskRuns"/>'s own doc gives: it needs the resolver's own env-then-
    /// file-then-default walk, and an unparseable value here must fall back rather than crash.
    /// </summary>
    public long? SpendBudgetTokens { get; internal set; }

    /// <summary>The window <see cref="SpendBudgetTokens"/> resets on — see <see cref="OperatingSettings.SpendPeriod"/>.</summary>
    public string SpendPeriod { get; internal set; } = OperatingSettings.DefaultSpendPeriod;

    /// <summary>Fallback sweep interval; the doorbell usually wakes the loop sooner.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan LeaseTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Per-gate ceiling; an overrunning gate fails, never hangs the pipeline.</summary>
    public TimeSpan VerifyGateTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Closeout poll cadence: how often this node asks gh about each awaiting-review
    /// pull request it opened. Minutes, not seconds — reviews and CI move on human
    /// timescales and this is a doorbell-less domain (Decisions Log #22).
    /// </summary>
    public TimeSpan PullRequestPollInterval { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// The ceiling a widening backoff may reach while <c>gh</c> keeps failing (independent
    /// pre-PR review, cycles 3 and 4, an explicit acceptance criterion the first cut shipped
    /// without): a sweep where every attempted inspection failed doubles the wait until the next
    /// one, up to this bound, so a rate limit or an outage does not spend a call every
    /// <see cref="PullRequestPollInterval"/> forever. A sweep where at least one inspection
    /// succeeded resets the wait to <see cref="PullRequestPollInterval"/> immediately, so one
    /// permanently broken pull request never pins every other healthy one this node watches to
    /// the backoff ceiling (see <see cref="PullRequestMonitor.IsSweepFailure"/>) — the widening
    /// answers gh's own trouble, not a standing posture.
    /// </summary>
    public TimeSpan PullRequestPollBackoffMaxInterval { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The absolute lifetime ceiling of automatic closeout actions (reopen dispatches, plus
    /// errored-review re-requests) one task's pull request may spend, whatever obstruction
    /// each one answered — the true runaway backstop (log #11 spirit, backlog 45), separate
    /// from <see cref="MaxCloseoutLapsPerObstruction"/>. A busy pull request that keeps
    /// clearing DIFFERENT obstructions never trips the progress cap, so this is what still
    /// stops it eventually; six is generous next to the per-obstruction cap's default of two
    /// because "many different real problems, one after another" is the legitimate case this
    /// ceiling exists to allow. A manual h9k pr resolve resets it.
    /// </summary>
    public int MaxAutomaticCloseoutRuns { get; set; } = 6;

    /// <summary>
    /// Consecutive automatic closeout laps the monitor may spend on the SAME obstruction —
    /// the same failing check name, or the exact same set of unresolved review-thread ids —
    /// before it parks (backlog 45, origin incident: task 18's flat budget of 2 was spent on
    /// two unrelated obstructions, review threads then an unrelated CI flake, leaving no room
    /// for Brian's own deliberate re-request on PR 26). A lap that clears its obstruction —
    /// a different check now fails, or the thread set changed — resets this counter, because
    /// what the cap exists to catch is repetition without progress, not the raw count of
    /// laps a busy pull request needs. A human-initiated event observed on the pull request
    /// (a review re-request, a newly opened human thread) grants one lap regardless of this
    /// cap, since a person engaging is itself proof the loop is not running away; <see
    /// cref="MaxAutomaticCloseoutRuns"/> is the ceiling that still applies even then. A third
    /// candidate signal, a new top-level pull-request comment, was cut before merge: agents
    /// here post top-level comments too, authored under the same login as a human's, so there
    /// is no discriminator for one the way a review thread's starter has one (AGENTS.md).
    /// </summary>
    public int MaxCloseoutLapsPerObstruction { get; set; } = 2;

    /// <summary>
    /// How often the daemon retries runs parked on token-budget exhaustion (backlog 40).
    /// The subscription window resets on a known-ish clock rather than an event the
    /// platform can watch for, so a patient poll is the whole mechanism — hourly is close
    /// enough for a window that resets on the order of hours, and a tighter interval would
    /// only spend process spawns proving the window is still shut.
    /// </summary>
    public TimeSpan TokenBudgetRetryInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Cycles the conformance track may run before the run parks for a human (Decisions Log
    /// #63). Conformance has no severity grades to gate on — a criterion is met or it is not —
    /// so its bound is simply "how many times may a machine be told the same thing". A
    /// conformance review still returning findings at this cycle parks, because at that point
    /// nothing automated is left to try.
    /// </summary>
    public int MaxComplianceReviewCycles { get; set; } = OperatingSettings.DefaultMaxComplianceReviewCycles;

    /// <summary>
    /// Cycles the adversarial track may run before the run parks (Decisions Log #63). It is
    /// deliberately far larger than the conformance cap: the severity gate, not the counter, is
    /// what ends this track in practice, and reaching this many cycles means the machine kept
    /// finding real high-severity problems — a fact a human should look at rather than a
    /// budget that quietly ran out.
    /// </summary>
    public int MaxAdversarialReviewCycles { get; set; } = OperatingSettings.DefaultMaxAdversarialReviewCycles;

    /// <summary>
    /// Rounds the mandatory <see cref="ReviewMode.FinalFullPass"/> may run before the run
    /// parks for a human (task: review cycles after the first, cycle-3 finding: the per-track
    /// cycle caps are measured from <see cref="RunAggregate.TrackBudgetBaseCycle"/>, which a
    /// <see cref="Hall9k.Domain.Features.Run.Events.ReviewTrackReactivated"/> deliberately resets — by design
    /// (<see cref="RunAggregate.TrackBudgetBaseCycle"/>'s own doc) — so a track the final pass
    /// keeps reawakening never trips its own cap, and the two-full-passes-plus-fix-session
    /// iteration could otherwise recur without end. This is the independent bound that ends it:
    /// however many times the mandatory pass has run for this run, once it reaches this count
    /// without ever settling, a human looks rather than the loop grinding forever.
    /// </summary>
    public int MaxFinalFullPassRounds { get; set; } = OperatingSettings.DefaultMaxFinalFullPassRounds;

    /// <summary>
    /// The task-lifetime ceiling on review cycles, counted across every run and follow-up a task
    /// has had — cycles are recorded per run (<see cref="RunAggregate.ReviewCycle"/>), so this is
    /// summed at cap-check time rather than kept as a second counter
    /// (<c>ReviewEngine.LifetimeReviewCycleCountAsync</c>). Immune to the per-run resets a
    /// stranding, retry, or follow-up round would otherwise give <see cref="MaxComplianceReviewCycles"/>,
    /// <see cref="MaxAdversarialReviewCycles"/>, and <see cref="MaxFinalFullPassRounds"/> — the
    /// gap that let task b6dfcbe5 reach 52 review cycles across nine generations with every
    /// per-run cap firing correctly and repeatedly, because no cap ever saw the task's true
    /// history (2026-08-30). Generous by design: it exists to catch genuine pathology, not to
    /// second-guess an ordinary multi-generation task. Once exceeded, every subsequent settle
    /// point parks for a human until a human resolution (<c>ReviewEngine.ParkIfLifetimeBudgetExceededAsync</c>).
    /// </summary>
    public int LifetimeReviewCycleBudget { get; set; } = OperatingSettings.DefaultLifetimeReviewCycleBudget;

    /// <summary>
    /// This node's review stage composition (task: the review pipeline's stage composition
    /// becomes configuration recorded per run) — bound through the same "Hall9k" configuration
    /// section as the four review-cycle caps above, never a <see cref="DaemonOptionsBinding.ResolverOwnedKeys"/>
    /// entry, so a hand-edited config file or an environment variable can carry a value
    /// <c>ConfigurationBinder</c> throws on if it is not a recognized composition word — the same
    /// shape those four caps already have. Task &gt; project &gt; node &gt; this compiled default
    /// (<see cref="ReviewStageComposition.FullPipeline"/>) is <see cref="ReviewStageCompositionResolver"/>'s
    /// own resolution order, resolved once at dispatch (<see cref="RunLauncher"/>) rather than
    /// re-checked live the way the caps are — see <see cref="ReviewStageComposition"/>'s own doc
    /// for why.
    /// </summary>
    public string ReviewStageComposition { get; set; } = Hall9k.Domain.Features.Run.ReviewStageComposition.FullPipeline.Value;

    /// <summary>
    /// The first adversarial cycle the severity gate applies to (Decisions Log #63). A Low or an
    /// ungraded finding rides along instead of being fixed on its own at every cycle, gate or no
    /// gate (Decisions Log #87) — the gate does not turn that rule on. What the gate changes is
    /// whether the track is forced into another cycle regardless of severity: before it, a
    /// needs-fixes verdict with a Route finding still runs the track again even though nothing
    /// attached meets the fix bar (a needs-fixes verdict whose findings are all ride-alongs is
    /// demoted to merge-ready before it ever reaches this rule), because the early cycles get
    /// full rigor on purpose while the code is still converging; from it onward only a High still
    /// forces the next cycle, and a Medium is fixed that cycle without forcing another. The gate
    /// exists for the nit-churn tail, which is where the conformance-only loop used to park work
    /// that would have converged one or two cycles later.
    /// </summary>
    public int AdversarialSeverityGateFromCycle { get; set; } = 4;

    /// <summary>
    /// How many immediate BlockedBy blockers a claimed task may have before their handoffs
    /// are condensed rather than passed through raw (Decisions Log #36). Fan-in is healthy —
    /// eight tasks converging on an integration task is good decomposition — but eight
    /// handoffs is a heavy way to start, so above this count the daemon dispatches a
    /// synthesis session first. Configuration rather than a constant, because the right
    /// number is only visible once real fan-in patterns appear.
    /// </summary>
    public int BlockerSynthesisThreshold { get; set; } = 3;

    /// <summary>
    /// How long a dispatch waits for its synthesis session before giving up and starting on the
    /// raw handoffs (Decisions Log #36). Unlike every other session the daemon spawns, this one
    /// is on the critical path — the dependent cannot start until its context is ready — and
    /// RunLauncher.LaunchAsync is awaited inside the dispatch loop, so a condenser that hangs
    /// would hold up every other claim on this node. The ceiling is what keeps that cost
    /// bounded: the wait ends, the timed-out session is terminated, and the run starts with the
    /// handoffs it already had. Condensing is an optimization over a context that already
    /// exists, so waiting forever for it is never the right trade.
    /// </summary>
    public TimeSpan BlockerSynthesisTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// This node's default answer to "ask the reviewers for another pass once a fix
    /// follow-up pushed?" (Decisions Log #62), under both the project and the owner
    /// settings in the resolution chain. Off, because each pass costs review quota and a
    /// reviewer re-reading a diff it has already read is how the refinement loop starts.
    /// </summary>
    public string DefaultReviewRerequest { get; set; } = ReviewRerequestPolicy.Disabled;

    /// <summary>
    /// How many countersign re-requests one task's pull request may draw before it settles
    /// on the internal review, the thread replies, and CI (Decisions Log #62). Its own
    /// counter beside MaxAutomaticCloseoutRuns rather than part of it: a re-request asks a
    /// reviewer a question, while the closeout budget bounds the agent runs that answer
    /// them, and one running out should not silently spend the other.
    /// </summary>
    public int MaxReviewRerequestsAfterFixes { get; set; } = 2;

    /// <summary>
    /// How long a card-publication session (backlog 18) gets before the daemon stops waiting and
    /// terminates it. Generous, because the session may be reading a repository's rules and
    /// conventions before composing the payload it submits through the write surface, and
    /// bounded for the same reason the synthesis pass is bounded: a hung session that nothing
    /// ever gives up on holds the publication queue behind it forever, and an abandoned agent
    /// burning tokens for nobody is worse than a request that says it timed out and can be run
    /// again.
    /// </summary>
    public TimeSpan CardPublicationTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How often the daemon looks for publication requests. The doorbell usually gets there
    /// first (h9k task push-to-jira rings it); this is the sweep that covers a request made while
    /// the daemon was down.
    /// </summary>
    public TimeSpan CardPublicationPollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often the daemon sweeps every project home and re-renders task.md/idea.md for whatever
    /// changed (backlog 48). The doorbell usually gets there first — most task and idea commands
    /// ring it — so this is the backstop for the ones that do not (a draft revise, since nothing
    /// dispatches from it) and for the daemon-start reconciliation pass.
    /// </summary>
    public TimeSpan ProjectHomeRenderPollInterval { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long a publication session dispatched by <em>another</em> node may stand before this
    /// node ends the request rather than waiting on a machine it cannot ask.
    /// <para>
    /// Adoption is scoped to the node that spawned the session, because a pid means nothing off
    /// the machine that issued it — which leaves a dispatch recorded against a node that never
    /// comes back with nothing to clear it: the sweep skips a dispatched request, push-to-jira
    /// refuses while one is outstanding, link-jira needs a card key that may not exist, and
    /// abandoning deliberately keeps the marker. This is the way out, and it is the same shape as
    /// <see cref="LeaseTimeout" />: what nobody is heard from about for long enough is ended.
    /// It is generous next to <see cref="CardPublicationTimeout" /> on purpose — a node that is
    /// alive stops its own session at that ceiling and records the outcome itself, so anything
    /// still standing four times later is a node that is not there. Origin incident (2026-08-22):
    /// the pre-PR review of this branch traced it from a machine rename, which gives the same
    /// install a new node identity and strands every publication the old one dispatched.
    /// </para>
    /// </summary>
    public TimeSpan ForeignPublicationCeiling { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How often the daemon retries a Jira write stuck on a rejected credential (Brian's design,
    /// 2026-08-28). There is no doorbell for "the connection was just fixed" — nothing on this
    /// machine observes that moment — so a patient poll is the whole mechanism, the same
    /// shape <see cref="TokenBudgetRetryInterval"/> already uses for a subscription window that
    /// resets on its own clock. Short next to that one: a re-authentication is a deliberate act a
    /// human just took, not a window they are waiting out, so the write should not sit for an
    /// hour once they have done it.
    /// </summary>
    public TimeSpan JiraWriteRetryInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a Jira write may sit pending with no outstanding authentication problem before
    /// <see cref="JiraWrites.JiraWriteRetryEngine"/> ends it on the clock alone (independent
    /// pre-PR review, cycle 1, both lenses). A write stuck on an expired or missing login stays
    /// pending on purpose (see <see cref="JiraWriteRetryInterval"/>), but a write cancelled — an
    /// operator's own Ctrl-C, or the daemon stopping mid-sweep — between
    /// <c>JiraWriteRequested</c> and its outcome has no such excuse and no spawned session for
    /// anything to adopt later, unlike <see cref="ForeignPublicationCeiling"/>'s own pid-tracked
    /// counterpart: every HTTP call <c>JiraWriteExecutor</c> makes is bounded well inside this
    /// window (<c>JiraHttp.Deadline</c>), and a create's own dedup search is bounded too — its own
    /// confirming calls are capped (<c>JiraWriteExecutor.MaxMarkerSearchCandidates</c>) rather than
    /// run once per search hit — so a write still pending this long was not merely slow. Generous next
    /// to how long any single write can actually take, for the same reason
    /// <see cref="ForeignPublicationCeiling"/> is generous next to
    /// <see cref="CardPublicationTimeout"/>: only a write nothing is working on any more should
    /// ever reach it.
    /// </summary>
    public TimeSpan PendingJiraWriteCeiling { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Platform-default commit style for follow-up runs (Narrative or Append), applied
    /// when a project sets none of its own (Decisions Log #26). Narrative folds fixes
    /// into their owning commits per the AGENTS.md authored-history rule. This is the
    /// node-level default until user-level defaults arrive (IDEA-platform-defaults).
    /// </summary>
    public string DefaultCommitStyle { get; set; } = CommitStyle.Narrative;

    /// <summary>
    /// The model every agent session runs on unless something more specific says otherwise:
    /// the bottom of the resolution chain, and the reason the platform no longer inherits
    /// whatever the human's personal Claude Code default happens to be that day
    /// (Decisions Log #33). An exact model id rather than a tier alias, because an alias is
    /// re-pointed as new models ship, which is the same drift in slower motion, and the
    /// 1M-context variant because that is what dispatched sessions were observed running on:
    /// shipping the standard-context id would have been its own silent narrowing.
    /// </summary>
    public string DefaultModel { get; set; } = AgentModel.PlatformFallback;

    /// <summary>
    /// Per-role model defaults, all empty as shipped: one configured model everywhere, no
    /// tiering. The knob and the record are the point; which role deserves which tier is a
    /// question for the spend data this task makes queryable (Decisions Log #33).
    /// </summary>
    public RoleModelDefaults ModelByRole { get; set; } = new();

    /// <summary>
    /// The effective model for a session: task override, then this node's role default, then
    /// the project default, then the platform default (Decisions Log #33). Every level is
    /// optional; the chain always ends somewhere explicit.
    /// </summary>
    public AgentModel ResolveModel(AgentRole role, AgentModel? taskModel, AgentModel? projectModel) =>
        AgentModel.Resolve(taskModel, ModelByRole.For(role), projectModel, DefaultModel);

    /// <summary>
    /// The effective model for a <see cref="Hall9k.Domain.Features.Run.ReviewMode.Verify"/> pass
    /// specifically (Brian's ruling, 2026-08-29): a task override still wins, same as any other
    /// pass, but underneath it sits <see cref="RoleModelDefaults.ReviewVerify"/> rather than the
    /// full role chain — a knob deliberately independent of <see cref="AgentRole"/>, because
    /// Verify is still Review-role work (Decisions Log #33's session shape is unchanged), just a
    /// different pass shape with its own mechanical, confirm-the-fix-and-check-blast-radius
    /// profile. Left unset, this falls through to exactly what a Discovery or FinalFullPass pass
    /// on the same task/project would resolve to, so the knob is opt-in and changes nothing until
    /// an install sets it.
    /// </summary>
    public AgentModel ResolveVerifyReviewModel(AgentModel? taskModel, AgentModel? projectModel)
    {
        AgentModel taskOverride = AgentModel.FromInput(taskModel);
        if (taskOverride != AgentModel.Unknown)
        {
            return taskOverride;
        }

        AgentModel verifyDefault = AgentModel.FromInput(ModelByRole.ReviewVerify);
        return verifyDefault != AgentModel.Unknown
            ? verifyDefault
            : ResolveModel(AgentRole.Review, taskModel, projectModel);
    }
}

/// <summary>
/// Node-level model defaults per session role (Decisions Log #33). The roles are named
/// rather than held in a dictionary so `h9kd --help`-shaped discovery, config binding, and
/// this file itself all state exactly which sessions are configurable. Blank means "no role
/// opinion" and the chain falls through to the project and platform defaults.
/// </summary>
public sealed class RoleModelDefaults
{
    /// <summary>The session that writes the feature (RunLauncher).</summary>
    public string Build { get; set; } = string.Empty;

    /// <summary>The independent reviewer over the run's diff; it reads far more than it writes (log #24).</summary>
    public string Review { get; set; } = string.Empty;

    /// <summary>The session that applies review findings in the run's worktree (log #24).</summary>
    public string Fix { get; set; } = string.Empty;

    /// <summary>The session that condenses a fan-in of blocker handoffs into one document (log #36).</summary>
    public string Synthesis { get; set; } = string.Empty;

    /// <summary>The (future) draft-refinement run, backlog IDEA-draft-refinement-runs: configurable before it exists.</summary>
    public string Refinement { get; set; } = string.Empty;

    /// <summary>The session that writes a task up as a card in an external tracker (backlog 18).</summary>
    public string Publication { get; set; } = string.Empty;

    /// <summary>
    /// The Review role's model for a <see cref="Hall9k.Domain.Features.Run.ReviewMode.Verify"/>
    /// pass specifically, underneath a task override but above <see cref="Review"/>'s own chain
    /// (Brian's ruling, 2026-08-29): blank falls through to whatever <see cref="Review"/> itself
    /// resolves to, so this is not a seventh <see cref="AgentRole"/> — Verify is still Review-role
    /// work — it is a narrower override for one pass shape, read by
    /// <see cref="DaemonOptions.ResolveVerifyReviewModel"/> rather than <see cref="For"/>.
    /// </summary>
    public string ReviewVerify { get; set; } = string.Empty;

    public AgentModel For(AgentRole role) => role switch
    {
        _ when role == AgentRole.Build => AgentModel.FromInput(Build),
        _ when role == AgentRole.Review => AgentModel.FromInput(Review),
        _ when role == AgentRole.Fix => AgentModel.FromInput(Fix),
        _ when role == AgentRole.Synthesis => AgentModel.FromInput(Synthesis),
        _ when role == AgentRole.Refinement => AgentModel.FromInput(Refinement),
        _ when role == AgentRole.Publication => AgentModel.FromInput(Publication),
        _ => AgentModel.Unknown,
    };
}

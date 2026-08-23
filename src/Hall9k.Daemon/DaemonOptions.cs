using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Daemon;

public sealed class DaemonOptions
{
    public const string SectionName = "Hall9k";

    /// <summary>
    /// How many agent sessions this node may have resident at once (PLAN.md §6.4 guidance,
    /// Decisions Log #64). Sessions rather than runs, because a session is the process the
    /// machine runs out of memory for and a run tree is not one process: a review cycle spawns
    /// every active lens together and waits on them together (log #59), so a run under review
    /// is <c>ReviewLens.CycleLenses.Count</c> resident sessions. The dispatcher converts, in
    /// <c>NodeLoad</c>: it claims in runs, because a run is the only thing it can decline to
    /// start, and charges each live run the peak sessions its tree can hold. Everything past
    /// the ceiling stays Queued and is claimed as slots free up.
    /// <para>
    /// A run gives its sessions back when its tree ends: completed, failed, parked, or handed
    /// to a follow-up with nothing running. A task waiting on a merge observation holds no
    /// memory and so holds nothing.
    /// </para>
    /// <para>
    /// Configuration rather than a constant because the right number is a property of the
    /// machine: the tower with the memory to hold six agent sessions and the laptop that
    /// panicked at four are the same platform with different answers. The shipped default is
    /// the count this machine was observed to carry, which on a two-lens review cycle means one
    /// run at a time — deliberately, because the alternative is budgeting for the average and
    /// discovering the peak the way the origin incident did. Origin incident (2026-08-21): an
    /// OOM killed three of four concurrently dispatched agent sessions the first time the queue
    /// went four wide, because the dispatcher claimed everything eligible and left the enforcing
    /// to the machine.
    /// </para>
    /// </summary>
    public int MaxConcurrentAgentSessions { get; set; } = 3;

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
    /// Automatic follow-up runs the closeout monitor may dispatch per task before it
    /// parks the pull request for the human (bounded retries, log #11 spirit). A manual
    /// h9k pr resolve resets the budget.
    /// </summary>
    public int MaxAutomaticCloseoutRuns { get; set; } = 2;

    /// <summary>
    /// How often the daemon retries runs parked on token-budget exhaustion (Decisions Log
    /// #40). The subscription window resets on a known-ish clock rather than an event the
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
    public int MaxComplianceReviewCycles { get; set; } = 3;

    /// <summary>
    /// Cycles the adversarial track may run before the run parks (Decisions Log #63). It is
    /// deliberately far larger than the conformance cap: the severity gate, not the counter, is
    /// what ends this track in practice, and reaching this many cycles means the machine kept
    /// finding real high-severity problems — a fact a human should look at rather than a
    /// budget that quietly ran out.
    /// </summary>
    public int MaxAdversarialReviewCycles { get; set; } = 10;

    /// <summary>
    /// The first adversarial cycle the severity gate applies to (Decisions Log #63). Before it,
    /// every finding of every grade is fixed and forces a fresh re-review; from it onward only
    /// a High forces the next cycle while Mediums and Lows are still fixed. The early cycles
    /// get full rigor on purpose — the code is still converging there — and the gate exists for
    /// the nit-churn tail, which is where the conformance-only loop used to park work that
    /// would have converged one or two cycles later.
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
    /// talking to Jira over MCP, and bounded for the same reason the synthesis pass is bounded: a
    /// hung session that nothing ever gives up on holds the publication queue behind it forever,
    /// and an abandoned agent burning tokens for nobody is worse than a request that says it
    /// timed out and can be run again.
    /// </summary>
    public TimeSpan CardPublicationTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How often the daemon looks for publication requests. The doorbell usually gets there
    /// first (h9k task push-to-jira rings it); this is the sweep that covers a request made while
    /// the daemon was down.
    /// </summary>
    public TimeSpan CardPublicationPollInterval { get; set; } = TimeSpan.FromSeconds(30);

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

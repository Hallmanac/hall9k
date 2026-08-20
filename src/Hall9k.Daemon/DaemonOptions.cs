using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Daemon;

public sealed class DaemonOptions
{
    public const string SectionName = "Hall9k";

    /// <summary>Node-level cap on simultaneously claimed/running tasks (PLAN.md §6.4 guidance).</summary>
    public int MaxConcurrentRuns { get; set; } = 3;

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
    /// Automatic fix runs the pre-PR review loop may dispatch per run before it parks
    /// the run for the human (the closeout retry-budget pattern, log #24). Each cycle is
    /// review → fix → gates → review; the budget counts the fix legs.
    /// </summary>
    public int MaxAutomaticReviewFixRuns { get; set; } = 2;

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

    /// <summary>The (future) draft-refinement run, backlog IDEA-draft-refinement-runs: configurable before it exists.</summary>
    public string Refinement { get; set; } = string.Empty;

    public AgentModel For(AgentRole role) => role switch
    {
        _ when role == AgentRole.Build => AgentModel.FromInput(Build),
        _ when role == AgentRole.Review => AgentModel.FromInput(Review),
        _ when role == AgentRole.Fix => AgentModel.FromInput(Fix),
        _ when role == AgentRole.Refinement => AgentModel.FromInput(Refinement),
        _ => AgentModel.Unknown,
    };
}

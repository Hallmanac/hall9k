using Hall9k.Domain.Features.Project;

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
}

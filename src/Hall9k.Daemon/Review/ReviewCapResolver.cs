using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Persistence;

namespace Hall9k.Daemon.Review;

/// <summary>
/// Which level actually supplied a resolved review cap's value (task: the review cycle caps
/// become settable at three levels) — an unpersisted, in-process outcome (AGENTS.md §8), never a
/// value object. <see cref="Node"/> and <see cref="Default"/> are distinguished by whether the
/// node's own resolved value differs from the compiled default; a node config explicitly set to
/// the same number as the compiled default is indistinguishable from unset and reads as
/// <see cref="Default"/> — a cosmetic gap in the park message's wording only, never in which
/// value is actually enforced.
/// </summary>
public enum ReviewCapLevel
{
    Default,
    Node,
    Project,
    Task,
}

/// <summary>A cap's resolved value together with which level supplied it, for the park message to name.</summary>
public sealed record ResolvedReviewCap(int Value, ReviewCapLevel Level)
{
    public string Describe() => Level switch
    {
        ReviewCapLevel.Task => "a task override",
        ReviewCapLevel.Project => "a project override",
        ReviewCapLevel.Node => "this node's configured value",
        _ => "the compiled default",
    };
}

/// <summary>
/// The four review-cycle caps as this run's task and project actually resolve them, right now
/// (task: the review cycle caps become settable at three levels). Resolved fresh at every cap
/// check rather than once per run, the same discipline
/// <c>ReviewEngine.VerifyCommandsFingerprintMatchesAsync</c> already applies to a project setting
/// that can change mid-run: a task or project override set while this run's review loop is
/// live — including the takeover lever, a task cap set at or below a track's current cycle
/// count — must be seen at the very next check, not only on a future run.
/// </summary>
public sealed record ResolvedReviewCaps(
    ResolvedReviewCap MaxComplianceReviewCycles,
    ResolvedReviewCap MaxAdversarialReviewCycles,
    ResolvedReviewCap MaxFinalFullPassRounds,
    ResolvedReviewCap LifetimeReviewCycleBudget)
{
    /// <summary>The cycle cap for a track — the resolved shape of <see cref="ReviewTrackPolicy.CapFor"/>.</summary>
    public ResolvedReviewCap CapFor(ReviewLens lens) =>
        lens == ReviewLens.Adversarial ? MaxAdversarialReviewCycles : MaxComplianceReviewCycles;
}

/// <summary>
/// Resolves each of the four review-cycle caps independently, strictly task &gt; project &gt; node
/// &gt; compiled default (Brian's ruling, 2026-08-29) — the first no-op hierarchy walker for an
/// int setting in this codebase (<c>ReviewRerequestPolicy.Resolve</c> and
/// <c>DaemonOptions.ResolveModel</c> are the nearest precedents, but neither resolves this
/// strictly: the model chain puts the node's per-role default above the project, Decisions Log
/// #33). <c>OperatingSettingsResolver</c> is the node level's own resolver for
/// <c>h9k config show</c>/<c>h9k daemon status</c>, run once per CLI invocation against the
/// platform config file directly; this type resolves the SAME node value as it is already bound
/// into the live <see cref="DaemonOptions"/> (the two agree by construction — both read the
/// identical env-var/config-file/default precedence, just through different pipelines), because
/// the daemon has no config-file-reading session mid-review to spend on it.
/// </summary>
public static class ReviewCapResolver
{
    public static ResolvedReviewCaps Resolve(TaskDetails? task, ProjectDetails? project, DaemonOptions nodeOptions) =>
        new(
            ResolveOne(
                task?.MaxComplianceReviewCycles, project?.MaxComplianceReviewCycles,
                nodeOptions.MaxComplianceReviewCycles, OperatingSettings.DefaultMaxComplianceReviewCycles),
            ResolveOne(
                task?.MaxAdversarialReviewCycles, project?.MaxAdversarialReviewCycles,
                nodeOptions.MaxAdversarialReviewCycles, OperatingSettings.DefaultMaxAdversarialReviewCycles),
            ResolveOne(
                task?.MaxFinalFullPassRounds, project?.MaxFinalFullPassRounds,
                nodeOptions.MaxFinalFullPassRounds, OperatingSettings.DefaultMaxFinalFullPassRounds),
            ResolveOne(
                task?.LifetimeReviewCycleBudget, project?.LifetimeReviewCycleBudget,
                nodeOptions.LifetimeReviewCycleBudget, OperatingSettings.DefaultLifetimeReviewCycleBudget));

    private static ResolvedReviewCap ResolveOne(int? taskValue, int? projectValue, int nodeValue, int compiledDefault) =>
        taskValue is { } fromTask ? new ResolvedReviewCap(fromTask, ReviewCapLevel.Task)
        : projectValue is { } fromProject ? new ResolvedReviewCap(fromProject, ReviewCapLevel.Project)
        : nodeValue != compiledDefault ? new ResolvedReviewCap(nodeValue, ReviewCapLevel.Node)
        : new ResolvedReviewCap(nodeValue, ReviewCapLevel.Default);
}

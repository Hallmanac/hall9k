using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Projections;

namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// What <c>h9k task publish</c> is told about the project's challenge requirement and what the publisher
/// passed. Null where a caller does not take part in the gate at all (the daemon's own publishes, the
/// pull-request review doors, and the tests that publish through the decider directly), which behaves as it
/// did before the gate existed.
/// </summary>
/// <param name="Required">Whether the project requires a challenge verdict (<see cref="Project.ChallengeRequirement"/>).</param>
/// <param name="ProjectName">The project's name, so a refusal can quote the exact command that turns the requirement off.</param>
/// <param name="SkipReason">
/// The publisher's <c>--skip-challenge</c> reason, or null when the flag was not passed. A blank one is refused
/// whether or not the gate asks, because a blank reason is an empty shell variable and never a statement.
/// </param>
public sealed record ChallengeGateInput(bool Required, string ProjectName, string? SkipReason);

/// <summary>
/// The publish gate's one rule, in one place so <c>h9k task publish</c>, the <c>Next:</c> lines of the verbs that
/// print a draft's next step, and <c>h9k task show</c> all answer the same question: would publishing this
/// draft be refused for want of a usable challenge verdict.
/// <para>
/// The latest verdict decides, and a failure replaces nothing. A missing verdict and a <c>do not publish</c>
/// refuse. A <c>publish as written</c> passes. A <c>publish as revised</c> passes when it holds no must change,
/// or when the draft has been revised since it landed, because revising is how a verdict gets applied. Any
/// other state passes, including later revisions of a verdict that already passed.
/// </para>
/// <para>
/// It never applies to a pr-review task, which has no author to challenge, or to a spike, which is bounded by
/// its own exit criterion and is deliberately a run rather than a walk.
/// </para>
/// </summary>
public static class ChallengeGate
{
    /// <summary>Whether a task of this type can be gated at all.</summary>
    public static bool AppliesTo(TaskType type) => type != TaskType.PrReview && type != TaskType.Spike;

    /// <summary>
    /// Why publishing this draft would be refused for want of a challenge verdict, or null when it would not be.
    /// </summary>
    public static string? Shortfall(TaskAggregate task, bool required) =>
        Shortfall(
            task.Type, task.LatestChallengeVerdict, task.LatestChallengeOutcome, task.RevisedSinceChallengeVerdict,
            required);

    /// <summary>The same question asked of the read model, for the displays that hold a document and not a stream.</summary>
    public static string? Shortfall(TaskDetails task, bool required) =>
        Shortfall(
            task.Type, task.LatestChallengeVerdict, task.LatestChallengeOutcome, task.RevisedSinceChallengeVerdict,
            required);

    /// <summary>The rule itself, over the four facts it reads.</summary>
    public static string? Shortfall(
        TaskType type, DraftChallengeRecorded? latestVerdict, DraftChallengeRecorded? latestOutcome,
        bool revisedSinceVerdict, bool required)
    {
        if (!required || !AppliesTo(type))
        {
            return null;
        }

        if (latestVerdict is not { } verdict)
        {
            return latestOutcome is { IsVerdict: false }
                ? "its only challenge ended with no verdict, so there is none on record"
                : "it has no challenge verdict on record";
        }

        if (verdict.Verdict == ChallengeVerdict.DoNotPublish)
        {
            return "its latest challenge verdict is 'do not publish'";
        }

        return verdict.Verdict == ChallengeVerdict.PublishAsRevised
            && verdict.MustCount > 0
            && !revisedSinceVerdict
            ? $"its latest challenge verdict is 'publish as revised' holding {verdict.MustCount} must "
              + $"change{(verdict.MustCount == 1 ? string.Empty : "s")} and the draft has not been revised since it landed"
            : null;
    }

    /// <summary>
    /// Whether a Draft's <c>Next:</c> line should point at the challenge verb rather than at publish: while the publish
    /// gate would refuse it. A challenge already requested or running is waited for, and the line says so elsewhere.
    /// </summary>
    public static bool NextStepIsChallenge(TaskAggregate task, bool required) =>
        task.State == TaskState.Draft
        && !task.ChallengeRequested
        && task.RunningChallengeSessionId is null
        && Shortfall(task, required) is not null;

    /// <summary>The read-model twin of <see cref="NextStepIsChallenge(TaskAggregate, bool)"/>.</summary>
    public static bool NextStepIsChallenge(TaskDetails task, bool required) =>
        task.State == TaskState.Draft
        && !task.ChallengeRequested
        && task.RunningChallengeSessionId is null
        && Shortfall(task, required) is not null;
}

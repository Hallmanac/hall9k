using Hall9k.Domain.Features.Tasks;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// Resolves the review stage composition strictly task &gt; project &gt; node &gt; compiled
/// default (task: the review pipeline's stage composition becomes configuration recorded per
/// run) — the same strict hierarchy <c>Hall9k.Daemon.Review.ReviewCapResolver</c> already walks
/// for the four review-cycle caps, applied to a single closed-set value instead of four integers.
/// Takes the three raw strings rather than the aggregate/projection/options types themselves so
/// both dispatch sites can call it: <c>Hall9k.Daemon.Execution.RunLauncher</c> (headless dispatch,
/// reading <c>DaemonOptions.ReviewStageComposition</c> for the node level) and
/// <c>Hall9k.Cli.Commands.TaskWorkCommand</c> (an interactive claim's own dispatch, which has no
/// live <c>DaemonOptions</c> to read and instead reads the same platform config file value
/// directly — <c>Hall9k.Domain → Hall9k.Cli</c> is a legal reference, <c>Hall9k.Daemon → Hall9k.Cli</c>
/// is not, so this resolver lives in Domain rather than beside the caps' own daemon-only one).
/// Unlike the caps, this is resolved once, at dispatch, and never re-checked mid-run — see
/// <see cref="ReviewStageComposition"/>'s own doc for why.
/// <para>
/// <paramref name="taskType"/> answers between the project and the node level, not below the node
/// the way the hierarchy's own prose above suggests it might (task: a content task runs a lighter
/// pipeline by default): an explicit task or project value still wins over it exactly as before,
/// but <paramref name="nodeValue"/> never reaches a real caller as genuinely blank — both
/// <c>DaemonOptions.ReviewStageComposition</c> and <c>OperatingSettingsResolver</c>'s own node-level
/// resolution default to the literal string <c>"FullPipeline"</c> rather than an empty one, so a
/// content default placed below the node check would never actually fire in a real dispatch; only
/// this resolver's own direct unit tests can pass a genuinely blank node value. Checking
/// <see cref="TaskType.Content"/> ahead of <paramref name="nodeValue"/> is what lets the type's own
/// default actually reach <c>RunDispatched</c> rather than being permanently shadowed by the node's
/// own ambient one. <see cref="TaskType.Content"/> is the only type this changes anything for;
/// every other type still resolves task &gt; project &gt; node &gt; compiled default exactly as
/// before.
/// </para>
/// </summary>
public static class ReviewStageCompositionResolver
{
    public static ReviewStageComposition Resolve(
        string? taskValue, string? projectValue, string? nodeValue, TaskType? taskType = null)
    {
        ReviewStageComposition fromTask = ReviewStageComposition.FromInput(taskValue);
        if (fromTask != ReviewStageComposition.Unknown)
        {
            return fromTask;
        }

        ReviewStageComposition fromProject = ReviewStageComposition.FromInput(projectValue);
        if (fromProject != ReviewStageComposition.Unknown)
        {
            return fromProject;
        }

        if (taskType == TaskType.Content)
        {
            return ReviewStageComposition.ConformanceOnly;
        }

        ReviewStageComposition fromNode = ReviewStageComposition.FromInput(nodeValue);
        return fromNode != ReviewStageComposition.Unknown ? fromNode : ReviewStageComposition.FullPipeline;
    }
}

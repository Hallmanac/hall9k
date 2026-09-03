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
/// </summary>
public static class ReviewStageCompositionResolver
{
    public static ReviewStageComposition Resolve(string? taskValue, string? projectValue, string? nodeValue)
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

        ReviewStageComposition fromNode = ReviewStageComposition.FromInput(nodeValue);
        return fromNode != ReviewStageComposition.Unknown ? fromNode : ReviewStageComposition.FullPipeline;
    }
}

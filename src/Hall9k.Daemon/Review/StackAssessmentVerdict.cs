using Hall9k.Domain.Features.Run;

namespace Hall9k.Daemon.Review;

/// <summary>
/// A stack assessment run's structured verdict, once parsed off its own trailer (task: a stacked
/// checkpoint that would park for a human on a git shape first dispatches a read-only assessment
/// run). Not itself an event — the daemon reads this, acts on it, and records the facts it
/// carries onto <see cref="Hall9k.Domain.Features.Run.Events.StackAssessmentCompleted"/>, the same
/// separation <c>StackedParentObservation</c> keeps from the events its own caller appends.
/// </summary>
public sealed record StackAssessmentVerdict(
    StackAssessmentVerdictKind Kind, string BoundaryCommit, string OntoCommit, string Evidence)
{
    public static StackAssessmentVerdict Aligned(string boundaryCommit, string ontoCommit, string evidence) =>
        new(StackAssessmentVerdictKind.Aligned, boundaryCommit, ontoCommit, evidence);

    public static StackAssessmentVerdict Replay(string boundaryCommit, string ontoCommit, string evidence) =>
        new(StackAssessmentVerdictKind.Replay, boundaryCommit, ontoCommit, evidence);

    public static StackAssessmentVerdict Undecidable(string evidence) =>
        new(StackAssessmentVerdictKind.Undecidable, string.Empty, string.Empty, evidence);
}

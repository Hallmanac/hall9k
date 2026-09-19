namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// A spike's review cycle reached a verdict against its stated exit criterion (task: a spike is a
/// run, not a walk) — met, not-met after the one fix lap a spike gets, or budget-exhausted when
/// the build session's own budget was crossed first. Always appended alongside a
/// <see cref="TaskCompleted"/> (with no pull request) in the same batch, since a spike never
/// parks for a human and never opens one: this event carries the verdict, and
/// <see cref="TaskCompleted"/> is what actually reaches Done. When the spike was cut from an idea,
/// the daemon also appends the sibling <c>Hall9k.Domain.Features.Idea.Events.IdeaSpikeConcluded</c>
/// onto the idea's own stream, through the from-idea provenance <see cref="TaskAdded.SourceIdeaId"/>
/// already carries.
/// </summary>
public sealed record SpikeConcluded(
    Guid Id,
    Guid RunId,
    SpikeVerdict Verdict,
    string Reason,
    string FindingsPath,
    DateTimeOffset ConcludedAt);

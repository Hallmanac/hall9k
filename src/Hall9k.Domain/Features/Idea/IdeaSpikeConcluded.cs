using Hall9k.Domain.Features.Tasks;

namespace Hall9k.Domain.Features.Idea;

/// <summary>
/// A spike cut from this idea (<c>h9k task add --from-idea --type spike</c>) reached its verdict —
/// the sibling <see cref="Hall9k.Domain.Features.Tasks.Events.SpikeConcluded"/> lands on the
/// task's own stream, and the daemon appends this alongside it, on the idea's stream, through the
/// from-idea provenance <c>TaskAdded.SourceIdeaId</c> already carries (task: a spike is a run, not
/// a walk — "a later judgment session can find it"). <c>h9k idea show</c> lists the idea's spikes
/// with their verdicts by joining <see cref="IdeaAggregate.CutTaskIds"/> against the task's own
/// live projection, never off this event alone — this record is the idea-side provenance trail,
/// not the source of truth for what the verdict currently is.
/// </summary>
public sealed record IdeaSpikeConcluded(
    Guid Id,
    Guid TaskId,
    SpikeVerdict Verdict,
    string Reason,
    DateTimeOffset ConcludedAt);

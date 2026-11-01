using Hall9k.Domain.Features.Tasks.Notes;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// The one record of a task's note promotion: at the merge that closed the task out, a small session
/// read the notes this node may put in a prompt and decided which, if any, becomes a project lesson.
/// This is what came of it. A task has at most one, whatever its outcome, and it sits on the task's
/// stream as <see cref="LessonPickRecorded"/> does, because the decision belongs to the task and
/// follows it across nodes.
/// <para>
/// It holds ids, counts and closed vocabularies, never a word of a note and never a word the session
/// wrote: the claim lives only in the lesson it became. A render of this record therefore cannot pass
/// a note's text or a session's prose on to a reader or to a prompt.
/// </para>
/// <para>
/// A daemon that stops or is killed during the step records nothing, so the notes it was deciding
/// read as undecided. That is deliberate: there is no dispatch event and no recovery sweep, because
/// a step takes seconds and a lost one is cheap.
/// </para>
/// </summary>
/// <param name="Id">The task this record belongs to.</param>
/// <param name="RunId">The merged run the step ran for; a record for a task that has moved on to a newer run is never written.</param>
/// <param name="Outcome">How the step ended.</param>
/// <param name="Decisions">One entry per note the trust rule let through, offered or not, each kept with its lesson id or dropped with its reason.</param>
/// <param name="HeldCount">How many of the task's own notes the trust rule held out, which the step counts and never offers.</param>
/// <param name="IgnoredLineCount">How many lines of the answer named a note that was not offered or repeated one already read.</param>
/// <param name="Model">The model the session ran on.</param>
/// <param name="Duration">How long the session took, from spawn to the end of the wait.</param>
/// <param name="RecordedAt">When this record was made.</param>
public sealed record NotePromotionRecorded(
    Guid Id,
    Guid RunId,
    NotePromotionOutcome Outcome,
    IReadOnlyList<NotePromotionDecision> Decisions,
    int HeldCount,
    int IgnoredLineCount,
    AgentModel Model,
    TimeSpan Duration,
    DateTimeOffset RecordedAt)
{
    /// <summary>What this record decided about <paramref name="noteId"/>, or null when it names no such note.</summary>
    public NotePromotionDecision? DecisionFor(Guid noteId) =>
        Decisions.FirstOrDefault(decision => decision.NoteId == noteId);

    /// <summary>How many notes this record kept; at most <see cref="NotePromotionLimits.MaxKept"/>.</summary>
    public int KeptCount => Decisions.Count(decision => decision.LessonId is not null);

    /// <summary>How many notes this record dropped, whatever the reason.</summary>
    public int DroppedCount => Decisions.Count - KeptCount;
}

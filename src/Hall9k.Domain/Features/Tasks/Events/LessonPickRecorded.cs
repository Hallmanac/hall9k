using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// The one record of a task's lesson pick (<see cref="LessonPickerMode"/>): a small session read the
/// task and the lessons its node may put in a prompt, and this is what came of it. A task has at
/// most one, whatever its outcome, and every later prompt composition for the task reads it from the
/// task's stream, which is why it sits there and not on a run's: the set follows the task across
/// runs and nodes.
/// <para>
/// It holds lesson ids and counts, never a word the session wrote. The session's own text is read by
/// the daemon as ids alone and then discarded, so nothing that renders this record can pass a
/// session's prose on to a reader or to another prompt.
/// </para>
/// </summary>
/// <param name="Id">The task this pick belongs to.</param>
/// <param name="Mode">Shadow or on, as the setting stood when the pick ran. A set picked under shadow never reaches a prompt.</param>
/// <param name="Outcome">How the pick ended; anything but picked means the build went out on newest-first.</param>
/// <param name="PickedLessonIds">The lessons the pick carries, in the picker's order and within both caps; empty unless the outcome is picked.</param>
/// <param name="NewestFirstLessonIds">The lessons newest-first would have carried at that moment, so shadow can be compared against what it did not change.</param>
/// <param name="EligibleCount">How many lessons the composing node's provenance rule let into a prompt, before either cap.</param>
/// <param name="DroppedIdCount">How many ids in the answer named no offered lesson or repeated one already read.</param>
/// <param name="Model">The model the picker session ran on.</param>
/// <param name="Duration">How long the session took, from spawn to the end of the wait.</param>
/// <param name="RecordedAt">When this record was made.</param>
public sealed record LessonPickRecorded(
    Guid Id,
    LessonPickerMode Mode,
    LessonPickOutcome Outcome,
    IReadOnlyList<Guid> PickedLessonIds,
    IReadOnlyList<Guid> NewestFirstLessonIds,
    int EligibleCount,
    int DroppedIdCount,
    AgentModel Model,
    TimeSpan Duration,
    DateTimeOffset RecordedAt)
{
    /// <summary>
    /// Whether prompts composed under <paramref name="current"/> carry this pick's set: only a set
    /// picked under on, with a picked outcome, while the setting is still on. A set picked under
    /// shadow never qualifies, and a setting moved away from on stops it at the next composition.
    /// </summary>
    public bool CarriesIntoPrompts(LessonPickerMode current) =>
        current == LessonPickerMode.On && Mode == LessonPickerMode.On && Outcome == LessonPickOutcome.Picked;
}

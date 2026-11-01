using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Learning;

/// <summary>
/// A run-earned lesson, recorded (idea d805fd8b, piece 1; backlog 55). The genesis event of a
/// Learning stream, one stream per lesson rather than a growing tail on the Project stream: a
/// per-lesson stream gives each lesson its own UUIDv7 identity to cite and retire, and keeps a
/// project read from replaying every lesson ever recorded.
/// <para>
/// A recorded lesson is live the moment it lands. There is no quarantine and no corroboration
/// gate: the gate's own admission criterion — a second independent sighting — requires exactly
/// the rediscovery cost this feature exists to prevent (IDEA-learning-capture, "Why there is no
/// quarantine"). Noise protection comes from retiring a bad lesson cheaply instead.
/// </para>
/// </summary>
/// <param name="DistilledFrom">
/// The lessons this one was merged out of, when it was recorded by a distillation
/// (<see cref="LearningDecider.RecordDistilled"/>), and null for a lesson recorded on its own,
/// which is every lesson appended before distillation shipped, so a stream written then
/// deserializes to exactly the honest answer rather than to an empty list that would read as "a
/// distillation that cited nothing".
/// <para>
/// Citations, never a supersession. Merging does not end the lessons it merged: those retire by
/// the ordinary explicit act, with a reason naming this lesson (<c>h9k learn retire &lt;id&gt;
/// --reason "Absorbed into &lt;id&gt;"</c>), so the one terminal status this slice has stays the
/// one terminal status and "live" keeps meaning one thing. What this field adds is the audit
/// trail in the other direction: from the survivor back to what it claims to speak for.
/// </para>
/// </param>
/// <param name="PromotedFromNote">
/// The one task note this lesson was promoted from at closeout
/// (<see cref="LearningDecider.RecordPromoted"/>), and null for a lesson recorded any other way,
/// which includes every lesson on a stream written before promotion existed. A sibling of
/// <see cref="DistilledFrom"/> rather than a reuse of it, because that field holds lesson ids and a
/// note is not a lesson; and a single id rather than a list, because one kept note makes one lesson
/// and the type states that nothing is merged. The note's task is the provenance's own
/// <see cref="RecordedProvenance.TaskId"/>, which the promotion entry point requires, so the
/// citation always resolves to a task's stream.
/// </param>
public sealed record LearningRecorded(
    Guid Id,
    KnowledgeScope Scope,
    Guid ScopeId,
    string Statement,
    RecordedProvenance Provenance,
    DateTimeOffset RecordedAt,
    IReadOnlyList<Guid>? DistilledFrom = null,
    Guid? PromotedFromNote = null);

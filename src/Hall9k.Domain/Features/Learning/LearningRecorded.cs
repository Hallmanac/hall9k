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
public sealed record LearningRecorded(
    Guid Id,
    KnowledgeScope Scope,
    Guid ScopeId,
    string Statement,
    RecordedProvenance Provenance,
    DateTimeOffset RecordedAt);

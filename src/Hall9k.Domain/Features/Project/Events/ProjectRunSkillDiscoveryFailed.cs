namespace Hall9k.Domain.Features.Project.Events;

/// <summary>
/// The run-skill discovery session could not produce a usable skill (idea b9b09779, piece 4): it
/// would not spawn, it exceeded its bound, or its answer carried no readable shape or a document
/// missing sections of the shared shape. Distinct from
/// <see cref="ProjectRunSkillRecorded"/> carrying <c>none-discoverable</c>, which is a real
/// finding about the repository — this event is the honest absence of a finding, and
/// <c>h9k project show</c> says so rather than reporting the repository as having no way to run
/// it when nobody actually established that.
/// <para>
/// Ends the outstanding request rather than leaving it to redispatch every tick. Asking again is
/// a deliberate act: <c>h9k project set PROJECT --discover-run-skill</c>.
/// </para>
/// </summary>
public sealed record ProjectRunSkillDiscoveryFailed(
    Guid ProjectId,
    string Reason,
    DateTimeOffset FailedAt);

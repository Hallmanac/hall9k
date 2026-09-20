using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// One persona's review session died, and the run carried on without it (idea b9b09779, piece 1).
/// Recorded rather than failing the whole run, for every persona but the engineer's: a pull
/// request reviewed by three personas must not lose two good reports because the third's session
/// errored, and a persona that produced nothing has to be named — in <c>h9k task show</c> and in
/// the findings report — instead of quietly missing from a report that looks complete.
/// <para>
/// The engineer's own sessions are the exception and still fail the run outright
/// (<c>ReviewPersonaEntry.FailureFailsTheRun</c>), which is the behaviour a pr-review run has
/// always had.
/// </para>
/// </summary>
/// <param name="Slug">The session that failed (<c>ReviewPersonaSession.Slug</c>), so a persona with several sessions says which one.</param>
/// <param name="Reason">What was observed, in the words the engine would otherwise have failed the run with.</param>
public sealed record PrReviewPersonaSessionFailed(
    Guid Id,
    ReviewPersona Persona,
    string Slug,
    string Reason,
    DateTimeOffset FailedAt);

namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The platform's posting path refused to put a reply into a thread a PERSON opened (task: a
/// review-feedback follow-up never answers a human reviewer in the owner's name on its own).
/// Nothing reached GitHub: the refusal happens before the provider write, so this records an
/// attempt, never a half-sent reply.
/// <para>
/// This is the enforcement half of the park. The prompt tells a review-feedback lap to draft a
/// decline or a route for a human's thread and park it rather than posting it; this event is what
/// happens when a session does it anyway and reaches for <c>h9k pr reply</c>. Recorded on the run
/// stream rather than only logged, so the attempt is visible in <c>h9k task show</c> alongside
/// the park it tried to skip — a refusal nobody can read is indistinguishable from a session that
/// behaved.
/// </para>
/// </summary>
/// <param name="ThreadId">
/// The thread the reply was aimed at, named because a refusal that does not say which thread
/// leaves the operator unable to check whether the person is still owed an answer.
/// </param>
/// <param name="Disposition">The disposition the session claimed when it tried — decline, route, or an unstated one.</param>
/// <param name="Reason">The refusal in the words the session was given, so the run log and the board read the same sentence.</param>
public sealed record ReviewThreadReplyRefused(
    Guid Id,
    string ThreadId,
    ReviewThreadDisposition Disposition,
    string Reason,
    DateTimeOffset RefusedAt);

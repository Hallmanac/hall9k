namespace Hall9k.Domain.Features.Run;

/// <summary>
/// One unresolved review thread a PERSON opened, as the provider itself reported it at the moment
/// closeout decided to dispatch a follow-up (task: a review-feedback follow-up never answers a
/// human reviewer in the owner's name on its own). Three observed facts and nothing derived: the
/// thread's own node id, the login that opened it, and the link a human opens to read it.
/// <para>
/// It exists because the thread id alone is opaque. <c>TaskReopened.KnownHumanReviewThreadIds</c>
/// has carried the ids since Decisions Log #80, which is all a mechanical diff needs; a park that
/// asks a person to approve a reply needs to show them WHICH thread, and an opaque
/// <c>PRRT_…</c> is not that. The url is read off the thread's first comment on the same GraphQL
/// read the ids come from, so it is an observation rather than the session's own claim about
/// where its reply would land — the distinction <see cref="ReviewDisagreement.ReviewUrl"/>'s own
/// doc draws for the other direction.
/// </para>
/// </summary>
/// <param name="ThreadId">The GraphQL review-thread node id, which is what a reply mutation takes.</param>
/// <param name="Author">The login that opened the thread — the person waiting on an answer.</param>
/// <param name="Url">
/// The thread's first comment's own url. Blank when the provider reported none, which reads as
/// "no link observed" rather than being assembled from the pull request and a guessed anchor
/// (AGENTS.md: never guess at unobserved facts).
/// </param>
public sealed record ReviewThreadReference(string ThreadId, string Author, string Url);

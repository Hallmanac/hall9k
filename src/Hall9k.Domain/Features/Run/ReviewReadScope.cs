namespace Hall9k.Domain.Features.Run;

/// <summary>
/// One unresolved review thread a review-feedback lap may read, with exactly the comments in it
/// that may be read (task: feedback from outside the project's trust chain never reaches a build
/// lap without the owner's go). A reply a non-member left inside a member's or a bot's thread is
/// absent from <see cref="CommentIds"/>, which is how it is never read.
/// </summary>
/// <param name="ThreadId">The GraphQL review-thread node id.</param>
/// <param name="Opener">The login that opened the thread, or empty when the provider reported none.</param>
/// <param name="OpenedBy">Why the opener's thread may be read: a member, a bot, or owner-approved.</param>
/// <param name="CommentIds">The GraphQL node ids of the comments in the thread a session may read the body of.</param>
public sealed record ReadableReviewThread(
    string ThreadId, string Opener, FeedbackAdmission OpenedBy, IReadOnlyList<string> CommentIds);

/// <summary>
/// What a review-feedback lap is allowed to read of the pull request's unresolved threads, decided by
/// closeout at dispatch from the project's trust-chain roster and carried on the reopen so the
/// prompt can name it (task: feedback from outside the project's trust chain never reaches a build
/// lap without the owner's go).
/// <para>
/// A reopen that recorded no scope at all (every one from before the gate, and a manual
/// <c>h9k pr resolve</c> on a task closeout never dispatched) reads as a scope with nothing in it,
/// never as <see cref="Unrestricted"/>: absence of an answer is not permission.
/// </para>
/// </summary>
/// <param name="Threads">Every unresolved thread whose opener may be read, with the comments in it that may be.</param>
/// <param name="WithheldThreadIds">
/// Every unresolved thread opened by someone outside the roster that the owner has not approved.
/// Ids only, never text: the prompt names them so the session leaves them untouched.
/// </param>
/// <param name="Unrestricted">
/// True when the membership gate is off for this repository (a private one, or an explicit
/// <c>h9k project set --review-requires-membership off</c>), where every thread keeps being read
/// exactly as it always was.
/// </param>
public sealed record ReviewReadScope(
    IReadOnlyList<ReadableReviewThread> Threads,
    IReadOnlyList<string> WithheldThreadIds,
    bool Unrestricted = false)
{
    /// <summary>The scope a repository with the gate off records.</summary>
    public static readonly ReviewReadScope AllThreads = new([], [], Unrestricted: true);
}

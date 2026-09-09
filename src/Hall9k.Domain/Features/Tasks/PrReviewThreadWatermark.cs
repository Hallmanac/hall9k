namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// One review thread the reviewing owner opened on a pull request, and how many comments were
/// waiting on them in it the last time the follow-through poll looked — the comparison point that
/// lets a later poll tell "somebody answered" from "nothing has happened yet" (task: a pr-review
/// task stays open while the pull request's review threads are unresolved).
/// <para>
/// A count rather than the comments themselves, deliberately. The question this watermark answers
/// is whether the thread MOVED, and a count answers it from one integer per thread instead of
/// carrying somebody else's prose onto this platform's own event stream, where it would then age
/// against the pull request it was copied from. What was actually written is read live, from the
/// pull request, by the scoped lap that goes looking at it
/// (<c>h9k pr review --since-my-review</c>).
/// </para>
/// <para>
/// <see cref="ReplyCount"/> counts only the comments the reviewer did NOT write — everything after
/// their own last comment in the thread (<c>ReviewThread.ReplyCountFor</c>, the one definition both
/// the poll and the scoped lap read). A count of every comment instead would report the reviewer's
/// own review back to them as an answer, and a follow-up comment of their own would flip the task
/// to needs-you asserting that its author had replied — an attribution nothing observed
/// (independent pre-PR review, cycle 1, both lenses; AGENTS.md: never guess at unobserved facts).
/// </para>
/// <para>
/// The provider's own <c>totalCount</c> is what the reply count is measured against wherever it
/// reported one, so a thread whose comment page is capped still counts honestly rather than
/// silently flattening to the page size.
/// </para>
/// </summary>
/// <param name="ThreadId">The provider's own node id for the thread, which is what a later poll matches on.</param>
/// <param name="ReplyCount">How many comments were waiting on the reviewer in this thread when the watermark was taken.</param>
/// <param name="IsResolved">Whether the thread was resolved at that moment — what decides whether the follow-through is over.</param>
public sealed record PrReviewThreadWatermark(string ThreadId, int ReplyCount, bool IsResolved);

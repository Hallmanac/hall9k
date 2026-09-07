namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The implementer directed what the pull request's reviewer hears about a parked disagreement
/// (<c>h9k review resolve</c>, task: a changes-requested pull-request review from a human becomes
/// a fix lap). Appended by the CLI immediately after it posted — or deliberately did not post —
/// and before the <see cref="ReviewParkResolved"/> that lets the run continue, so the record of
/// what was said always precedes the record of the run moving on.
/// <para>
/// One event per reply, not one per resolve. A park holding two disagreements posts two different
/// drafts to two different places, and a single event carrying one body beside two targets would
/// state that both reviewers read the same words — a fabricated audit fact in exactly the record
/// that exists to prove the platform said only what a human chose. A <c>Nothing</c> choice
/// appends one event with neither, which is the whole of what happened.
/// </para>
/// </summary>
/// <param name="Choice">
/// Which of the three the implementer picked. Never inferred from whether text was supplied — the
/// command requires exactly one of its three options.
/// </param>
/// <param name="PostedBody">
/// The text actually posted, whether drafted or edited, so the record reads back what the
/// reviewer saw rather than pointing at a draft that may since have been edited. Null on
/// <c>Nothing</c>.
/// </param>
/// <param name="PostedTarget">
/// Where it landed: a review thread id for an in-thread reply, or the pull request's own url for a
/// top-level comment answering a review body. Null when nothing was posted — and null on a post
/// that failed, because the resolve refuses rather than recording a reply nobody received.
/// </param>
public sealed record ReviewDisagreementReplyDirected(
    Guid Id,
    ReviewDisagreementReplyChoice Choice,
    string? PostedBody,
    string? PostedTarget,
    DateTimeOffset DirectedAt,
    Guid DirectedByOwnerId);

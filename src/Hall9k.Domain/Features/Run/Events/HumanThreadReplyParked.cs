namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A review-feedback follow-up disposed a thread a PERSON opened as decline or route, drafted the
/// reply, and stopped rather than posting it (task: a review-feedback follow-up never answers a
/// human reviewer in the owner's name on its own). Nothing reached the thread: telling a colleague
/// their point does not hold is a social act the implementer owns, and an agent posting it under
/// the owner's login is the defect this park exists to remove.
/// <para>
/// Origin incidents, both with accurate replies that still had to be deleted or disowned:
/// arx-platform PR #2021 on 2026-09-09, where a follow-up posted roughly 400 words under Brian's
/// login ten minutes after John Mark approved and asked a question, and PR #2042 on 2026-09-15,
/// where a five-paragraph answer to jsmotherman went out the same way. The words were right; the
/// authorship was not, and there was no operator lever between the dispatch and the post.
/// </para>
/// <para>
/// The sibling of <see cref="ReviewDisagreementParked"/> and deliberately not the same event.
/// That one answers a standing CHANGES_REQUESTED review (Decisions Log #152); this one answers a
/// plain thread a person opened, which may sit beside an approval and carries a triage
/// disposition instead of a review url. They share everything downstream — both land in
/// <see cref="RunAggregate.ParkedDisagreements"/>, both set
/// <see cref="RunAggregate.ParkedOnReviewDisagreement"/>, and both are answered with the same
/// three <c>h9k review resolve</c> choices (post as written, post edited text, post nothing) — so
/// the operator learns one lever rather than two. Keeping the events apart is what lets
/// <c>h9k task show</c> render each under an honest heading: a draft parked here belongs under no
/// changes-requested review, because there was none.
/// </para>
/// <para>
/// Appended immediately ahead of the <see cref="ReviewParked"/> that actually parks the run, the
/// same ordering <see cref="ReviewDisagreementParked"/> uses and for the same reason: the park
/// event stays the one thing that moves state, and these drafts are already on the stream by the
/// time anything reads the parked run.
/// </para>
/// </summary>
/// <param name="Drafts">
/// One per human-authored thread the lap declined or routed, carrying the thread id the reply
/// would land in, the disposition, the session's reasoning, and the drafted words nobody has sent.
/// Never empty — <c>RunSupervisor</c> appends nothing when its parse found no draft naming a
/// thread closeout itself observed as human-authored, and the run takes the ordinary
/// thread-dispute park instead.
/// </param>
public sealed record HumanThreadReplyParked(
    Guid Id,
    IReadOnlyList<ReviewDisagreement> Drafts,
    DateTimeOffset ParkedAt);

namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// The reviewer's own verdict, already submitted to GitHub (<c>h9k pr approve</c> /
/// <c>h9k pr request-changes</c>, Decisions Log #149). Recorded AFTER the review is posted, not
/// before: the deliverable is the GitHub review, so this event is the platform's record of
/// something that already happened out there rather than an intention it then tries to carry
/// out — a post that fails leaves no verdict on the stream at all, and the reviewer runs the
/// command again.
/// <para>
/// <see cref="HeadSha"/> is the pull request head the review was submitted against, read live
/// immediately before posting: a review is an opinion about a specific tree, and the head can
/// move while a lap is open. <see cref="ReviewUrl"/> is what GitHub answered with, honestly null
/// when the post succeeded but the response carried no URL to record (AGENTS.md, never guess at
/// unobserved facts).
/// </para>
/// <para>
/// <see cref="Findings"/> is the line comments that went out with a changes-requested review,
/// kept verbatim as the reviewer typed them so the task's own record says what was actually
/// posted. Empty for an approval, which carries only its note.
/// </para>
/// </summary>
public sealed record PullRequestReviewVerdictDelivered(
    Guid Id,
    ReviewerVerdict Verdict,
    string Note,
    IReadOnlyList<string> Findings,
    string HeadSha,
    string? ReviewUrl,
    DateTimeOffset DeliveredAt,
    Guid DeliveredByOwnerId);

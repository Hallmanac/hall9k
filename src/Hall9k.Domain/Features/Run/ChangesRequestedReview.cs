namespace Hall9k.Domain.Features.Run;

/// <summary>
/// One thing a human reviewer's changes-requested review asked for, carried whole so the fix
/// session reads what the reviewer actually wrote rather than a summary of it (task: a
/// changes-requested pull-request review from a human becomes a fix lap).
/// <para>
/// Two shapes reach here, and the difference matters downstream because GitHub treats them
/// differently. An INLINE comment belongs to a review thread, so it has a
/// <see cref="Location"/> the reviewer anchored it at and a <see cref="ThreadId"/> a reply can
/// land inside. The review's own BODY belongs to no thread at all — GitHub makes a body
/// unthreadable — so it carries neither, and an answer to it can only be a top-level comment on
/// the pull request.
/// </para>
/// </summary>
/// <param name="Body">The reviewer's own text, verbatim.</param>
/// <param name="Location">
/// Where the reviewer anchored it, in the same `path/to/file.cs:123` form every platform review
/// finding states (<see cref="ReviewFindingRecord.Location"/>), or the path alone when the
/// provider reported a comment with no line. Null for a review body, and for an inline comment
/// the provider reported without a path — never guessed at (AGENTS.md).
/// </param>
/// <param name="ThreadId">
/// The review thread this comment opened, which is where a reply to it goes. Null for a review
/// body, which has no thread — the repo invariant is that nothing here ever STARTS a thread, so
/// a bodiless answer is a top-level pull-request comment or nothing.
/// </param>
public sealed record ChangesRequestedFinding(string Body, string? Location = null, string? ThreadId = null);

/// <summary>
/// One human reviewer's CHANGES_REQUESTED review, observed by closeout on the pull request's
/// current head (task: a changes-requested pull-request review from a human becomes a fix lap).
/// This is the whole of what the fix lap is answering: the review's own body and every inline
/// comment it left, as <see cref="Findings"/>, in the order the provider reported them.
/// <para>
/// A bot's changes-requested review is deliberately not one of these. Copilot's findings stay on
/// the automated path they have always been on — the thread-based ReviewFeedback follow-up, which
/// disputes, replies and resolves without a human in the loop (Brian's ruling, 2026-09-06 12:15).
/// A disagreement with a PERSON is a social act the implementer owns, which is the whole reason
/// this shape exists separately.
/// </para>
/// </summary>
/// <param name="Reviewer">The provider login the review was authored under.</param>
/// <param name="ReviewUrl">
/// The review itself, as a link — what a park names so the human can open the thing they are
/// being asked about, and the key a parked disagreement names to say which review it answers.
/// </param>
/// <param name="SubmittedAt">
/// GitHub's own submission timestamp, not this install's observation time. Null when the provider
/// did not report one, which reads as unknown rather than as now.
/// </param>
public sealed record ChangesRequestedReview(
    string Reviewer,
    string ReviewUrl,
    DateTimeOffset? SubmittedAt,
    IReadOnlyList<ChangesRequestedFinding> Findings);

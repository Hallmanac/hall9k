namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// One session's own terminal result reported at least one tool call its real permission file
/// refused (security review idea 6be68ee2, process-injection finding 1) — appended alongside that
/// session's own <c>TokensRecorded</c>, never on its own. Read by <c>h9k task show</c> and, for a
/// pr-review run, by the findings report's own per-persona section, so a denial the file's current
/// allow list is missing is visible rather than a silent stall — the same surface that lets the
/// allow list grow by evidence.
/// </summary>
/// <param name="SessionSlug">
/// Which session denied a tool: a persona session's own slug (<c>ReviewPersonaSession.Slug</c>)
/// for a pr-review persona session, or a distinct constant for a mention follow-up
/// (<c>PrReviewEngine.MentionFollowUpSlug</c>) and for an ordinary build's primary session — the
/// key every other reader of this run's persona sections already looks up findings by.
/// </param>
public sealed record RunPermissionDenialsRecorded(
    Guid Id, string SessionSlug, IReadOnlyList<PermissionDenial> Denials, DateTimeOffset RecordedAt);

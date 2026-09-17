namespace Hall9k.Domain.Features.Project;

/// <summary>
/// One prompt builder's current addendum, as this node's own event stream last recorded it
/// (idea b9b09779, piece 6). The ledger file the daemon writes from this — never this node's own
/// projection — is what every prompt builder actually splices in; this is this node's own audit
/// trail of who set it, when, and whether it went in over the size cap.
/// </summary>
public sealed record ProjectPromptAddendum(
    string Content,
    bool OverCap,
    string? OverCapReason,
    DateTimeOffset SetAt,
    Guid SetByOwnerId);

namespace Hall9k.Domain.Features.Epic;

/// <summary>
/// An identity-only pointer to a Jira epic, recorded so a human can click out to it — never a
/// sync (Decisions Log #99: no mirroring, one sanctioned connection, a link).
/// Reference is exactly what was typed, a bare key or a full URL, trimmed and nothing more:
/// no read against Jira ever produces this event, and none ever will.
/// </summary>
public sealed record EpicLinkedToJira(
    Guid Id,
    string Reference,
    DateTimeOffset LinkedAt,
    Guid LinkedByOwnerId);

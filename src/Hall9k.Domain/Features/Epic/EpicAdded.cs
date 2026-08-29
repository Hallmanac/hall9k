namespace Hall9k.Domain.Features.Epic;

/// <summary>
/// An epic named into existence: a business concept worth a name, so future surfaces can
/// search and filter on it (Decisions Log #100). Identity and a title are all it
/// takes — membership is optional and attaches to tasks separately, never demanded here.
/// </summary>
public sealed record EpicAdded(
    Guid Id,
    Guid ProjectId,
    string Title,
    DateTimeOffset AddedAt,
    Guid AddedByOwnerId);

namespace Hall9k.Domain.Features.Epic;

/// <summary>
/// An epic named into existence: a business concept worth a name, so future surfaces can
/// search and filter on it (Decisions Log #100). Identity and a title are all it
/// takes — membership is optional and attaches to tasks separately, never demanded here.
/// <para>
/// A solution epic is one ordinary epic per member project of a solution, each copy carrying the
/// same <see cref="SolutionEpicKey"/> and the key of the <see cref="SolutionKey"/> it was made
/// for. Both are trailing optional fields, so every stream written before them replays unchanged,
/// and a build that does not know them reads each copy as the plain project epic it is. Neither
/// is ever set alone.
/// </para>
/// </summary>
public sealed record EpicAdded(
    Guid Id,
    Guid ProjectId,
    string Title,
    DateTimeOffset AddedAt,
    Guid AddedByOwnerId,
    string? SolutionEpicKey = null,
    string? SolutionKey = null);

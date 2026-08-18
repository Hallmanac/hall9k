using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Project.Events;

public sealed record ProjectSettingsChanged(
    Guid Id,
    Optional<IReadOnlyList<VerifyCommand>> VerifyCommands,
    Optional<bool> SkipPermissions,
    Optional<int> MaxParallelAgents,
    Optional<IReadOnlyList<ContextLink>> ContextLinks,
    DateTimeOffset ChangedAt,
    Guid ChangedByOwnerId,
    Optional<CommitStyle> CommitStyle = default);

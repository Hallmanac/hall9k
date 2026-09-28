using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Trust;

/// <summary>
/// One project member's label, as this project's own trust chain currently shows it (task
/// b7d8222e): their root fingerprint, the node ids of their current fleet (a revoked node already
/// excluded — <c>Hall9k.Connectors.Trust.TrustedOwner.FleetNodeIds</c>'s own doc), their newest
/// display name declaration, and the login of their newest GitHub account declaration (ties broken
/// by the higher node id, since the chain already breaks them that way). Never a trust or
/// cross-check fact — only ever what a surface names a member by.
/// </summary>
public sealed record ProjectMemberLabel(
    string RootFingerprint,
    IReadOnlyList<Guid> FleetNodeIds,
    DisplayName DisplayName,
    string? DeclaredLogin);

/// <summary>
/// This project's own trust chain read, boiled down to the labels a feed, courier, or message
/// surface names a member by (task b7d8222e) — appended only when the set of labels actually
/// changed since the last sweep (<see cref="ProjectMemberLabelsDecider.Observe"/>), the same
/// change-only rule <see cref="Hall9k.Domain.Features.Project.Events.ProjectGitHubCollaboratorsObserved"/>
/// already applies to its own roster. Node-scoped
/// (<see cref="Hall9k.Domain.Infrastructure.Persistence.EventScopeRegistry"/>): a live ledger read
/// per surface would cost a git fetch per project and fail offline, so every node keeps its own
/// copy instead, current as of its own last message sweep, written by the daemon alone — a CLI on
/// a machine whose daemon is down keeps the last recorded labels.
/// </summary>
public sealed record ProjectMemberLabelsObserved(
    Guid ProjectId,
    IReadOnlyList<ProjectMemberLabel> Labels,
    DateTimeOffset ObservedAt);

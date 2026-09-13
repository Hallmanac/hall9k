namespace Hall9k.Domain.Features.Project.Events;

/// <summary>
/// Ends a pending <see cref="ProjectPurgeScheduled"/> before its deadline fires (task: an archived
/// project can be purged). Leaves the project exactly as archiving left it — reversible by
/// <see cref="ProjectReactivated"/> the same as before a purge was ever scheduled — it never
/// reactivates the project itself.
/// </summary>
public sealed record ProjectPurgeCancelled(Guid Id, DateTimeOffset CancelledAt, Guid CancelledByOwnerId);

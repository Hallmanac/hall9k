namespace Hall9k.Domain.Features.Project.Events;

/// <summary>
/// Ends an archive in place, on the same stream and the same id: every task, run, idea, setting,
/// and the recorded home all read exactly as they did before <see cref="ProjectArchived"/>, and the
/// dispatcher's claim sweep and both daemon sweeps (project-home render, auto-pr-review) resume the
/// moment this lands. The home directory on disk is never touched by either event — reactivation
/// only ever clears the projection's own archived flag, so whether the directory still exists is a
/// fact the reactivating command reads and reports, never one this event carries.
/// </summary>
public sealed record ProjectReactivated(Guid Id, DateTimeOffset ReactivatedAt, Guid ReactivatedByOwnerId);

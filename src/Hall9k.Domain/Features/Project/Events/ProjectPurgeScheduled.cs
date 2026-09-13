namespace Hall9k.Domain.Features.Project.Events;

/// <summary>
/// A permanent hard delete scheduled on an archived project (task: an archived project can be
/// purged — the second half of the two-tier project-removal design, PLAN.md §16 #182's purge
/// follow-up; the one explicit exception to the platform's nothing-is-deleted doctrine, ruled by
/// Brian 2026-08-29). <see cref="PurgeAt"/> is the deadline a daemon sweep
/// (<c>Hall9k.Daemon.Purge.ProjectPurgeEngine</c>) checks each tick and on start; <see cref="ScheduledAt"/>
/// is kept only as the record of when the request was made. Cancellable any time before
/// <see cref="PurgeAt"/> by <see cref="ProjectPurgeCancelled"/>, which leaves the project archived —
/// never reactivated. Once the sweep fires, nothing survives to record it: the project's own
/// stream, this event included, is itself part of what gets destroyed, so the daemon's log line is
/// the only surviving record of a completed purge.
/// </summary>
public sealed record ProjectPurgeScheduled(Guid Id, DateTimeOffset ScheduledAt, DateTimeOffset PurgeAt, Guid ScheduledByOwnerId);

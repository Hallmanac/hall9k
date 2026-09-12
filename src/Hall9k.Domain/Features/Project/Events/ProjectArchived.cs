namespace Hall9k.Domain.Features.Project.Events;

/// <summary>
/// A project archived on this install alone (task: a project can be archived, listed as archived,
/// reactivated, and renamed — Decisions Log #PLACEHOLDER-7228d4c7). Reversible by
/// <see cref="ProjectReactivated"/>: nothing here is deleted, the project's stream, tasks, runs,
/// ideas, settings, and recorded home are all untouched, and the home directory on disk is never
/// touched either — only <see cref="Projections.ProjectDetails.IsArchived"/> and
/// <see cref="Projections.ProjectDetails.ArchivedAt"/> change. Named with the purge follow-up (the
/// second half of this design — a <c>--purge</c> flag on <c>h9k project remove</c> that schedules a
/// hard delete after a 24-hour grace period) in mind: that task builds on this event and this field
/// rather than reshaping either.
/// <para>
/// Refused by <see cref="Handlers.ProjectDecider.Archive"/> on a project already archived; refused
/// by <c>h9k project remove</c> itself (not the decider, which is pure and has no database to ask)
/// while any of the project's tasks sits in a state the daemon may still act on — anything other
/// than Draft, Published (always unassigned — Decisions Log #34), Done, or Abandoned.
/// </para>
/// </summary>
public sealed record ProjectArchived(Guid Id, string? Reason, DateTimeOffset ArchivedAt, Guid ArchivedByOwnerId);

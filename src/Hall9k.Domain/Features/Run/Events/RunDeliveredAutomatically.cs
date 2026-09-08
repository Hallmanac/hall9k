namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A deliberate headless start (<c>h9k task start</c>) exited with nobody watching, and the
/// platform found the worktree clean with commits beyond its base branch, so it ran the deliver
/// handoff itself rather than leaving the task reading "building" indefinitely (origin incident
/// 2026-09-05, task ef2fefe5: a clean, committed session sat undelivered for an hour until a
/// human found it by hand). Appended immediately ahead of <see cref="AgentSessionCompleted"/> —
/// the event that actually moves the run into the standard gates/review/pull-request pipeline,
/// exactly as <c>h9k task deliver</c>'s own hand-off already does — so this is purely the marker
/// that distinguishes an automatic delivery from a human's own <c>h9k task deliver</c> on the run
/// stream, not a second way of recording the transition itself.
/// </summary>
public sealed record RunDeliveredAutomatically(Guid Id, DateTimeOffset DeliveredAt, string Reason);

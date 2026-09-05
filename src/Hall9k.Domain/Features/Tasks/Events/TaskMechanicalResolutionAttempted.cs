namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// The daemon spent one unit of a pre-approved task's mechanical-resolution budget (task: a task
/// can be published pre-approved) — a merge attempt that failed for a reason that might clear on
/// its own, tried again without an agent session. A single per-task pool, the same shape
/// <c>CloseoutAttempts</c> already gives ordinary closeout (Decisions Log #22, #80): a lifetime
/// ceiling (<see cref="DaemonOptions.MaxMechanicalResolutionAttempts"/>) rather than a per-
/// obstruction one, since a pre-approved merge attempt that keeps failing mechanically is
/// contested regardless of which reason it failed for each time (design ruling 6). Reset by
/// <see cref="TaskAggregate.ResetAutomaticCloseoutState"/> exactly like <c>CloseoutAttempts</c> —
/// a manual <c>h9k pr resolve</c> refills both pools together.
/// </summary>
public sealed record TaskMechanicalResolutionAttempted(
    Guid Id,
    string Reason,
    DateTimeOffset AttemptedAt);

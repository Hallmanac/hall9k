namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The daemon tried to merge a pre-approved task's pull request itself (task: a task can be
/// published pre-approved) — deterministic daemon code, never an agent (design ruling 8: a rebase
/// merge with linear history). A success is immediately followed, in the same transaction, by the
/// same <see cref="PullRequestMerged"/>/<see cref="RunHandoffRecorded"/>/<see cref="RunCompleted"/>
/// triple an operator's own observed merge produces — this event exists only so the attempt
/// itself, and a failed one's reason, are on the record the way
/// <see cref="PullRequestMechanicalRebaseAttempted"/> already records its own mechanical attempts.
/// A failure is mechanical by construction (GitHub itself refused the merge call), so it costs one
/// unit of the pre-approved task's own mechanical-resolution budget
/// (<c>TaskMechanicalResolutionAttempted</c>) rather than an agent session.
/// </summary>
public sealed record PullRequestAutoMergeAttempted(
    Guid Id,
    bool Succeeded,
    string? FailureReason,
    DateTimeOffset AttemptedAt);

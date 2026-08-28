namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// Starts a run stream that never received its own <see cref="RunDispatched"/> — RunLauncher's
/// declined-dispatch path (a queued follow-up whose pull request was already merged before the
/// run ever spawned, log #26's shape) and CloseoutEngine's orphan sweep (a Done task naming a
/// run id no stream was ever started for, discovered later) both reach here. Everything the
/// run's own dispatch would have observed — worktree, branch, session, model — was never
/// recorded and is deliberately absent from this event rather than guessed at: the never-guess
/// rule (AGENTS.md) applies to a reconstruction exactly as it does to a live record, so the
/// projection leaves those fields at their honest blank/unknown defaults.
/// </summary>
public sealed record RunRecordReconstructed(
    Guid Id,
    Guid TaskId,
    Guid NodeId,
    Guid OwnerId,
    string? PullRequestUrl,
    int? PullRequestNumber,
    DateTimeOffset ReconstructedAt);

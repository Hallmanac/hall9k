namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The narrow rebase-recovery session finished (task: a run rebases its branch onto the current
/// base branch). <see cref="ReviewFixOutcome.Fixed"/> (and <see cref="ReviewFixOutcome.Unknown"/>,
/// when no resolution was declared) records the resolution as a completed
/// <see cref="RunRebasedOntoBase"/> and returns the loop to <see cref="ReviewPhase.Settling"/>,
/// which re-checks the (now rebased) tree and finds nothing further to do — the same "no extra
/// review of its own" guarantee a clean rebase gets, now earned by the agent's own judgment
/// instead of git's automatic apply.
/// <see cref="ReviewFixOutcome.Disputed"/> — the session judged the conflict genuinely
/// undecidable, both sides changing the same behavior rather than just the same lines — parks
/// the run with the conflict named, the same shape a disputed rebase park takes today.
/// </summary>
public sealed record PreFinalPassRebaseRecoveryCompleted(
    Guid Id,
    ReviewFixOutcome Outcome,
    DateTimeOffset CompletedAt);

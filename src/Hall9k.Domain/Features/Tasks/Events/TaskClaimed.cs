namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// A node took responsibility. LeaseGeneration is the fencing token (Decisions Log #7);
/// RunId is minted before the claim so the Task stream carries its run linkage.
/// DependencyOverrideAcknowledged is true whenever a human warned about unmet dependency edges
/// chose to claim anyway (PLAN.md's "the platform advises, the human overrides" ruling,
/// design ruling R7 "edges stay acknowledgment-gated everywhere"): both
/// <see cref="Handlers.TaskDecider.ClaimDeliberately"/>'s Blocked-entry branch (task 8a56af78-h9k,
/// "a deliberate human kick-off") and <see cref="Handlers.TaskDecider.ClaimInteractively"/>'s own
/// Blocked-entry branch (task 0ac72cb8-h9k) can record it — false for a plain node
/// <see cref="Handlers.TaskDecider.Claim"/>, which can never be claimed with dependencies still
/// open. DependencyOverrideCarriedForward is true when this claim relied on an acknowledgment
/// already recorded by an earlier claim on this same task (still-open blockers unchanged since),
/// rather than a fresh one given right now — the caller decides which is true before building this
/// event, since only it knows whether <c>--acknowledge-unmet-dependencies</c> was passed or the
/// aggregate's own carried-forward record already covered the still-open set. False whenever
/// DependencyOverrideAcknowledged is false.
/// </summary>
/// <param name="InteractiveMode">
/// True when this claim is the human's own hands-on-the-wheel act (task: interactive mode
/// becomes a recorded property of the task, design ruling R2) — set unconditionally by
/// <see cref="Handlers.TaskDecider.ClaimInteractively"/> (h9k task work) and, only when the CLI
/// caller says this is a human's own deliberate kick-off rather than an automated one, by
/// <see cref="Handlers.TaskDecider.ClaimDeliberately"/> (h9k task start; false for
/// <c>AutoPrReviewEngine</c>'s own automated "now"-speed claim on the identical decider method).
/// It only ever turns <see cref="TaskAggregate.InteractiveModeEnabled"/> on — a plain node
/// <see cref="Handlers.TaskDecider.Claim"/>, or a reclaim through either decider without this
/// flag, never turns it back off; only <c>h9k task handback</c> (design ruling R9) and a default
/// <c>h9k task release</c> (design ruling R6, amended 2026-09-05) do that.
/// </param>
public sealed record TaskClaimed(
    Guid Id,
    Guid NodeId,
    Guid OwnerId,
    int LeaseGeneration,
    Guid RunId,
    DateTimeOffset ClaimedAt,
    bool DependencyOverrideAcknowledged = false,
    bool DependencyOverrideCarriedForward = false,
    bool InteractiveMode = false);

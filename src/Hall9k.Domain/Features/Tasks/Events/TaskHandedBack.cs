namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// An operator working a task interactively (h9k task work) handed it back to a headless
/// agent partway through (h9k task handback): the human's claim releases, the task returns to
/// Queued through normal dispatch, and Branch is what the next claim resumes — mechanically the
/// existing follow-up resume-existing-branch flow (RunLauncher.CheckoutFreshOrRetryAsync reads
/// TaskAggregate.RetryBranch exactly as a human-requested retry's does), so a headless run
/// continues from the branch state an operator started rather than cutting a fresh one.
/// </summary>
public sealed record TaskHandedBack(
    Guid Id, Guid RunId, string Branch, string? Reason, DateTimeOffset HandedBackAt, Guid HandedBackByOwnerId);

namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A human granted the closeout monitor a fresh automatic budget for this pull request
/// (`h9k pr resolve`, Decisions Log #22/#77, backlog 45). The reset itself lands on the
/// task stream as `TaskReopened(Automatic: false)`; this is the same grant recorded on
/// the run the human resolved, so the run's own history shows a human touched it without
/// sending a reader to the task stream to find out why the budget is full again.
/// </summary>
public sealed record CloseoutBudgetGranted(Guid Id, string? Reason, DateTimeOffset GrantedAt);

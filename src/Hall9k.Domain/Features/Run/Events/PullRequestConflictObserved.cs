namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The closeout monitor observed the run's pull request reporting GitHub's CONFLICTING
/// mergeable state against its base branch — an observation, never an inference from
/// staleness (backlog 44): the provider's own <c>mergeable</c> field is what is recorded,
/// not a guess from how long the branch has sat open. A rebase follow-up dispatch (or a
/// CloseoutParked, when the automatic budget is spent) is appended in the same transaction,
/// exactly like <see cref="PullRequestChecksFailed"/>.
/// </summary>
public sealed record PullRequestConflictObserved(Guid Id, DateTimeOffset ObservedAt);

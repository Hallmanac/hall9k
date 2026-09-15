namespace Hall9k.Domain.Features.Run.Events;

/// <param name="OpenedAgainstBaseBranch">
/// Non-null exactly in <c>PullRequestOpener.ResolveOpenBaseAsync</c>'s own fallback shape: this
/// run's own recorded base named a stacked parent branch already gone from origin, so this pull
/// request opened against the project's base instead — carried here so
/// <see cref="Hall9k.Domain.Features.Run.Projections.RunDetails.OpenedAgainstBaseBranch"/> answers
/// a later reader without a live GitHub call (see that field's own doc). Null for every ordinary
/// open and for a stream written before this field existed.
/// </param>
public sealed record PullRequestOpened(
    Guid Id,
    string PullRequestUrl,
    int PullRequestNumber,
    DateTimeOffset OpenedAt,
    string? OpenedAgainstBaseBranch = null);

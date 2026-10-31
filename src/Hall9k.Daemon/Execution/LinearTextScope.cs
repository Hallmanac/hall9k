namespace Hall9k.Daemon.Execution;

/// <summary>
/// What the Linear guard needs to compose a pull request's text on a Linear-tracked project: the
/// identifier of the task's own issue, whose mention the guard leaves alone, and the team keys the
/// install's connection can see. A null <paramref name="TeamKeys"/> is a read that failed or was not
/// possible, and the guard then matches by shape and skips the title rule.
/// </summary>
internal sealed record LinearTextScope(string? OwnIdentifier, IReadOnlySet<string>? TeamKeys);

/// <summary>
/// The same facts before the team keys are read. The read is a network call, so it is a delegate the
/// composer runs only when the text holds a key-shaped token other than the task's own, which is why
/// a pull request that names only its own issue never makes it. It reports failure as null and never
/// throws for a tracker reason (<see cref="Hall9k.Connectors.WorkItems.LinearTeamKeys"/>).
/// </summary>
internal sealed record LinearTextScopeSource(
    string? OwnIdentifier, Func<CancellationToken, Task<IReadOnlySet<string>?>> ReadTeamKeysAsync);

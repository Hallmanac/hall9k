namespace Hall9k.Connectors.Worktrees;

/// <summary>
/// Whether git's own error text for a failed fetch reads as a passing network failure, the kind a
/// retry a few minutes later can be expected to outlast, rather than a refusal waiting will not
/// change. A closed, named list matched against the text, because git reports no machine-readable
/// cause: the phrases are what git and its transports actually print for an unreachable host, and
/// anything not on the list (<c>couldn't find remote ref</c>, <c>Authentication failed</c>, a
/// repository that is not found) is treated as final and fails the task at once, as it always did.
/// <para>
/// Origin incident (2026-10-03, task 51939c4c in arx-platform): a launch failed outright because
/// <c>git fetch</c> of the pull request ref reported <c>Could not resolve host: github.com</c> on a
/// flaky Parallels link, while the daemon's own database connection recovered by itself.
/// </para>
/// </summary>
public static class GitNetworkFailure
{
    private static readonly string[] TransientPhrases =
    [
        "could not resolve host",
        "failed to connect to",
        "connection timed out",
        "operation timed out",
        "connection refused",
        "connection reset",
        "connection was reset",
        "network is unreachable",
    ];

    public static bool IsTransient(string? gitError) =>
        gitError is { Length: > 0 }
        && TransientPhrases.Any(phrase => gitError.Contains(phrase, StringComparison.OrdinalIgnoreCase));
}

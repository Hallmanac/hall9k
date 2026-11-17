namespace Hall9k.Daemon.ClaudeAccounts;

/// <summary>
/// The pure half of the per-account gate on new work: which projects a refused account holds, and which
/// candidates are left once they are taken out. Work for unbound projects and for other accounts passes.
/// </summary>
public static class ClaudeAccountRefusals
{
    /// <summary>The projects whose binding names a refused connection.</summary>
    public static IReadOnlySet<Guid> RefusedProjects(
        IEnumerable<(Guid ProjectId, Guid? ConnectionId)> bindings, IReadOnlySet<Guid> refusedConnections) =>
        bindings
            .Where(binding => binding.ConnectionId is { } connectionId && refusedConnections.Contains(connectionId))
            .Select(binding => binding.ProjectId)
            .ToHashSet();

    /// <summary><paramref name="candidates"/> without those whose project is in <paramref name="refusedProjects"/>, in order.</summary>
    public static IReadOnlyList<T> Admitted<T>(
        IReadOnlyList<T> candidates, Func<T, Guid> projectOf, IReadOnlySet<Guid> refusedProjects) =>
        refusedProjects.Count == 0
            ? candidates
            : [.. candidates.Where(candidate => !refusedProjects.Contains(projectOf(candidate)))];
}

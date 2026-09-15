using Hall9k.Connectors.Processes;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Shared.Exceptions;
using Marten;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// The platform's real <see cref="ProcessRunner"/> (idea 202383dc, A2b item 4): identical to
/// <see cref="ExternalProcess.Runner"/> for every tool except <c>gh</c>, where
/// <paramref name="workingDirectory"/> — already the registered project's own repository path at
/// every existing call site, since that is what every one of them already passes gh's working
/// directory as — is matched back to that project and the call is pinned to its own GitHub
/// account through <see cref="ProjectGitHubClient"/>, never whichever account the machine's own
/// <c>gh</c> happens to be logged into.
/// <para>
/// This is what nearly every GitHub connector class in the platform already threads a
/// <see cref="ProcessRunner"/> through today (<c>GitHubWorkItemProvider</c>,
/// <c>GitHubReviewAssignments</c>, <c>TrackerClaimGate</c>, and the rest) — so wiring this in at
/// the one place each of them is constructed for real (a CLI command's own <c>ExecuteAsync</c>,
/// or the daemon's single <see cref="ProcessRunner"/> DI registration) is the whole migration.
/// Nothing about any of those classes' own constructors, method shapes, or test seams changes: a
/// test that already passes a fake <see cref="ProcessRunner"/> straight to one of them never
/// reaches this class at all.
/// </para>
/// </summary>
public sealed class ProjectScopedGitHubRunner(
    IDocumentStore store, ProjectGitHubClient? client = null, ProcessRunner? passthrough = null)
{
    private readonly ProjectGitHubClient client = client ?? new ProjectGitHubClient();
    private readonly ProcessRunner passthrough = passthrough ?? ExternalProcess.Runner;

    public ProcessRunner Runner => RunAsync;

    private async Task<ProcessResult> RunAsync(
        string fileName, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        if (!string.Equals(fileName, "gh", StringComparison.Ordinal))
        {
            return await passthrough(fileName, arguments, workingDirectory, cancellationToken);
        }

        await using IQuerySession session = store.QuerySession();
        ProjectDetails project = await session.Query<ProjectDetails>()
            .FirstOrDefaultAsync(candidate => candidate.RepositoryPath == workingDirectory, cancellationToken)
            ?? throw new DomainNotFoundException(
                $"No registered project has its repository at {workingDirectory}, so there is no project "
                + "account to run gh as.");

        ProjectGitHubAccount account = await ProjectGitHubClient.ResolveAccountAsync(session, project, cancellationToken);
        return await client.RunAsync(account, workingDirectory, arguments, cancellationToken);
    }
}

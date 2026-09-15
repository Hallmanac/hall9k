using Hall9k.Connectors.Processes;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
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
    IDocumentStore store, ProjectGitHubClient? client = null, ProcessRunner? passthrough = null, TimeProvider? clock = null,
    Func<string, GhIdentityReader>? identityReaderFactory = null)
{
    /// <summary>
    /// How long a resolved account is reused for the same working directory before this runner
    /// re-queries the project and re-resolves its account — short enough that a project's account
    /// changing (a re-registered connection, a different login) is picked up quickly, long enough
    /// that a closeout sweep touching several pull requests for the same project opens the Postgres
    /// query session once rather than once per gh call (independent pre-PR review, cycle 1,
    /// conformance lens).
    /// </summary>
    private static readonly TimeSpan AccountCacheTtl = TimeSpan.FromMinutes(2);

    private readonly ProjectGitHubClient client = client ?? new ProjectGitHubClient();
    private readonly ProcessRunner passthrough = passthrough ?? ExternalProcess.Runner;
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly Func<string, GhIdentityReader> identityReaderFactory =
        identityReaderFactory ?? (workingDirectory => ProjectGitHubClient.AmbientIdentityReader(workingDirectory));
    private readonly object accountCacheGate = new();
    private readonly Dictionary<string, (ProjectGitHubAccount Account, DateTimeOffset ExpiresAt)> accountCache =
        new(StringComparer.Ordinal);

    public ProcessRunner Runner => RunAsync;

    private async Task<ProcessResult> RunAsync(
        string fileName, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        if (!string.Equals(fileName, "gh", StringComparison.Ordinal))
        {
            return await passthrough(fileName, arguments, workingDirectory, cancellationToken);
        }

        ProjectGitHubAccount account = await ResolveCachedAccountAsync(workingDirectory, cancellationToken);
        return await client.RunAsync(account, workingDirectory, arguments, cancellationToken);
    }

    private async Task<ProjectGitHubAccount> ResolveCachedAccountAsync(
        string workingDirectory, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        lock (accountCacheGate)
        {
            if (accountCache.TryGetValue(workingDirectory, out (ProjectGitHubAccount Account, DateTimeOffset ExpiresAt) cached)
                && cached.ExpiresAt > now)
            {
                return cached.Account;
            }
        }

        ProjectDetails project;
        await using (IQuerySession session = store.QuerySession())
        {
            project = await session.Query<ProjectDetails>()
                .FirstOrDefaultAsync(candidate => candidate.RepositoryPath == workingDirectory, cancellationToken)
                ?? throw new DomainNotFoundException(
                    $"No registered project has its repository at {workingDirectory}, so there is no project "
                    + "account to run gh as.");
        }

        ProjectGitHubAccount account = await ResolveAccountRefreshingIfUnconfirmedAsync(project, workingDirectory, cancellationToken);

        lock (accountCacheGate)
        {
            accountCache[workingDirectory] = (account, now + AccountCacheTtl);
        }

        return account;
    }

    /// <summary>
    /// Resolves the project's account, and on the one confirmable gap — this install's connection
    /// was registered before its GitHub identity was ever observed, which happens only at
    /// <c>h9k project add</c>, <c>h9k project join</c>, and daemon start — refreshes that identity
    /// live and resolves once more before giving up. Without this, an install upgraded onto this
    /// migration whose daemon has not yet restarted would have every one of these ordinary CLI
    /// commands start refusing with "no confirmed GitHub account" where the exact same command
    /// worked a moment ago against ambient <c>gh</c> (independent pre-PR review, Copilot,
    /// PR #399). Best-effort exactly like <see cref="NodeBootstrap.RefreshGitHubIdentityAsync"/>
    /// itself: a gh that still cannot answer leaves the original refusal to propagate unchanged.
    /// </summary>
    private async Task<ProjectGitHubAccount> ResolveAccountRefreshingIfUnconfirmedAsync(
        ProjectDetails project, string workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            await using IQuerySession session = store.QuerySession();
            return await ProjectGitHubClient.ResolveAccountAsync(session, project, cancellationToken);
        }
        catch (DomainValidationException)
        {
            GhIdentityReader ghIdentityReader = identityReaderFactory(workingDirectory);
            await using IDocumentSession refreshSession = store.LightweightSession();
            if (!await NodeBootstrap.RefreshGitHubIdentityAsync(
                refreshSession, project.ConnectionId, cancellationToken, ghIdentityReader))
            {
                throw;
            }

            await refreshSession.SaveChangesAsync(cancellationToken);

            await using IQuerySession retrySession = store.QuerySession();
            return await ProjectGitHubClient.ResolveAccountAsync(retrySession, project, cancellationToken);
        }
    }
}

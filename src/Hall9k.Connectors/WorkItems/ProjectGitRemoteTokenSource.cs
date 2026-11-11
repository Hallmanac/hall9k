using Hall9k.Connectors.Processes;
using Hall9k.Domain.Features.Project.Projections;
using Marten;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// The real <see cref="IGitRemoteTokenSource"/>: the GitHub token of the account a project's
/// repository belongs to, for a git call that reaches that repository's remote.
/// <para>
/// The call's working directory is matched back to the registered project the same way
/// <see cref="ProjectScopedGitHubRunner"/> matches a <c>gh</c> call, widened to the directories git
/// itself is run in: a worktree is followed through its <c>.git</c> pointer file to the repository
/// it was cut from (a file read, never a process). The account is the project's registered
/// connection's confirmed identity (<see cref="ProjectGitHubClient.TryResolveAccountAsync"/>), and
/// the token is read through the one <see cref="ProjectGitHubClient"/> this source is built with,
/// so its cache holds across calls and <c>gh auth token</c> is not started once per git call.
/// </para>
/// <para>
/// A token is handed out only when the repository's <c>origin</c> is on https://github.com/
/// (read with <c>git remote get-url origin</c>, which applies <c>insteadOf</c> rewrites). A
/// GitHub Enterprise host, another provider or an ssh URL gets nothing, and the owner's credential
/// helper runs for it as it always has. Every failure to resolve any of this is the same null:
/// the git call then runs without the keys, as it does today. The token is never logged and never
/// part of an exception message; nothing here logs at all.
/// </para>
/// </summary>
public sealed class ProjectGitRemoteTokenSource(
    GitRepositoryAccountResolver resolveAccount, ProjectGitHubClient client, ProcessRunner? git = null,
    TimeProvider? clock = null)
    : IGitRemoteTokenSource
{
    /// <summary>A source that finds each repository's project in <paramref name="store"/>.</summary>
    public ProjectGitRemoteTokenSource(IDocumentStore store, ProjectGitHubClient client)
        : this((candidates, cancellationToken) => AccountInStoreAsync(store, candidates, cancellationToken), client)
    {
    }

    /// <summary>How long a resolved account and remote are reused for one working directory: long enough that a sync loop's fetches open one Postgres session and start one <c>git remote get-url</c>, short enough that a re-registered project or changed remote is noticed.</summary>
    private static readonly TimeSpan ResolutionTtl = TimeSpan.FromMinutes(2);

    /// <summary>A directory that resolved to nothing (no project, no confirmed account, a remote elsewhere) is looked at again sooner, so a project registered a moment ago does not wait out <see cref="ResolutionTtl"/>.</summary>
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(30);

    private readonly ProcessRunner git = git ?? ExternalProcess.Runner;
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<string, (string? Login, DateTimeOffset ExpiresAt)> resolutions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> tokenFailures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> redirects = new(StringComparer.Ordinal);

    public async Task<GitRemoteAuthorization?> TryGetAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        if (RecentlyRedirected(workingDirectory))
        {
            return null;
        }

        try
        {
            string? login = await ResolveLoginAsync(workingDirectory, cancellationToken);
            if (login is null || TokenRecentlyFailed(login))
            {
                return null;
            }

            try
            {
                return new GitRemoteAuthorization(await client.TokenAsync(login, workingDirectory, cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // gh cannot produce this account's token (logged out, or confirmed through a
                // GH_TOKEN the daemon no longer has). Remembered for NegativeTtl, so while that holds
                // a remote git call costs no extra gh process on top of the owner's helper.
                lock (gate)
                {
                    tokenFailures[login] = clock.GetUtcNow() + NegativeTtl;
                }

                return null;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // No token can be had (the database was unreachable): the call runs the way it always
            // has. The message is deliberately not kept: it is the only thing that could carry gh's
            // output, and there is nothing for a caller to do with it.
            return null;
        }
    }

    public void Invalidate(string workingDirectory)
    {
        string? login;
        lock (gate)
        {
            login = resolutions.TryGetValue(workingDirectory, out (string? Login, DateTimeOffset ExpiresAt) found)
                ? found.Login
                : null;
        }

        if (login is not null)
        {
            lock (gate)
            {
                tokenFailures.Remove(login);
            }

            client.InvalidateToken(login);
        }
    }

    /// <summary>
    /// Kept for <see cref="ResolutionTtl"/>: a remote that redirects (a renamed repository) keeps
    /// doing so, and while it is remembered its calls run once without the keys rather than failing
    /// with them first.
    /// </summary>
    public void RecordRedirect(string workingDirectory)
    {
        lock (gate)
        {
            redirects[workingDirectory] = clock.GetUtcNow() + ResolutionTtl;
        }
    }

    private bool RecentlyRedirected(string workingDirectory)
    {
        DateTimeOffset now = clock.GetUtcNow();
        lock (gate)
        {
            return redirects.TryGetValue(workingDirectory, out DateTimeOffset until) && until > now;
        }
    }

    private bool TokenRecentlyFailed(string login)
    {
        DateTimeOffset now = clock.GetUtcNow();
        lock (gate)
        {
            return tokenFailures.TryGetValue(login, out DateTimeOffset until) && until > now;
        }
    }

    private async Task<string?> ResolveLoginAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        lock (gate)
        {
            if (resolutions.TryGetValue(workingDirectory, out (string? Login, DateTimeOffset ExpiresAt) cached)
                && cached.ExpiresAt > now)
            {
                return cached.Login;
            }
        }

        string? login;
        try
        {
            login = await ResolveUncachedAsync(workingDirectory, cancellationToken);
        }
        // An unreachable database or a git that will not run is the same answer as no project: no
        // token, remembered for NegativeTtl, so a store that is down costs one failed lookup per
        // directory per half minute rather than one per git call.
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            login = null;
        }

        lock (gate)
        {
            resolutions[workingDirectory] = (login, now + (login is null ? NegativeTtl : ResolutionTtl));
        }

        return login;
    }

    private async Task<string?> ResolveUncachedAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        ProjectGitHubAccount? account =
            await resolveAccount(GitRepositoryLocator.Candidates(workingDirectory), cancellationToken);
        if (account is null)
        {
            return null;
        }

        ProcessResult remote = await git("git", ["remote", "get-url", "origin"], workingDirectory, cancellationToken);
        return remote.ExitCode == 0 && IsGitHubHttps(remote.StandardOutput.Trim()) ? account.Login : null;
    }

    private static async Task<ProjectGitHubAccount?> AccountInStoreAsync(
        IDocumentStore store, IReadOnlyList<string> candidates, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();
        foreach (string candidate in candidates)
        {
            ProjectDetails? project = await session.Query<ProjectDetails>()
                .FirstOrDefaultAsync(details => details.RepositoryPath == candidate, cancellationToken);
            if (project is not null)
            {
                return await ProjectGitHubClient.TryResolveAccountAsync(session, project, cancellationToken);
            }
        }

        return null;
    }

    /// <summary>Exactly the scope the keys are written for: https, host github.com, no user, no port.</summary>
    internal static bool IsGitHubHttps(string url) =>
        url.StartsWith(GitRemoteAuthorization.GitHubScopeUrl, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Finds the directories a registered project's <c>RepositoryPath</c> can be, starting from one git
/// is run in: the directory itself, and, from the nearest <c>.git</c> above it, the repository it
/// belongs to. A linked worktree's <c>.git</c> is a file holding <c>gitdir: &lt;repo&gt;/worktrees/&lt;name&gt;</c>,
/// which names the repository without starting git.
/// </summary>
internal static class GitRepositoryLocator
{
    public static IReadOnlyList<string> Candidates(string workingDirectory)
    {
        List<string> found = [];
        Add(found, workingDirectory);

        string? directory = Path.GetFullPath(workingDirectory);
        while (directory is not null)
        {
            string dotGit = Path.Combine(directory, ".git");
            if (File.Exists(dotGit))
            {
                if (ReadGitDirectory(dotGit, directory) is { } gitDirectory
                    && ContainingRepository(gitDirectory) is { } repository)
                {
                    Add(found, repository);
                    if (string.Equals(Path.GetFileName(repository), ".git", StringComparison.Ordinal)
                        && Path.GetDirectoryName(repository) is { } checkout)
                    {
                        Add(found, checkout);
                    }
                }

                Add(found, directory);
                break;
            }

            if (Directory.Exists(dotGit)
                || (File.Exists(Path.Combine(directory, "HEAD")) && Directory.Exists(Path.Combine(directory, "objects"))))
            {
                Add(found, directory);
                break;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return found;
    }

    private static string? ReadGitDirectory(string dotGitFile, string directory)
    {
        try
        {
            foreach (string line in File.ReadLines(dotGitFile))
            {
                if (line.StartsWith("gitdir:", StringComparison.Ordinal))
                {
                    string target = line["gitdir:".Length..].Trim();
                    return Path.GetFullPath(target, directory);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An unreadable pointer is a directory that resolves to no project, which is the answer.
        }

        return null;
    }

    /// <summary>The repository above <c>&lt;repo&gt;/worktrees/&lt;name&gt;</c>, or null when the path is not shaped that way.</summary>
    private static string? ContainingRepository(string gitDirectory)
    {
        string? worktrees = Path.GetDirectoryName(gitDirectory);
        return worktrees is not null
            && string.Equals(Path.GetFileName(worktrees), "worktrees", StringComparison.Ordinal)
                ? Path.GetDirectoryName(worktrees)
                : null;
    }

    /// <summary>Adds a path as it is, in the platform's own separators, and in the other separator, since a project's path is stored as typed at registration.</summary>
    private static void Add(List<string> found, string path)
    {
        string trimmed = path.Length > 1 ? path.TrimEnd('/', '\\') : path;
        foreach (string variant in new[] { trimmed, trimmed.Replace('\\', '/'), trimmed.Replace('/', '\\') })
        {
            if (!found.Contains(variant, StringComparer.Ordinal))
            {
                found.Add(variant);
            }
        }
    }
}

/// <summary>
/// The confirmed GitHub account of the registered project whose repository is at one of the
/// <paramref name="candidates"/> (most specific first), or null when there is no such project or it
/// has no confirmed account.
/// </summary>
public delegate Task<ProjectGitHubAccount?> GitRepositoryAccountResolver(
    IReadOnlyList<string> candidates, CancellationToken cancellationToken);

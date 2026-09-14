using Hall9k.Connectors.Processes;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;

namespace Hall9k.Connectors.WorkItems;

/// <summary>The confirmed GitHub account a project's repository access runs as — GitHub's own numeric id alongside the login it currently names.</summary>
public sealed record ProjectGitHubAccount(long Id, string Login);

/// <summary>
/// Runs <c>gh</c> as a specific project's own GitHub account — never whichever account the
/// machine's <c>gh</c> happens to be logged into right now (idea 202383dc, A2b, item 4). The
/// account is the project's registered GitHub connection's own confirmed identity
/// (<see cref="ConnectionDetails.GitHubAccountId"/>/<see cref="ConnectionDetails.GitHubLogin"/>,
/// observed together by the same GitHub read — never the registration-time
/// <see cref="ConnectionDetails.ExternalAccountId"/> placeholder, which can go stale the moment
/// the machine's <c>gh</c> logs into a different login than the one the connection first registered
/// under); the token for it is read fresh, per call, with <c>gh auth token --user &lt;login&gt;</c> — live
/// as of gh 2.100 (confirmed 2026-09-12): it hands out a named account's token without touching
/// the machine's own <c>gh auth</c> selection — and passed to the wrapped command as the
/// <c>GH_TOKEN</c> environment variable for that one invocation alone. Nothing here runs
/// <c>gh auth switch</c> or otherwise mutates the machine's login.
/// <para>
/// This is the one gh helper A2b's own calls (the push check, the access mirror) go through.
/// Migrating the platform's other 17 direct <c>gh</c> call sites across 11 files onto it is its
/// own, later, unstacked task (idea 202383dc, A2b item 4) — they are left exactly as they are for
/// now. Account switching as a first-class feature, letting one owner's install hold two accounts
/// side by side for every command, is parked (trigger: "first owner needing two accounts on one
/// machine").
/// </para>
/// </summary>
public sealed class ProjectGitHubClient(EnvironmentProcessRunner? runner = null, ProcessRunner? tokenRunner = null)
{
    private readonly EnvironmentProcessRunner runner = runner ?? ExternalProcess.RunnerWithEnvironment;
    private readonly ProcessRunner tokenRunner = tokenRunner ?? ExternalProcess.Runner;

    /// <summary>
    /// Runs one <c>gh</c> command from <paramref name="workingDirectory"/>, authenticated as
    /// <paramref name="account"/> alone. Split from account resolution (<see cref="ResolveAccountAsync"/>)
    /// so the actual gh invocation — pinning the token, setting the environment — is testable
    /// against an in-memory fake with no Marten session in the loop at all; a caller that already
    /// knows the account (<see cref="ProjectGitHubAccessMirror"/>, chiefly) calls this directly.
    /// </summary>
    public async Task<ProcessResult> RunAsync(
        ProjectGitHubAccount account, string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        string token = await TokenAsync(account.Login, workingDirectory, cancellationToken);
        return await runner(
            "gh", arguments, workingDirectory,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["GH_TOKEN"] = token },
            cancellationToken);
    }

    /// <summary>
    /// The account this project's own repository access runs as — its registered GitHub
    /// connection's account, confirmed by a real GitHub identity read (never the
    /// <c>Environment.UserName</c> placeholder <c>NodeBootstrap</c> falls back to when <c>gh</c>
    /// could not answer). Public so a caller that only needs the account, not a live gh call (a
    /// refusal message naming it), does not have to reimplement this resolution.
    /// </summary>
    public static async Task<ProjectGitHubAccount> ResolveAccountAsync(
        IQuerySession session, ProjectDetails project, CancellationToken cancellationToken)
    {
        ConnectionDetails? connection = await session.LoadAsync<ConnectionDetails>(project.ConnectionId, cancellationToken);
        return connection is { } found
            && found.Provider == WorkItemProvider.GitHub
            && found.GitHubAccountId is { } accountId
            && found.GitHubLogin.IsNotBlank()
                ? new ProjectGitHubAccount(accountId, found.GitHubLogin)
                : throw new DomainValidationException(
                    $"Project '{project.Name}' has no confirmed GitHub account to act as — gh reported no "
                    + "login for this install's own connection (h9k connection list shows what is "
                    + "registered, but only ever what was already recorded; it never calls gh). Run 'gh "
                    + "auth login' (gh auth status confirms it), then retry h9k project join.");
    }

    private async Task<string> TokenAsync(string login, string workingDirectory, CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await tokenRunner("gh", ["auth", "token", "--user", login], workingDirectory, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new DomainValidationException(
                $"gh could not be run to produce a token for '{login}' (gh auth token --user {login}): "
                + $"{exception.Message}");
        }

        string token = result.StandardOutput.Trim();
        if (result.ExitCode != 0 || token.IsBlank())
        {
            throw new DomainValidationException(
                $"gh could not produce a token for '{login}' (gh auth token --user {login}): "
                + $"{result.StandardError.Trim()}. Check 'gh auth status' names this account as logged in "
                + "on this machine.");
        }

        return token;
    }
}

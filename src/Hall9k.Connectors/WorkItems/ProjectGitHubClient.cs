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
/// under). The token is read the same way the identity itself was confirmed: <c>GH_TOKEN</c>, then
/// <c>GITHUB_TOKEN</c> — gh's own ambient-environment precedence, and the identical order
/// <c>NodeBootstrap</c>'s own <c>gh api user</c> read already resolves through, since that read is
/// what actually confirmed <paramref name="account"/>'s own login in the first place. Only when
/// neither is set does this fall back to <c>gh auth token --user &lt;login&gt;</c> — live as of gh
/// 2.100 (confirmed 2026-09-12): it hands out a named account's token without touching the
/// machine's own <c>gh auth</c> selection, but it reads only the keyring and gh's own config file
/// and never the environment, so on an install signed in purely through an exported token (a
/// documented gh auth mode, and the only one on some headless setups) with no matching keyring
/// entry, that call alone could never produce a token for a login the ambient environment had just
/// confirmed (independent pre-PR review, cycle 1, adversarial lens, medium). The resolved token is
/// passed to the wrapped command as the <c>GH_TOKEN</c> environment variable for that one
/// invocation alone. Nothing here runs <c>gh auth switch</c> or otherwise mutates the machine's
/// login.
/// <para>
/// This is the one gh helper A2b's own calls (the push check, the access mirror) go through.
/// Migrating the platform's other 17 direct <c>gh</c> call sites across 11 files onto it is its
/// own, later, unstacked task (idea 202383dc, A2b item 4) — they are left exactly as they are for
/// now. Account switching as a first-class feature, letting one owner's install hold two accounts
/// side by side for every command, is parked (trigger: "first owner needing two accounts on one
/// machine").
/// </para>
/// </summary>
public sealed class ProjectGitHubClient(
    EnvironmentProcessRunner? runner = null, ProcessRunner? tokenRunner = null, Func<string, string?>? environmentVariable = null)
{
    private readonly EnvironmentProcessRunner runner = runner ?? ExternalProcess.RunnerWithEnvironment;
    private readonly ProcessRunner tokenRunner = tokenRunner ?? ExternalProcess.Runner;
    private readonly Func<string, string?> environmentVariable = environmentVariable ?? Environment.GetEnvironmentVariable;

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
        try
        {
            return await runner(
                "gh", arguments, workingDirectory,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["GH_TOKEN"] = token },
                cancellationToken);
        }
        // The token read above already wraps its own gh call this way; the actual command run here
        // never did, so a gh hung on the network answered with an unhandled TimeoutException (or
        // ProcessOutputStuckException, which derives from it) all the way out of both
        // ProjectGitHubAccessMirror.ObserveAsync and standalone h9k project join — the one CLI
        // command with no catch of its own for either type, printing a stack trace instead of the
        // reason on stderr AGENTS.md's CLI standard requires (independent pre-PR review, cycle 1,
        // conformance and adversarial lenses, both low). DomainValidationException is caught
        // globally in Program.cs, the same as the token-read failure just above.
        catch (TimeoutException exception)
        {
            throw new DomainValidationException(
                $"gh {string.Join(' ', arguments)} did not answer from {workingDirectory}: {exception.Message}");
        }
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
        if (AmbientToken() is { } ambient)
        {
            return ambient;
        }

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

    /// <summary>
    /// gh's own ambient-token precedence, checked in the identical order gh itself checks it before
    /// ever consulting the keyring or its config file — <c>GH_TOKEN</c>, then <c>GITHUB_TOKEN</c> —
    /// so a call here reads the exact token <c>NodeBootstrap</c>'s own <c>gh api user</c> read would
    /// have used to confirm this account's login a moment earlier. Login-blind by design: an
    /// install with more than one account active only through ambient tokens rather than the
    /// keyring is the account-switching feature this task's own decision log entry parks, not
    /// something this helper resolves on its own.
    /// </summary>
    private string? AmbientToken() =>
        environmentVariable("GH_TOKEN") is { Length: > 0 } ghToken
            ? ghToken
            : environmentVariable("GITHUB_TOKEN") is { Length: > 0 } githubToken
                ? githubToken
                : null;
}

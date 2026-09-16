using Hall9k.Connectors.Processes;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
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
/// This is the platform's one gh helper. Every direct <c>gh</c> call site the idea's own re-review
/// counted (idea 202383dc, A2b item 4) is migrated onto it — through <c>ProjectScopedGitHubRunner</c>
/// for the dozen connector and command call sites that already have a registered project's account
/// to pin, and through this class's own ambient mode (<see cref="RunAmbientAsync"/>,
/// <see cref="AmbientProcessRunner"/>, <see cref="AmbientIdentityReader"/>) for the handful — the
/// release-download update check, bootstrap's own identity read, <c>h9k doctor</c>'s presence probe
/// — that run before any project, or any account to pin, exists. Account switching as a first-class
/// feature, letting one owner's install hold two accounts side by side for every command, is parked
/// (trigger: "first owner needing two accounts on one machine").
/// </para>
/// </summary>
public sealed class ProjectGitHubClient(
    EnvironmentProcessRunner? runner = null, ProcessRunner? tokenRunner = null, Func<string, string?>? environmentVariable = null,
    TimeProvider? clock = null)
{
    /// <summary>
    /// How long a keyring-resolved token (<c>gh auth token --user &lt;login&gt;</c>) is reused
    /// before this client asks gh again — short enough that a rotated or revoked token is picked
    /// up quickly, long enough that a closeout sweep touching several pull requests for the same
    /// project spawns that second gh process once rather than once per gh call (independent pre-PR
    /// review, cycle 1, conformance lens). An ambient <c>GH_TOKEN</c>/<c>GITHUB_TOKEN</c> is never
    /// cached: it is read fresh from the environment every call, which is already as cheap as this
    /// cache would make it.
    /// </summary>
    private static readonly TimeSpan TokenCacheTtl = TimeSpan.FromMinutes(2);

    private readonly EnvironmentProcessRunner runner = runner ?? ExternalProcess.RunnerWithEnvironment;
    private readonly ProcessRunner tokenRunner = tokenRunner ?? ExternalProcess.Runner;
    private readonly Func<string, string?> environmentVariable = environmentVariable ?? Environment.GetEnvironmentVariable;
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly object tokenCacheGate = new();
    private readonly Dictionary<string, (string Token, DateTimeOffset ExpiresAt)> tokenCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Runs one <c>gh</c> command from <paramref name="workingDirectory"/>, authenticated as
    /// <paramref name="account"/> alone. Split from account resolution (<see cref="ResolveAccountAsync"/>)
    /// so the actual gh invocation — pinning the token, setting the environment — is testable
    /// against an in-memory fake with no Marten session in the loop at all; a caller that already
    /// knows the account (<see cref="ProjectGitHubAccessMirror"/>, chiefly) calls this directly.
    /// <para>
    /// Deliberately does not translate a hung gh's <see cref="TimeoutException"/> (or the
    /// <see cref="ProcessOutputStuckException"/> that derives from it) into a
    /// <see cref="DomainValidationException"/> the way <see cref="TokenAsync"/>'s own token read
    /// does: this same method is now every connector's shared <c>gh</c> seam, reached through
    /// <c>ProjectScopedGitHubRunner</c> by <c>GitHubWorkItemProvider</c>, <c>GitHubReviewThreads</c>,
    /// <c>GitHubPullRequestSurface</c>, and the rest. Some of those (<c>GitHubWorkItemProvider</c>,
    /// <c>GitHubPullRequestProvider</c>, <c>GitHubPullRequestSurface</c>, <c>GitHubReviewThreads</c>)
    /// already wrap their own runner call to turn a hang into a richer, per-operation message (which
    /// credential hint to give, an exit-code-aware distinction between a hang and a failure whose
    /// output pipe stuck open, and so on); others (<c>GitHubReviewReplies</c>,
    /// <c>GitHubReviewAssignments</c>) have no such handling today and let either exception escape
    /// as-is, exactly as they did before this migration — unchanged behaviour, not a regression this
    /// task introduced (independent pre-PR review, cycle 1, adversarial lens). A blanket catch here
    /// would have converted every one of those into this method's own generic wording before any
    /// per-operation handling that does exist ever saw the exception: a caller with no handling of
    /// its own for either type is <see cref="ProjectGitHubAccessMirror.ObserveAsync"/>'s own
    /// <c>repo view</c> read, and that is where the translation belongs instead.
    /// </para>
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
    /// This client's <see cref="RunAsync"/>, closed over <paramref name="account"/>, in the plain
    /// <see cref="ProcessRunner"/> shape every existing GitHub connector class in the platform
    /// already takes (<c>GitHubReviewAssignments</c>, <c>GitHubWorkItemProvider</c>, and the rest).
    /// No production caller resolves an account ahead of time and binds it this way today: every
    /// real call site goes through <c>ProjectScopedGitHubRunner</c> instead, which resolves the
    /// account fresh per call from the working directory it is given rather than once up front, so
    /// this is exercised directly by <c>ProjectGitHubClientTests</c> rather than reached through
    /// production wiring (independent pre-PR review, cycle 1, adversarial lens). Kept as the pinned
    /// counterpart to <see cref="AmbientProcessRunner"/> for the same plain-<see cref="ProcessRunner"/>
    /// shape, should a future caller resolve an account itself rather than through
    /// <c>ProjectScopedGitHubRunner</c>. <paramref name="fileName"/> is asserted rather than silently
    /// ignored: every existing call site only ever passes "gh" through this seam, and a caller that
    /// somehow passed anything else would otherwise have that tool's invocation silently redirected
    /// to gh.
    /// </summary>
    public ProcessRunner AsProcessRunner(ProjectGitHubAccount account) =>
        (fileName, arguments, workingDirectory, cancellationToken) =>
            string.Equals(fileName, "gh", StringComparison.Ordinal)
                ? RunAsync(account, workingDirectory, arguments, cancellationToken)
                : throw new ArgumentOutOfRangeException(
                    nameof(fileName), fileName, "This runner only ever spawns gh.");

    /// <summary>
    /// Runs <c>gh</c> with no account pinned — whatever gh's own ambient auth resolves to, the
    /// exact behaviour every call site had before this migration. The deliberate exception for a
    /// gh call that is not "act as this project's account" at all, because there is no project to
    /// act as: <c>UpdateCommand</c>'s release download reads Hall9k's own public release
    /// repository, never a registered project's, and could run before any project — or any
    /// database — exists on a fresh install. Still funnelled through this class, so the platform
    /// has exactly one place that ever spawns <c>gh</c>, even for the one call with no account to
    /// choose.
    /// </summary>
    public Task<ProcessResult> RunAmbientAsync(
        string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        runner("gh", arguments, workingDirectory, new Dictionary<string, string>(StringComparer.Ordinal), cancellationToken);

    /// <summary>
    /// <see cref="RunAmbientAsync"/> in the same plain <see cref="ProcessRunner"/> shape
    /// <see cref="AsProcessRunner"/> hands back for a resolved account — for the one caller
    /// (<c>UpdateCommand</c>) that needs the ambient, no-account path in that shape instead.
    /// </summary>
    public ProcessRunner AmbientProcessRunner =>
        (fileName, arguments, workingDirectory, cancellationToken) =>
            string.Equals(fileName, "gh", StringComparison.Ordinal)
                ? RunAmbientAsync(workingDirectory, arguments, cancellationToken)
                : throw new ArgumentOutOfRangeException(
                    nameof(fileName), fileName, "This runner only ever spawns gh.");

    /// <summary>
    /// How long <see cref="AmbientIdentityReader"/> gives <c>gh api user</c> to answer — the same
    /// bound <c>NodeBootstrap</c>'s own raw spawn always used before this method replaced it, kept
    /// short rather than <see cref="ExternalProcess.Deadline"/>'s two minutes so an ordinary
    /// <c>h9k</c> invocation never hangs on a wedged <c>gh</c> during bootstrap.
    /// </summary>
    private static readonly TimeSpan BootstrapIdentityDeadline = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Adapts <see cref="RunAmbientAsync"/> into the synchronous, parameterless
    /// <see cref="GhIdentityReader"/> shape <c>NodeBootstrap.EnsureAsync</c> and
    /// <c>RefreshGitHubIdentityAsync</c> take — the seam the Cli and the daemon each supply their
    /// own instance of at the one or two call sites that actually need a live read (<c>h9k project
    /// add</c>, <c>h9k project join</c>, and the daemon's own start), so bootstrap's identity read
    /// runs through this class, the platform's one place that ever spawns <c>gh</c>, rather than a
    /// raw process of its own (independent pre-PR review, cycle 1, human verdict). Ambient, never
    /// account-pinned, because bootstrap runs before any project — or any registered GitHub
    /// connection — exists: there is no account yet to resolve, since
    /// <see cref="ResolveAccountAsync"/> reads the very connection this call is what first
    /// confirms. <c>Hall9k.Domain</c> gains no reference to <c>Hall9k.Connectors</c> from this: the
    /// <see cref="GhIdentityReader"/> delegate type is already Domain's own, and only the Cli and
    /// daemon composition roots (which already reference this class) ever construct one.
    /// <para>
    /// <paramref name="runner"/> defaults to the real transport bound to
    /// <see cref="BootstrapIdentityDeadline"/> rather than <see cref="ExternalProcess.Deadline"/>'s
    /// two minutes; a test pins its own fake here the same way every other seam on this class does,
    /// rather than shelling to a real <c>gh</c>.
    /// </para>
    /// </summary>
    public static GhIdentityReader AmbientIdentityReader(string workingDirectory, EnvironmentProcessRunner? runner = null)
    {
        ProjectGitHubClient client = new(runner: runner ?? ExternalProcess.RunnerWithEnvironmentAndDeadline(BootstrapIdentityDeadline));
        return () =>
        {
            try
            {
                ProcessResult result = client
                    .RunAmbientAsync(workingDirectory, ["api", "user"], CancellationToken.None)
                    .GetAwaiter().GetResult();
                return result.ExitCode == 0 && result.StandardOutput.IsNotBlank()
                    ? result.StandardOutput.Trim()
                    : null;
            }
            // A gh that cannot answer at all (not installed, wedged, no network) is exactly as
            // unconfirmed as one that answers with a non-zero exit — NodeBootstrap's own contract,
            // unchanged from the raw spawn this replaces, is best-effort with no exception ever
            // escaping the reader.
            catch (Exception exception) when (exception is TimeoutException or InvalidOperationException
                or System.ComponentModel.Win32Exception)
            {
                return null;
            }
        };
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

        DateTimeOffset now = clock.GetUtcNow();
        lock (tokenCacheGate)
        {
            if (tokenCache.TryGetValue(login, out (string Token, DateTimeOffset ExpiresAt) cached) && cached.ExpiresAt > now)
            {
                return cached.Token;
            }
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

        lock (tokenCacheGate)
        {
            tokenCache[login] = (token, now + TokenCacheTtl);
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

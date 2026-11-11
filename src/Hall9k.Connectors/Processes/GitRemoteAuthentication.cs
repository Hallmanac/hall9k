using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Hall9k.Connectors.Processes;

/// <summary>
/// Where a remote git call's GitHub token comes from. The platform has one real implementation
/// (<c>ProjectGitRemoteTokenSource</c>, which resolves the project a repository belongs to and
/// reads that project's confirmed account's token through the one shared
/// <c>ProjectGitHubClient</c>); tests supply their own.
/// </summary>
public interface IGitRemoteTokenSource
{
    /// <summary>
    /// The credentials for a git call run in <paramref name="workingDirectory"/>, or null when it
    /// has none to give: the repository belongs to no registered project, the project has no
    /// confirmed GitHub account, its remote is not on https://github.com/, or gh could not produce
    /// a token. Null is never an error: the call then runs the way it always has, through the
    /// owner's own credential helper. Never throws for those reasons.
    /// </summary>
    Task<GitRemoteAuthorization?> TryGetAsync(string workingDirectory, CancellationToken cancellationToken);

    /// <summary>
    /// Drops whatever token is cached for the repository's account, so the next
    /// <see cref="TryGetAsync"/> reads it again. Called when git rejected the token it was given.
    /// </summary>
    void Invalidate(string workingDirectory);
}

/// <summary>
/// The token one git call is to carry, and the URL prefix it is scoped to. The token is a
/// secret: this type never formats it, so a stray <c>ToString()</c> or log line cannot print it.
/// </summary>
public sealed class GitRemoteAuthorization(string token, string? scopeUrl = null)
{
    /// <summary>The only place git calls are scoped to: GitHub's own https host.</summary>
    public const string GitHubScopeUrl = "https://github.com/";

    public string Token { get; } = token;

    /// <summary>The URL prefix the header and the helper override are scoped to, with a trailing slash. A test points it at a local server; production is always <see cref="GitHubScopeUrl"/>.</summary>
    public string ScopeUrl { get; } = scopeUrl ?? GitHubScopeUrl;

    public override string ToString() => $"GitRemoteAuthorization({ScopeUrl}, token withheld)";
}

/// <summary>
/// Gives a git call that reaches a remote its GitHub token through git's own per-call environment
/// configuration (<c>GIT_CONFIG_COUNT</c>/<c>GIT_CONFIG_KEY_n</c>/<c>GIT_CONFIG_VALUE_n</c>), with
/// the owner's credential helper switched off for github.com alone. Without it git-remote-https
/// asks the helper configured in the owner's <c>~/.gitconfig</c> (typically
/// <c>!gh auth git-credential</c>) for the same token, which on Windows starts <c>sh</c> and
/// <c>gh</c> for every call and leaves a kernel access token allocated for each until reboot
/// (docs/spikes/windows-token-leak-origin/FINDINGS.md, rows 3 to 5, 53.8% of the remaining leak);
/// on macOS it costs the same two processes without the leak.
/// <para>
/// Deliberately separate from <see cref="NonInteractiveGit.Apply"/>, which runs for every process
/// h9kd starts: gate commands run agent-written code, and gh and ssh-keygen never need a git
/// token. This type is reached only from the start paths that run git, and only adds keys when
/// the git subcommand actually talks to a remote (<see cref="ReachesRemote"/>). It never writes
/// anywhere but the <see cref="ProcessStartInfo"/> it is handed: nothing here touches this
/// process's own environment, which every agent session would inherit.
/// </para>
/// <para>
/// The keys are scoped by URL, never unscoped, so a remote on another host (GitHub Enterprise,
/// GitLab, Azure) keeps its own helper and never receives the header:
/// <c>credential.https://github.com.helper</c> empty, <c>http.https://github.com/.extraHeader</c>
/// the token, and <c>http.https://github.com/.followRedirects</c> false. The last one matters
/// because git re-sends a configured header to wherever a redirect lands on its follow-up request
/// (challenge probe 1); with redirects refused, a call that needs one fails and is retried once
/// without the keys, which is what it did before this existed.
/// </para>
/// </summary>
public static class GitRemoteAuthentication
{
    /// <summary>
    /// Variables that make git, curl or trace2 write request headers or environment values to a
    /// log. All of them are removed from a call that carries the token: the header is in
    /// <c>GIT_CONFIG_VALUE_n</c>, which <c>GIT_TRACE2_ENV_VARS</c> can record, and
    /// <c>http.*</c> config, which <c>GIT_TRACE2_CONFIG_PARAMS</c> can (challenge probe 3).
    /// </summary>
    internal static readonly IReadOnlyList<string> TraceVariables =
    [
        "GIT_TRACE_CURL", "GIT_CURL_VERBOSE", "GIT_TRACE_REDACT", "GIT_TRACE2_CONFIG_PARAMS", "GIT_TRACE2_ENV_VARS",
    ];

    private static readonly string[] RemoteSubcommands = ["fetch", "push", "ls-remote", "clone", "pull"];

    private static IGitRemoteTokenSource? source;

    private static readonly AsyncLocal<IGitRemoteTokenSource?> ScopedSource = new();

    /// <summary>
    /// The process-wide source, set once by a composition root (the daemon, the CLI). Unset, which
    /// is every test and any host that never configures one, means no call carries a token.
    /// </summary>
    public static void Configure(IGitRemoteTokenSource? tokenSource) => Volatile.Write(ref source, tokenSource);

    /// <summary>
    /// A source that applies to the current asynchronous flow alone, restored when the returned
    /// scope is disposed. For a test: the process-wide source is shared by every test running in
    /// parallel, a flow-local one is not.
    /// </summary>
    public static IDisposable UseForCurrentFlow(IGitRemoteTokenSource tokenSource)
    {
        ScopedSource.Value = tokenSource;
        return new Restore();
    }

    private sealed class Restore : IDisposable
    {
        public void Dispose() => ScopedSource.Value = null;
    }

    private static IGitRemoteTokenSource? CurrentSource => ScopedSource.Value ?? Volatile.Read(ref source);

    /// <summary>
    /// The git subcommand in <paramref name="arguments"/>, past git's own leading options
    /// (<c>-C &lt;path&gt;</c>, <c>-c &lt;key=value&gt;</c>, and flags such as <c>--no-pager</c>);
    /// null when there is none.
    /// </summary>
    internal static string? Subcommand(IReadOnlyList<string> arguments)
    {
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            switch (argument)
            {
                case "-C" or "-c" or "--git-dir" or "--work-tree" or "--namespace" or "--exec-path":
                    index++;
                    break;
                case { Length: > 0 } when argument[0] == '-':
                    break;
                default:
                    return argument;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="fileName"/> with <paramref name="arguments"/> is a git call that
    /// talks to a remote: fetch, push, ls-remote, clone or pull. <c>git ls-remote --get-url</c>
    /// only prints a URL and reaches nothing, so it is not one.
    /// </summary>
    public static bool ReachesRemote(string fileName, IReadOnlyList<string> arguments)
    {
        if (!IsGit(fileName))
        {
            return false;
        }

        string? subcommand = Subcommand(arguments);
        return subcommand is not null
            && Array.IndexOf(RemoteSubcommands, subcommand) >= 0
            && !arguments.Contains("--get-url", StringComparer.Ordinal);
    }

    /// <summary>The same test for a call written as one argument string (<c>fetch origin</c>), the shape <c>GitWorktreeManager</c> builds.</summary>
    public static bool ReachesRemote(string fileName, string arguments) =>
        ReachesRemote(fileName, arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static bool IsGit(string fileName) =>
        Path.GetFileNameWithoutExtension(fileName).Equals("git", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Adds the token keys to <paramref name="startInfo"/>, after any <c>GIT_CONFIG_COUNT</c>
    /// entries already there (<see cref="NonInteractiveGit.Apply"/>'s <c>commit.gpgsign=false</c>
    /// stays in place), and removes the variables that could write them to a log. Writes to the
    /// start info alone, never to this process's own environment.
    /// </summary>
    public static void Apply(ProcessStartInfo startInfo, GitRemoteAuthorization authorization)
    {
        // The same reading NonInteractiveGit.Apply uses: NumberStyles.None rejects a sign, so a
        // negative inherited count starts over at index 0 rather than being appended to.
        int existing = startInfo.Environment.TryGetValue("GIT_CONFIG_COUNT", out string? count)
            && int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : 0;

        string scope = authorization.ScopeUrl.EndsWith('/') ? authorization.ScopeUrl : authorization.ScopeUrl + "/";
        string credentialScope = scope.TrimEnd('/');
        string basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"x-access-token:{authorization.Token}"));

        (string Key, string Value)[] entries =
        [
            ($"credential.{credentialScope}.helper", string.Empty),
            ($"http.{scope}.extraHeader", $"Authorization: Basic {basic}"),
            ($"http.{scope}.followRedirects", "false"),
        ];

        for (int offset = 0; offset < entries.Length; offset++)
        {
            string index = (existing + offset).ToString(CultureInfo.InvariantCulture);
            startInfo.Environment[$"GIT_CONFIG_KEY_{index}"] = entries[offset].Key;
            startInfo.Environment[$"GIT_CONFIG_VALUE_{index}"] = entries[offset].Value;
        }

        startInfo.Environment["GIT_CONFIG_COUNT"] =
            (existing + entries.Length).ToString(CultureInfo.InvariantCulture);

        foreach (string variable in TraceVariables)
        {
            startInfo.Environment.Remove(variable);
        }
    }

    /// <summary>
    /// Runs one git call with the token keys when it reaches a remote and a token can be had, and
    /// settles what a failure means. <paramref name="start"/> starts the process once, with the
    /// credentials it is handed (null means none) applied to its start info through
    /// <see cref="Apply"/>, and returns whatever it reads from it.
    /// <list type="bullet">
    /// <item>No source, not a remote call, no token: one run without the keys, as before.</item>
    /// <item>A redirect failure (the keys forbid following one): retried once without the keys.</item>
    /// <item>An authentication failure: the cached token is dropped and read again once; if there is a fresh one the call is retried with it, and if that fails on authentication or a redirect, or there is none, it runs once more without the keys.</item>
    /// </list>
    /// A retried call is one git had already refused, so a push is never repeated after it landed.
    /// </summary>
    public static async Task<TResult> RunAsync<TResult>(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Func<GitRemoteAuthorization?, Task<TResult>> start,
        Func<TResult, (int ExitCode, string StandardError)> outcome,
        CancellationToken cancellationToken)
    {
        IGitRemoteTokenSource? tokens = CurrentSource;
        if (tokens is null || !ReachesRemote(fileName, arguments))
        {
            return await start(null);
        }

        GitRemoteAuthorization? authorization = await tokens.TryGetAsync(workingDirectory, cancellationToken);
        if (authorization is null)
        {
            return await start(null);
        }

        TResult result = await start(authorization);
        (int exitCode, string standardError) = outcome(result);
        if (exitCode == 0)
        {
            return result;
        }

        switch (Classify(standardError))
        {
            case FailureKind.Redirect:
                return await start(null);
            case FailureKind.Authentication:
                tokens.Invalidate(workingDirectory);
                GitRemoteAuthorization? fresh = await tokens.TryGetAsync(workingDirectory, cancellationToken);
                if (fresh is null)
                {
                    return await start(null);
                }

                TResult retried = await start(fresh);
                (int retriedExit, string retriedError) = outcome(retried);
                return retriedExit != 0 && Classify(retriedError) is FailureKind.Authentication or FailureKind.Redirect
                    ? await start(null)
                    : retried;
            default:
                return result;
        }
    }

    /// <summary>The same run, for a call whose arguments are written as one string (<c>fetch origin</c>).</summary>
    public static Task<TResult> RunAsync<TResult>(
        string fileName,
        string arguments,
        string workingDirectory,
        Func<GitRemoteAuthorization?, Task<TResult>> start,
        Func<TResult, (int ExitCode, string StandardError)> outcome,
        CancellationToken cancellationToken) =>
        RunAsync(
            fileName, arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries), workingDirectory, start, outcome,
            cancellationToken);

    internal enum FailureKind
    {
        Other,
        Redirect,
        Authentication,
    }

    /// <summary>
    /// Reads git's own words for why a call failed. A redirect is what
    /// <c>http.followRedirects=false</c> turns every 3xx into ("The requested URL returned error:
    /// 301"); an authentication failure is a 401 or 403, or git saying it could not read or was
    /// refused a credential. Anything else (a rejected non-fast-forward push, a missing ref, a
    /// network failure) is never retried here.
    /// </summary>
    internal static FailureKind Classify(string standardError)
    {
        if (standardError.Length == 0)
        {
            return FailureKind.Other;
        }

        foreach (string status in new[] { "301", "302", "303", "307", "308" })
        {
            if (standardError.Contains($"returned error: {status}", StringComparison.Ordinal))
            {
                return FailureKind.Redirect;
            }
        }

        string[] authentication =
        [
            "returned error: 401", "returned error: 403", "authentication failed", "invalid username or password",
            "could not read username", "could not read password", "terminal prompts disabled",
            "bad credentials", "invalid credentials",
        ];
        return authentication.Any(marker => standardError.Contains(marker, StringComparison.OrdinalIgnoreCase))
            ? FailureKind.Authentication
            : FailureKind.Other;
    }
}

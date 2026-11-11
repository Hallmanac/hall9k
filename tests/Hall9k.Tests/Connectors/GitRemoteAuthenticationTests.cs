using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Hall9k.Connectors.Processes;
using Hall9k.Tests.Domain;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// How a git call that reaches a remote carries its project's GitHub token: through git's own
/// per-call environment configuration, scoped to github.com, appended after the entries already
/// there, and kept off every environment that is not a remote git call's. Machine-independent: it
/// asserts the shape of the start info and the retry rules, and never runs git.
/// </summary>
public sealed class GitRemoteAuthenticationTests
{
    private const string Token = "ghp_TESTTOKEN0123456789";

    private static string Basic => Convert.ToBase64String(Encoding.ASCII.GetBytes($"x-access-token:{Token}"));

    /// <summary>A start info with any inherited GIT_CONFIG_* and trace variable removed, so the test says what it asserts whether or not the gate that hosts it applied the same hardening.</summary>
    private static ProcessStartInfo Fresh()
    {
        ProcessStartInfo startInfo = new();
        foreach (string key in startInfo.Environment.Keys
            .Where(key => key.StartsWith("GIT_CONFIG_", StringComparison.Ordinal)
                || key.StartsWith("GIT_TRACE", StringComparison.Ordinal)
                || key.Equals("GIT_CURL_VERBOSE", StringComparison.Ordinal))
            .ToList())
        {
            startInfo.Environment.Remove(key);
        }

        return startInfo;
    }

    private static Dictionary<string, string> ConfigEntries(ProcessStartInfo startInfo)
    {
        int count = int.Parse(startInfo.Environment["GIT_CONFIG_COUNT"]!);
        Dictionary<string, string> entries = [];
        for (int index = 0; index < count; index++)
        {
            entries[startInfo.Environment[$"GIT_CONFIG_KEY_{index}"]!] = startInfo.Environment[$"GIT_CONFIG_VALUE_{index}"]!;
        }

        return entries;
    }

    [Fact]
    public void The_keys_append_after_the_signing_override_and_all_three_are_scoped_to_github()
    {
        ProcessStartInfo startInfo = Fresh();
        NonInteractiveGit.Apply(startInfo);

        GitRemoteAuthentication.Apply(startInfo, new GitRemoteAuthorization(Token));

        startInfo.Environment["GIT_CONFIG_COUNT"].Should().Be("4");
        startInfo.Environment["GIT_CONFIG_KEY_0"].Should().Be("commit.gpgsign");
        startInfo.Environment["GIT_CONFIG_VALUE_0"].Should().Be("false");
        Dictionary<string, string> entries = ConfigEntries(startInfo);
        entries["credential.https://github.com.helper"].Should().BeEmpty();
        entries["http.https://github.com/.extraHeader"].Should().Be($"Authorization: Basic {Basic}");
        entries["http.https://github.com/.followRedirects"].Should().Be("false");
    }

    [Fact]
    public void Entries_are_appended_after_any_count_already_set_even_without_the_signing_override()
    {
        ProcessStartInfo startInfo = Fresh();
        startInfo.Environment["GIT_CONFIG_COUNT"] = "2";
        startInfo.Environment["GIT_CONFIG_KEY_0"] = "a.b";
        startInfo.Environment["GIT_CONFIG_VALUE_0"] = "1";
        startInfo.Environment["GIT_CONFIG_KEY_1"] = "c.d";
        startInfo.Environment["GIT_CONFIG_VALUE_1"] = "2";

        GitRemoteAuthentication.Apply(startInfo, new GitRemoteAuthorization(Token));

        startInfo.Environment["GIT_CONFIG_COUNT"].Should().Be("5");
        startInfo.Environment["GIT_CONFIG_KEY_0"].Should().Be("a.b");
        startInfo.Environment["GIT_CONFIG_KEY_1"].Should().Be("c.d");
        startInfo.Environment["GIT_CONFIG_KEY_2"].Should().Be("credential.https://github.com.helper");
    }

    [Fact]
    public void The_variables_that_could_write_the_header_to_a_log_are_removed()
    {
        ProcessStartInfo startInfo = Fresh();
        foreach (string variable in new[]
        {
            "GIT_TRACE_CURL", "GIT_CURL_VERBOSE", "GIT_TRACE_REDACT", "GIT_TRACE2_CONFIG_PARAMS", "GIT_TRACE2_ENV_VARS",
        })
        {
            startInfo.Environment[variable] = "1";
        }

        GitRemoteAuthentication.Apply(startInfo, new GitRemoteAuthorization(Token));

        startInfo.Environment.Keys.Should().NotContain(
            ["GIT_TRACE_CURL", "GIT_CURL_VERBOSE", "GIT_TRACE_REDACT", "GIT_TRACE2_CONFIG_PARAMS", "GIT_TRACE2_ENV_VARS"]);
    }

    [Fact]
    public void The_token_is_on_no_command_line_and_in_no_formatted_text()
    {
        GitRemoteAuthorization authorization = new(Token);
        ProcessStartInfo startInfo = ExternalProcess.StartInfoFor(
            "git", ["fetch", "origin"], "/repos/hall9k", environment: null, remote: authorization);

        startInfo.FileName.Should().NotContain(Token).And.NotContain(Basic);
        startInfo.Arguments.Should().NotContain(Token).And.NotContain(Basic);
        startInfo.ArgumentList.Should().NotContain(argument => argument.Contains(Token) || argument.Contains(Basic));
        authorization.ToString().Should().NotContain(Token);
        ConfigEntries(startInfo)["http.https://github.com/.extraHeader"].Should().Contain(
            Basic, "the environment is the one place it travels");
    }

    [Fact]
    public void A_start_info_built_for_a_remote_call_keeps_the_signing_override_in_place()
    {
        ProcessStartInfo startInfo = ExternalProcess.StartInfoFor(
            "git", ["push", "origin", "main"], "/repos/hall9k", environment: null, remote: new GitRemoteAuthorization(Token));

        ConfigEntries(startInfo)["commit.gpgsign"].Should().Be("false");
    }

    [Theory]
    [InlineData("git", new[] { "fetch", "origin" }, true)]
    [InlineData("git", new[] { "push", "origin", "a:b" }, true)]
    [InlineData("git", new[] { "ls-remote", "origin", "refs/heads/*" }, true)]
    [InlineData("git", new[] { "clone", "--bare", "https://github.com/o/r.git", "r.git" }, true)]
    [InlineData("git", new[] { "pull", "--ff-only" }, true)]
    [InlineData("git", new[] { "-C", "/repos/hall9k", "fetch", "origin" }, true)]
    [InlineData("git", new[] { "-c", "gpg.format=ssh", "-C", "/r", "ls-remote", "origin" }, true)]
    [InlineData("git.exe", new[] { "fetch" }, true)]
    [InlineData("git", new[] { "ls-remote", "--get-url", "origin" }, false)]
    [InlineData("git", new[] { "remote", "get-url", "origin" }, false)]
    [InlineData("git", new[] { "rev-parse", "HEAD" }, false)]
    [InlineData("git", new[] { "-C", "/r", "verify-commit", "abc" }, false)]
    [InlineData("git", new[] { "update-ref", "refs/x", "abc" }, false)]
    [InlineData("git", new[] { "worktree", "add", "wt" }, false)]
    [InlineData("gh", new[] { "pr", "view" }, false)]
    [InlineData("ssh-keygen", new[] { "-Y", "verify" }, false)]
    [InlineData("cmd.exe", new[] { "/c", "git fetch" }, false)]
    public void Only_a_git_subcommand_that_talks_to_a_remote_reaches_one(string fileName, string[] arguments, bool expected)
    {
        GitRemoteAuthentication.ReachesRemote(fileName, arguments).Should().Be(expected);
    }

    [Theory]
    [InlineData("fetch origin", true)]
    [InlineData("fetch origin \"+refs/pull/3/head:refs/x\"", true)]
    [InlineData("remote get-url origin", false)]
    [InlineData("status --porcelain", false)]
    public void The_one_string_form_a_worktree_call_is_written_in_is_read_the_same_way(string arguments, bool expected)
    {
        GitRemoteAuthentication.ReachesRemote("git", arguments).Should().Be(expected);
    }

    [Fact]
    public void Gate_commands_sessions_gh_ssh_keygen_and_local_git_carry_none_of_the_keys()
    {
        // Gate commands and agent sessions are built from NonInteractiveGit.Apply and the daemon's
        // own environment, never from the helper; gh, ssh-keygen and a git call that reaches no
        // remote go through StartInfoFor, which hands the keys only to a call the runner has
        // authorized, and the runner authorizes none of these.
        ProcessStartInfo gate = Fresh();
        NonInteractiveGit.Apply(gate);
        ProcessStartInfo[] environments =
        [
            gate,
            ExternalProcess.StartInfoFor("gh", ["pr", "view"], "/repos/hall9k"),
            ExternalProcess.StartInfoFor("ssh-keygen", ["-Y", "verify"], "/repos/hall9k"),
            ExternalProcess.StartInfoFor("git", ["rev-parse", "HEAD"], "/repos/hall9k"),
            ExternalProcess.StartInfoFor("git", ["verify-commit", "abc"], "/repos/hall9k"),
        ];

        foreach (ProcessStartInfo environment in environments)
        {
            environment.Environment
                .Where(pair => pair.Key.StartsWith("GIT_CONFIG_KEY_", StringComparison.Ordinal))
                .Select(pair => pair.Value)
                .Should().NotContain(value => value != null
                    && (value.Contains("extraHeader") || value.Contains("followRedirects") || value.EndsWith(".helper")));
        }
    }

    [Fact]
    public async Task A_gh_ssh_keygen_or_local_git_call_never_asks_for_a_token()
    {
        FakeTokenSource source = new(Token);
        using IDisposable scope = GitRemoteAuthentication.UseForCurrentFlow(source);
        List<GitRemoteAuthorization?> handed = [];

        foreach ((string fileName, string[] arguments) in new[]
        {
            ("gh", new[] { "pr", "view" }), ("ssh-keygen", new[] { "-Y", "verify" }),
            ("git", new[] { "rev-parse", "HEAD" }), ("git", new[] { "remote", "get-url", "origin" }),
        })
        {
            await GitRemoteAuthentication.RunAsync(
                fileName, arguments, "/repos/hall9k",
                remote => { handed.Add(remote); return Task.FromResult((ExitCode: 0, StandardError: string.Empty)); },
                result => result, CancellationToken.None);
        }

        source.Gets.Should().Be(0, "none of these reaches a remote");
        handed.Should().OnlyContain(remote => remote == null);
    }

    [Fact]
    public async Task No_source_means_one_plain_run_even_for_a_remote_call()
    {
        int runs = 0;
        List<GitRemoteAuthorization?> handed = [];

        await GitRemoteAuthentication.RunAsync(
            "git", ["fetch", "origin"], "/repos/hall9k",
            remote => { runs++; handed.Add(remote); return Task.FromResult((ExitCode: 1, StandardError: "fatal: nope")); },
            result => result, CancellationToken.None);

        runs.Should().Be(1);
        handed.Should().ContainSingle().Which.Should().BeNull();
    }

    [Fact]
    public async Task A_remote_call_with_no_token_to_give_runs_once_without_the_keys()
    {
        FakeTokenSource source = new();
        using IDisposable scope = GitRemoteAuthentication.UseForCurrentFlow(source);
        List<GitRemoteAuthorization?> handed = [];

        await GitRemoteAuthentication.RunAsync(
            "git", ["fetch", "origin"], "/repos/hall9k",
            remote => { handed.Add(remote); return Task.FromResult((ExitCode: 0, StandardError: string.Empty)); },
            result => result, CancellationToken.None);

        handed.Should().ContainSingle().Which.Should().BeNull();
        source.Gets.Should().Be(1);
    }

    [Fact]
    public async Task A_successful_call_with_the_token_is_not_repeated()
    {
        FakeTokenSource source = new(Token);
        using IDisposable scope = GitRemoteAuthentication.UseForCurrentFlow(source);
        int runs = 0;

        await GitRemoteAuthentication.RunAsync(
            "git", ["push", "origin", "a:b"], "/repos/hall9k",
            remote => { runs++; return Task.FromResult((ExitCode: 0, StandardError: string.Empty)); },
            result => result, CancellationToken.None);

        runs.Should().Be(1);
        source.Invalidations.Should().Be(0);
    }

    [Fact]
    public async Task A_rejected_push_that_is_not_an_authentication_failure_is_never_retried()
    {
        FakeTokenSource source = new(Token, "fresh");
        using IDisposable scope = GitRemoteAuthentication.UseForCurrentFlow(source);
        int runs = 0;

        (int ExitCode, string StandardError) result = await GitRemoteAuthentication.RunAsync(
            "git", ["push", "origin", "a:b"], "/repos/hall9k",
            remote => { runs++; return Task.FromResult((ExitCode: 1, StandardError: "! [rejected] a -> b (non-fast-forward)")); },
            r => r, CancellationToken.None);

        runs.Should().Be(1);
        result.ExitCode.Should().Be(1);
        source.Invalidations.Should().Be(0);
    }

    [Fact]
    public async Task An_authentication_failure_drops_the_token_reads_it_again_once_and_retries_with_the_new_one()
    {
        FakeTokenSource source = new("stale-token", "fresh-token");
        using IDisposable scope = GitRemoteAuthentication.UseForCurrentFlow(source);
        List<string?> used = [];

        (int ExitCode, string StandardError) result = await GitRemoteAuthentication.RunAsync(
            "git", ["fetch", "origin"], "/repos/hall9k",
            remote =>
            {
                used.Add(remote?.Token);
                return Task.FromResult(remote?.Token == "fresh-token"
                    ? (ExitCode: 0, StandardError: string.Empty)
                    : (ExitCode: 128, StandardError: "fatal: Authentication failed for 'https://github.com/o/r.git/'"));
            },
            r => r, CancellationToken.None);

        used.Should().Equal("stale-token", "fresh-token");
        source.Invalidations.Should().Be(1);
        source.Gets.Should().Be(2);
        result.ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task When_the_retry_fails_the_same_way_the_call_runs_once_more_without_the_keys()
    {
        FakeTokenSource source = new("stale-token", "also-bad", "never-read");
        using IDisposable scope = GitRemoteAuthentication.UseForCurrentFlow(source);
        List<string?> used = [];

        await GitRemoteAuthentication.RunAsync(
            "git", ["fetch", "origin"], "/repos/hall9k",
            remote =>
            {
                used.Add(remote?.Token);
                return Task.FromResult((ExitCode: 128, StandardError: "remote: Invalid username or password."));
            },
            r => r, CancellationToken.None);

        used.Should().Equal("stale-token", "also-bad", null);
        source.Invalidations.Should().Be(1, "the token is dropped and read once, not in a loop");
        source.Redirects.Should().Be(0, "an authentication failure is not a redirect");
        source.Gets.Should().Be(2);
    }

    [Fact]
    public async Task When_the_retry_with_the_fresh_token_meets_a_redirect_the_call_runs_once_more_without_the_keys()
    {
        FakeTokenSource source = new("stale-token", "fresh-token");
        using IDisposable scope = GitRemoteAuthentication.UseForCurrentFlow(source);
        List<string?> used = [];

        (int ExitCode, string StandardError) result = await GitRemoteAuthentication.RunAsync(
            "git", ["fetch", "origin"], "/repos/hall9k",
            remote =>
            {
                used.Add(remote?.Token);
                return Task.FromResult(remote?.Token switch
                {
                    null => (ExitCode: 0, StandardError: string.Empty),
                    "stale-token" => (ExitCode: 128, StandardError: "fatal: unable to access 'https://github.com/o/old.git/': The requested URL returned error: 401"),
                    _ => (ExitCode: 128, StandardError: "fatal: unable to access 'https://github.com/o/old.git/': The requested URL returned error: 301"),
                });
            },
            r => r, CancellationToken.None);

        used.Should().Equal("stale-token", "fresh-token", null);
        result.ExitCode.Should().Be(0, "a repository renamed since the token was revoked still fetches, as it did before the keys");
        source.Redirects.Should().Be(1, "a redirect on the retry is remembered the same as one on the first attempt");
    }

    [Fact]
    public async Task When_no_fresh_token_can_be_read_after_an_authentication_failure_the_call_runs_without_the_keys()
    {
        FakeTokenSource source = new("stale-token");
        using IDisposable scope = GitRemoteAuthentication.UseForCurrentFlow(source);
        List<string?> used = [];

        await GitRemoteAuthentication.RunAsync(
            "git", ["ls-remote", "origin"], "/repos/hall9k",
            remote =>
            {
                used.Add(remote?.Token);
                return Task.FromResult((ExitCode: 128, StandardError: "fatal: could not read Username for 'https://github.com'"));
            },
            r => r, CancellationToken.None);

        used.Should().Equal("stale-token", null);
    }

    [Fact]
    public async Task A_redirect_failure_is_retried_once_without_the_keys_and_does_not_drop_the_token()
    {
        FakeTokenSource source = new(Token);
        using IDisposable scope = GitRemoteAuthentication.UseForCurrentFlow(source);
        List<string?> used = [];

        (int ExitCode, string StandardError) result = await GitRemoteAuthentication.RunAsync(
            "git", ["fetch", "origin"], "/repos/hall9k",
            remote =>
            {
                used.Add(remote?.Token);
                return Task.FromResult(remote is null
                    ? (ExitCode: 0, StandardError: string.Empty)
                    : (ExitCode: 128, StandardError: "fatal: unable to access 'https://github.com/o/old.git/': The requested URL returned error: 301"));
            },
            r => r, CancellationToken.None);

        used.Should().Equal(Token, null);
        source.Invalidations.Should().Be(0);
        source.Redirects.Should().Be(1);
        result.ExitCode.Should().Be(0);
    }

    [Fact]
    public void The_source_files_name_the_helper_only_in_the_five_start_paths()
    {
        string[] allowed =
        [
            Path.Combine("Hall9k.Connectors", "Processes", "ExternalProcess.cs"),
            Path.Combine("Hall9k.Connectors", "Ledger", "GitLedger.cs"),
            Path.Combine("Hall9k.Connectors", "Messaging", "GitLedgerMessageTransport.cs"),
            Path.Combine("Hall9k.Connectors", "Worktrees", "GitWorktreeManager.cs"),
            Path.Combine("Hall9k.Daemon", "Execution", "PullRequestOpener.cs"),
        ];
        string sourceDirectory = TestSourceTree.SourceDirectory();

        string[] appliers =
        [
            .. Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories)
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .Where(file => File.ReadAllText(file).Contains("GitRemoteAuthentication.Apply(", StringComparison.Ordinal))
                .Select(file => Path.GetRelativePath(sourceDirectory, file)),
        ];

        appliers.Should().BeEquivalentTo(allowed);
    }

    [Fact]
    public void The_shared_non_interactive_helper_knows_nothing_about_the_token()
    {
        string text = File.ReadAllText(Path.Combine(
            TestSourceTree.SourceDirectory(), "Hall9k.Connectors", "Processes", "NonInteractiveGit.cs"));

        text.Should().NotContain("extraHeader").And.NotContain("followRedirects").And.NotContain("credential.");
    }

    private sealed class FakeTokenSource(params string?[] tokens) : IGitRemoteTokenSource
    {
        private readonly Queue<string?> remaining = new(tokens);

        public int Gets { get; private set; }

        public int Invalidations { get; private set; }

        public int Redirects { get; private set; }

        public Task<GitRemoteAuthorization?> TryGetAsync(string workingDirectory, CancellationToken cancellationToken)
        {
            Gets++;
            return Task.FromResult(remaining.TryDequeue(out string? token) && token is not null
                ? new GitRemoteAuthorization(token)
                : null);
        }

        public void Invalidate(string workingDirectory) => Invalidations++;

        public void RecordRedirect(string workingDirectory) => Redirects++;
    }
}

using FluentAssertions;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// Where a remote git call's token comes from: the project's confirmed account, read through one
/// shared <see cref="ProjectGitHubClient"/>, and only for a remote on https://github.com/. The
/// project lookup is a seam, so none of this needs a database.
/// </summary>
public sealed class ProjectGitRemoteTokenSourceTests
{
    private const string Token = "ghp_SOURCETOKEN0123456789";

    private static readonly ProjectGitHubAccount Account = new(4181388, "hallmanac");

    private static string? NoAmbientToken(string name) => null;

    private static GitRepositoryAccountResolver Resolves(ProjectGitHubAccount? account) =>
        (_, _) => Task.FromResult(account);

    private static ProjectGitHubClient ClientReading(RecordingProcessRunner tokenRunner) =>
        new(RecordingEnvironmentProcessRunner.Succeeding("{}").Runner, tokenRunner.Runner, NoAmbientToken);

    private static RecordingProcessRunner Origin(string url) => RecordingProcessRunner.Succeeding(url + "\n");

    [Fact]
    public async Task A_github_https_remote_gets_the_projects_token_and_the_github_scope()
    {
        RecordingProcessRunner tokenRunner = RecordingProcessRunner.Succeeding(Token + "\n");
        ProjectGitRemoteTokenSource source = new(
            Resolves(Account), ClientReading(tokenRunner), Origin("https://github.com/hallmanac/hall9k.git").Runner);

        GitRemoteAuthorization? authorization = await source.TryGetAsync("/repos/hall9k", CancellationToken.None);

        authorization.Should().NotBeNull();
        authorization!.Token.Should().Be(Token);
        authorization.ScopeUrl.Should().Be("https://github.com/");
        tokenRunner.Calls.Single().Arguments.Should().ContainInOrder("auth", "token", "--user", "hallmanac");
    }

    [Theory]
    [InlineData("https://ghe.example.com/hallmanac/hall9k.git")]
    [InlineData("https://gitlab.com/hallmanac/hall9k.git")]
    [InlineData("git@github.com:hallmanac/hall9k.git")]
    [InlineData("ssh://git@github.com/hallmanac/hall9k.git")]
    [InlineData("http://github.com/hallmanac/hall9k.git")]
    [InlineData("https://github.com.evil.example/hallmanac/hall9k.git")]
    [InlineData("https://github.com:8443/hallmanac/hall9k.git")]
    [InlineData("C:/local/origin.git")]
    public async Task A_remote_that_is_not_on_https_github_com_gets_no_token_and_gh_is_never_asked(string remote)
    {
        RecordingProcessRunner tokenRunner = RecordingProcessRunner.Succeeding(Token + "\n");
        ProjectGitRemoteTokenSource source = new(Resolves(Account), ClientReading(tokenRunner), Origin(remote).Runner);

        GitRemoteAuthorization? authorization = await source.TryGetAsync("/repos/hall9k", CancellationToken.None);

        authorization.Should().BeNull("the owner's own credential helper runs for this remote, as it does today");
        tokenRunner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_remote_that_is_not_on_github_gets_no_keys_at_all_through_the_runner()
    {
        RecordingProcessRunner tokenRunner = RecordingProcessRunner.Succeeding(Token + "\n");
        ProjectGitRemoteTokenSource source = new(
            Resolves(Account), ClientReading(tokenRunner), Origin("https://ghe.example.com/o/r.git").Runner);
        using IDisposable scope = GitRemoteAuthentication.UseForCurrentFlow(source);
        List<GitRemoteAuthorization?> handed = [];

        await GitRemoteAuthentication.RunAsync(
            "git", ["fetch", "origin"], "/repos/hall9k",
            remote => { handed.Add(remote); return Task.FromResult((ExitCode: 0, StandardError: string.Empty)); },
            result => result, CancellationToken.None);

        handed.Should().ContainSingle().Which.Should().BeNull(
            "no header and no emptied helper reach a host that is not github.com");
    }

    [Fact]
    public async Task A_repository_that_belongs_to_no_project_or_has_no_confirmed_account_gets_no_token()
    {
        RecordingProcessRunner tokenRunner = RecordingProcessRunner.Succeeding(Token + "\n");
        ProjectGitRemoteTokenSource source = new(
            Resolves(null), ClientReading(tokenRunner), Origin("https://github.com/o/r.git").Runner);

        (await source.TryGetAsync("/elsewhere", CancellationToken.None)).Should().BeNull();
        tokenRunner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task One_shared_client_starts_gh_auth_token_once_across_many_git_calls()
    {
        RecordingProcessRunner tokenRunner = RecordingProcessRunner.Succeeding(Token + "\n");
        ProjectGitRemoteTokenSource source = new(
            Resolves(Account), ClientReading(tokenRunner), Origin("https://github.com/o/r.git").Runner);

        for (int call = 0; call < 20; call++)
        {
            (await source.TryGetAsync("/repos/hall9k", CancellationToken.None)).Should().NotBeNull();
        }

        tokenRunner.Calls.Should().ContainSingle("the client's cache holds across the calls");
    }

    [Fact]
    public async Task A_dropped_token_is_read_again_by_the_next_call()
    {
        RecordingProcessRunner tokenRunner = RecordingProcessRunner.Succeeding(Token + "\n");
        ProjectGitRemoteTokenSource source = new(
            Resolves(Account), ClientReading(tokenRunner), Origin("https://github.com/o/r.git").Runner);
        await source.TryGetAsync("/repos/hall9k", CancellationToken.None);

        source.Invalidate("/repos/hall9k");
        await source.TryGetAsync("/repos/hall9k", CancellationToken.None);

        tokenRunner.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_failed_gh_auth_token_gives_no_token_and_never_puts_its_standard_output_anywhere()
    {
        const string leaked = "gho_LEAKEDFROMSTDOUT";
        RecordingProcessRunner tokenRunner = new(() => new ProcessResult(1, leaked, "gh: not logged in"));
        ProjectGitHubClient client = ClientReading(tokenRunner);

        Func<Task> read = () => client.TokenAsync("hallmanac", "/repos/hall9k", CancellationToken.None);
        DomainValidationException thrown = (await read.Should().ThrowAsync<DomainValidationException>()).Which;

        thrown.Message.Should().NotContain(leaked);
        thrown.ToString().Should().NotContain(leaked);

        ProjectGitRemoteTokenSource source = new(
            Resolves(Account), client, Origin("https://github.com/o/r.git").Runner);
        (await source.TryGetAsync("/repos/hall9k", CancellationToken.None)).Should().BeNull(
            "no token can be had, so the call runs without the keys");
    }

    [Fact]
    public async Task A_gh_that_cannot_be_run_gives_no_token_instead_of_an_exception()
    {
        RecordingProcessRunner tokenRunner = RecordingProcessRunner.Unstartable(
            new System.ComponentModel.Win32Exception("gh was not found"));
        ProjectGitRemoteTokenSource source = new(
            Resolves(Account), ClientReading(tokenRunner), Origin("https://github.com/o/r.git").Runner);

        (await source.TryGetAsync("/repos/hall9k", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task A_store_that_is_down_gives_no_token_and_is_asked_once_for_a_while_not_once_per_git_call()
    {
        int lookups = 0;
        RecordingProcessRunner tokenRunner = RecordingProcessRunner.Succeeding(Token);
        ProjectGitRemoteTokenSource source = new(
            (_, _) => { lookups++; throw new InvalidOperationException("connection refused"); },
            ClientReading(tokenRunner), Origin("https://github.com/o/r.git").Runner);

        (await source.TryGetAsync("/repos/hall9k", CancellationToken.None)).Should().BeNull();
        (await source.TryGetAsync("/repos/hall9k", CancellationToken.None)).Should().BeNull();

        lookups.Should().Be(1);
        tokenRunner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_cancelled_call_is_still_cancelled()
    {
        RecordingProcessRunner tokenRunner = RecordingProcessRunner.Succeeding(Token);
        ProjectGitRemoteTokenSource source = new(
            (_, cancellationToken) => Task.FromCanceled<ProjectGitHubAccount?>(new CancellationToken(canceled: true)),
            ClientReading(tokenRunner), Origin("https://github.com/o/r.git").Runner);

        Func<Task> run = () => source.TryGetAsync("/repos/hall9k", CancellationToken.None);

        await run.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void The_origin_read_does_not_itself_reach_a_remote()
    {
        // A source that asked a runner which authorizes remote calls would recurse; the one read it
        // makes is "git remote get-url origin", which reaches nothing.
        GitRemoteAuthentication.ReachesRemote("git", ["remote", "get-url", "origin"]).Should().BeFalse();
    }

    [Fact]
    public void A_worktree_is_followed_to_the_repository_it_was_cut_from_without_starting_git()
    {
        string root = Path.Combine(Path.GetTempPath(), $"h9k-locator-{Guid.NewGuid():N}");
        try
        {
            string bare = Path.Combine(root, "repo", "hall9k.git");
            string worktree = Path.Combine(root, "repo", "wt-1");
            Directory.CreateDirectory(Path.Combine(bare, "objects"));
            File.WriteAllText(Path.Combine(bare, "HEAD"), "ref: refs/heads/main\n");
            Directory.CreateDirectory(Path.Combine(bare, "worktrees", "wt-1"));
            Directory.CreateDirectory(Path.Combine(worktree, "src", "deep"));
            File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {bare.Replace('\\', '/')}/worktrees/wt-1\n");

            IReadOnlyList<string> fromWorktree = GitRepositoryLocator.Candidates(worktree);
            IReadOnlyList<string> fromSubdirectory = GitRepositoryLocator.Candidates(Path.Combine(worktree, "src", "deep"));
            IReadOnlyList<string> fromBare = GitRepositoryLocator.Candidates(bare);

            fromWorktree.Should().Contain(Path.GetFullPath(bare));
            fromSubdirectory.Should().Contain(Path.GetFullPath(bare));
            fromBare.Should().Contain(bare);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void An_ordinary_checkouts_dot_git_directory_names_the_checkout()
    {
        string root = Path.Combine(Path.GetTempPath(), $"h9k-locator-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            Directory.CreateDirectory(Path.Combine(root, "sub"));

            GitRepositoryLocator.Candidates(Path.Combine(root, "sub")).Should().Contain(Path.GetFullPath(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

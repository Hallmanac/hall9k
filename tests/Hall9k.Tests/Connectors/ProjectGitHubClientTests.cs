using FluentAssertions;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The one gh helper A2b's own calls go through (idea 202383dc, A2b, item 4): it runs a specific
/// account rather than whichever account the machine's <c>gh</c> is logged into, pinning that
/// account's token to the one invocation as <c>GH_TOKEN</c> — read fresh, per call, from an ambient
/// <c>GH_TOKEN</c>/<c>GITHUB_TOKEN</c> when either is set (the same precedence and the same
/// environment <c>gh api user</c> itself already reads to confirm the login in the first place),
/// falling back to <c>gh auth token --user &lt;login&gt;</c> only when neither is set. Account
/// resolution itself (<see cref="ProjectGitHubClient.ResolveAccountAsync"/>) needs a real Marten
/// session and is exercised by <c>ProjectJoinCommandTests</c> instead, per Brian's 2026-09-13
/// testing rule.
/// </summary>
public sealed class ProjectGitHubClientTests
{
    private static readonly ProjectGitHubAccount Account = new(4181388, "hallmanac");

    /// <summary>No ambient token anywhere, so every existing test's "gh auth token" path is reached
    /// exactly the way it was before the ambient-environment check existed.</summary>
    private static string? NoAmbientToken(string name) => null;

    [Fact]
    public async Task RunAsync_pins_the_accounts_own_token_as_GH_TOKEN_for_this_call_alone()
    {
        RecordingProcessRunner tokenRunner = RecordingProcessRunner.Succeeding("token-for-hallmanac\n");
        RecordingEnvironmentProcessRunner ghRunner = RecordingEnvironmentProcessRunner.Succeeding("{}");
        ProjectGitHubClient client = new(ghRunner.Runner, tokenRunner.Runner, NoAmbientToken);

        await client.RunAsync(Account, "/repos/hall9k", ["repo", "view"], CancellationToken.None);

        tokenRunner.Calls.Single().Arguments.Should().ContainInOrder("auth", "token", "--user", "hallmanac");
        ghRunner.Calls.Single().Environment["GH_TOKEN"].Should().Be("token-for-hallmanac");
        ghRunner.Calls.Single().Arguments.Should().ContainInOrder("repo", "view");
    }

    [Fact]
    public async Task RunAsync_never_reaches_gh_at_all_when_the_token_read_fails()
    {
        RecordingProcessRunner tokenRunner = RecordingProcessRunner.Failing("gh: no oauth token found for 'hallmanac'");
        RecordingEnvironmentProcessRunner ghRunner = RecordingEnvironmentProcessRunner.Succeeding("{}");
        ProjectGitHubClient client = new(ghRunner.Runner, tokenRunner.Runner, NoAmbientToken);

        Func<Task> run = () => client.RunAsync(Account, "/repos/hall9k", ["repo", "view"], CancellationToken.None);

        (await run.Should().ThrowAsync<DomainValidationException>()).WithMessage("*gh auth token --user hallmanac*");
        ghRunner.Calls.Should().BeEmpty("a token this install could not produce is never presented to a real command");
    }

    /// <summary>
    /// The identity read that confirmed this login (<c>NodeBootstrap</c>'s own <c>gh api user</c>)
    /// resolves an ambient <c>GH_TOKEN</c> before ever consulting the keyring; <c>gh auth token
    /// --user</c> does the opposite and never reads the environment at all. Left unreconciled, an
    /// install signed in purely through an exported token with no matching keyring entry could
    /// confirm a login it could then never produce a token for (independent pre-PR review, cycle 1,
    /// adversarial lens, medium).
    /// </summary>
    [Fact]
    public async Task RunAsync_uses_an_ambient_GH_TOKEN_instead_of_asking_gh_for_one()
    {
        RecordingProcessRunner tokenRunner = RecordingProcessRunner.Succeeding("token-for-hallmanac\n");
        RecordingEnvironmentProcessRunner ghRunner = RecordingEnvironmentProcessRunner.Succeeding("{}");
        ProjectGitHubClient client = new(
            ghRunner.Runner, tokenRunner.Runner, name => name == "GH_TOKEN" ? "ambient-gh-token" : null);

        await client.RunAsync(Account, "/repos/hall9k", ["repo", "view"], CancellationToken.None);

        tokenRunner.Calls.Should().BeEmpty(
            "an ambient GH_TOKEN is the same token gh api user itself would already have used to confirm this login");
        ghRunner.Calls.Single().Environment["GH_TOKEN"].Should().Be("ambient-gh-token");
    }

    [Fact]
    public async Task RunAsync_falls_back_to_an_ambient_GITHUB_TOKEN_when_GH_TOKEN_is_unset()
    {
        RecordingProcessRunner tokenRunner = RecordingProcessRunner.Succeeding("token-for-hallmanac\n");
        RecordingEnvironmentProcessRunner ghRunner = RecordingEnvironmentProcessRunner.Succeeding("{}");
        ProjectGitHubClient client = new(
            ghRunner.Runner, tokenRunner.Runner, name => name == "GITHUB_TOKEN" ? "ambient-github-token" : null);

        await client.RunAsync(Account, "/repos/hall9k", ["repo", "view"], CancellationToken.None);

        tokenRunner.Calls.Should().BeEmpty();
        ghRunner.Calls.Single().Environment["GH_TOKEN"].Should().Be("ambient-github-token");
    }

    [Fact]
    public async Task RunAsync_prefers_GH_TOKEN_over_GITHUB_TOKEN_when_both_are_set()
    {
        RecordingProcessRunner tokenRunner = RecordingProcessRunner.Succeeding("token-for-hallmanac\n");
        RecordingEnvironmentProcessRunner ghRunner = RecordingEnvironmentProcessRunner.Succeeding("{}");
        ProjectGitHubClient client = new(
            ghRunner.Runner, tokenRunner.Runner,
            name => name switch { "GH_TOKEN" => "gh-token-value", "GITHUB_TOKEN" => "github-token-value", _ => null });

        await client.RunAsync(Account, "/repos/hall9k", ["repo", "view"], CancellationToken.None);

        ghRunner.Calls.Single().Environment["GH_TOKEN"].Should().Be("gh-token-value");
    }

    /// <summary>
    /// <see cref="ProjectGitHubClient.RunAsync"/> itself must NOT translate a hung gh's
    /// <see cref="TimeoutException"/>: it is now every connector's shared seam
    /// (<c>ProjectScopedGitHubRunner</c>), and each connector already turns a hang into its own
    /// richer, per-operation message — a blanket catch here converted every one of those into this
    /// method's own generic wording before any connector's own handling ever saw the exception
    /// (independent pre-PR review, cycle 1, adversarial lens). The one caller with no handling of
    /// its own, <see cref="ProjectGitHubAccessMirror.ObserveAsync"/>, carries this translation
    /// itself now — see <c>ProjectGitHubAccessMirrorTests</c> — so this test locks in that the raw
    /// exception propagates from here instead.
    /// </summary>
    [Fact]
    public async Task RunAsync_lets_a_hung_gh_commands_TimeoutException_propagate_for_the_caller_to_translate()
    {
        RecordingProcessRunner tokenRunner = RecordingProcessRunner.Succeeding("token-for-hallmanac\n");
        RecordingEnvironmentProcessRunner ghRunner = new(() => throw new TimeoutException(
            "gh did not answer within 120 seconds, so Hall9k stopped waiting and ended it."));
        ProjectGitHubClient client = new(ghRunner.Runner, tokenRunner.Runner, NoAmbientToken);

        Func<Task> run = () => client.RunAsync(Account, "/repos/hall9k", ["repo", "view"], CancellationToken.None);

        (await run.Should().ThrowAsync<TimeoutException>()).WithMessage("*did not answer*");
    }

    [Fact]
    public async Task AsProcessRunner_runs_gh_as_the_bound_account_in_the_plain_ProcessRunner_shape()
    {
        RecordingProcessRunner tokenRunner = RecordingProcessRunner.Succeeding("token-for-hallmanac\n");
        RecordingEnvironmentProcessRunner ghRunner = RecordingEnvironmentProcessRunner.Succeeding("{}");
        ProjectGitHubClient client = new(ghRunner.Runner, tokenRunner.Runner, NoAmbientToken);
        ProcessRunner bound = client.AsProcessRunner(Account);

        await bound("gh", ["repo", "view"], "/repos/hall9k", CancellationToken.None);

        tokenRunner.Calls.Single().Arguments.Should().ContainInOrder("auth", "token", "--user", "hallmanac");
        ghRunner.Calls.Single().Environment["GH_TOKEN"].Should().Be("token-for-hallmanac");
    }

    [Fact]
    public async Task AsProcessRunner_refuses_anything_other_than_gh()
    {
        ProjectGitHubClient client = new();
        ProcessRunner bound = client.AsProcessRunner(Account);

        Func<Task> run = () => bound("git", ["status"], "/repos/hall9k", CancellationToken.None);

        await run.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task RunAmbientAsync_runs_gh_with_no_account_pinned()
    {
        RecordingEnvironmentProcessRunner ghRunner = RecordingEnvironmentProcessRunner.Succeeding("{}");
        ProjectGitHubClient client = new(ghRunner.Runner);

        await client.RunAmbientAsync("/tmp", ["release", "download"], CancellationToken.None);

        ghRunner.Calls.Single().Arguments.Should().ContainInOrder("release", "download");
        ghRunner.Calls.Single().Environment.Should().BeEmpty("no account token is pinned in ambient mode");
    }

    [Fact]
    public async Task AmbientProcessRunner_runs_gh_ambiently_in_the_plain_ProcessRunner_shape()
    {
        RecordingEnvironmentProcessRunner ghRunner = RecordingEnvironmentProcessRunner.Succeeding("{}");
        ProjectGitHubClient client = new(ghRunner.Runner);
        ProcessRunner bound = client.AmbientProcessRunner;

        await bound("gh", ["release", "download"], "/tmp", CancellationToken.None);

        ghRunner.Calls.Single().Arguments.Should().ContainInOrder("release", "download");
        ghRunner.Calls.Single().Environment.Should().BeEmpty("no account token is pinned in ambient mode");
    }

    [Fact]
    public async Task AmbientProcessRunner_refuses_anything_other_than_gh()
    {
        ProjectGitHubClient client = new();
        ProcessRunner bound = client.AmbientProcessRunner;

        Func<Task> run = () => bound("git", ["status"], "/tmp", CancellationToken.None);

        await run.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// The seam <c>NodeBootstrap</c>'s own bootstrap now reads gh's identity through, rather than a
    /// raw process of its own (independent pre-PR review, cycle 1, human verdict) — proven here the
    /// same way <see cref="RunAmbientAsync_runs_gh_with_no_account_pinned"/> proves the transport it
    /// wraps, with no real gh in the loop.
    /// </summary>
    [Fact]
    public void AmbientIdentityReader_returns_gh_api_users_own_output_with_no_account_pinned()
    {
        RecordingEnvironmentProcessRunner ghRunner = RecordingEnvironmentProcessRunner.Succeeding("""{"id": 1, "login": "hallmanac"}""" + "\n");
        GhIdentityReader reader = ProjectGitHubClient.AmbientIdentityReader("/tmp", ghRunner.Runner);

        string? json = reader();

        json.Should().Be("""{"id": 1, "login": "hallmanac"}""");
        ghRunner.Calls.Single().Arguments.Should().ContainInOrder("api", "user");
        ghRunner.Calls.Single().Environment.Should().BeEmpty("no account is confirmed yet to pin a token for");
    }

    [Fact]
    public void AmbientIdentityReader_returns_null_when_gh_exits_non_zero()
    {
        RecordingEnvironmentProcessRunner ghRunner = RecordingEnvironmentProcessRunner.Failing("gh: not authenticated");
        GhIdentityReader reader = ProjectGitHubClient.AmbientIdentityReader("/tmp", ghRunner.Runner);

        reader().Should().BeNull();
    }

    /// <summary>
    /// Best-effort, matching the raw spawn this replaced: a gh that cannot even be reached (not
    /// installed, wedged past its deadline) reads as unconfirmed rather than throwing out of
    /// <c>NodeBootstrap.EnsureAsync</c>/<c>RefreshGitHubIdentityAsync</c>.
    /// </summary>
    [Fact]
    public void AmbientIdentityReader_returns_null_rather_than_throwing_when_gh_cannot_be_reached()
    {
        EnvironmentProcessRunner throwing = (_, _, _, _, _) => throw new TimeoutException("gh did not answer within 3 seconds");
        GhIdentityReader reader = ProjectGitHubClient.AmbientIdentityReader("/tmp", throwing);

        reader().Should().BeNull();
    }
}

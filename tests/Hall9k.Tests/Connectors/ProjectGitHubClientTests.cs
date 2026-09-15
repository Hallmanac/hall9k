using FluentAssertions;
using Hall9k.Connectors.WorkItems;
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
    /// The token read already turned a hung gh into a <see cref="DomainValidationException"/>
    /// before this fix; the actual command run here never did, so it escaped standalone
    /// <c>h9k project join</c> as an unhandled <see cref="TimeoutException"/> and a stack trace
    /// instead of the reason on stderr AGENTS.md's CLI standard requires (independent pre-PR
    /// review, cycle 1, conformance and adversarial lenses, both low).
    /// </summary>
    [Fact]
    public async Task RunAsync_reports_a_hung_gh_command_as_a_domain_exception_rather_than_crashing()
    {
        RecordingProcessRunner tokenRunner = RecordingProcessRunner.Succeeding("token-for-hallmanac\n");
        RecordingEnvironmentProcessRunner ghRunner = new(() => throw new TimeoutException(
            "gh did not answer within 120 seconds, so Hall9k stopped waiting and ended it."));
        ProjectGitHubClient client = new(ghRunner.Runner, tokenRunner.Runner, NoAmbientToken);

        Func<Task> run = () => client.RunAsync(Account, "/repos/hall9k", ["repo", "view"], CancellationToken.None);

        (await run.Should().ThrowAsync<DomainValidationException>()).WithMessage("*did not answer*");
    }
}

using FluentAssertions;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The one gh helper A2b's own calls go through (idea 202383dc, A2b, item 4): it runs a specific
/// account rather than whichever account the machine's <c>gh</c> is logged into, pinning that
/// account's token to the one invocation as <c>GH_TOKEN</c> — read fresh, per call, with
/// <c>gh auth token --user &lt;login&gt;</c>. Account resolution itself
/// (<see cref="ProjectGitHubClient.ResolveAccountAsync"/>) needs a real Marten session and is
/// exercised by <c>ProjectJoinCommandTests</c> instead, per Brian's 2026-09-13 testing rule.
/// </summary>
public sealed class ProjectGitHubClientTests
{
    private static readonly ProjectGitHubAccount Account = new(4181388, "hallmanac");

    [Fact]
    public async Task RunAsync_pins_the_accounts_own_token_as_GH_TOKEN_for_this_call_alone()
    {
        RecordingProcessRunner tokenRunner = RecordingProcessRunner.Succeeding("token-for-hallmanac\n");
        RecordingEnvironmentProcessRunner ghRunner = RecordingEnvironmentProcessRunner.Succeeding("{}");
        ProjectGitHubClient client = new(ghRunner.Runner, tokenRunner.Runner);

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
        ProjectGitHubClient client = new(ghRunner.Runner, tokenRunner.Runner);

        Func<Task> run = () => client.RunAsync(Account, "/repos/hall9k", ["repo", "view"], CancellationToken.None);

        (await run.Should().ThrowAsync<DomainValidationException>()).WithMessage("*gh auth token --user hallmanac*");
        ghRunner.Calls.Should().BeEmpty("a token this install could not produce is never presented to a real command");
    }
}

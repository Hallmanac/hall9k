using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Credentials;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The check a publish that creates a Linear issue makes before it commits: the team is resolved
/// through Linear, and every way that can fail refuses with a non-zero exit (a thrown
/// <see cref="DomainException"/>) while the task is still a Draft, naming the remedy and
/// <c>--untracked</c> as the way to publish without Linear. Responses are fixtures written from
/// Linear's public schema; none reaches a live workspace.
/// </summary>
public sealed class LinearPublishResolveTests : IDisposable
{
    private const string KeyFileName = "linear-resolve-test-key";
    private static readonly Uri Workspace = new("https://linear.app/hall9k");

    private readonly ScopedTestHome _home = new();
    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromMinutes(1));
    private readonly Guid _taskId = DomainId.New();

    private CancellationToken Token => _cancellation.Token;

    public LinearPublishResolveTests()
    {
        Directory.CreateDirectory(CredentialVault.Directory);
        File.WriteAllText(CredentialVault.FileFor(KeyFileName), "lin_api_resolve_test_key");
    }

    public void Dispose()
    {
        _cancellation.Dispose();
        _home.Dispose();
    }

    private static ProjectDetails Project(string? team = "ENG") =>
        new()
        {
            Id = DomainId.New(),
            Name = "hall9k",
            LinearTeam = team is null ? LinearTeamKey.None : LinearTeamKey.Parse(team),
        };

    private static LinearWorkItemProvider Provider(List<LinearRequest> seen, Func<Task<LinearResponse>> answer) =>
        new(
            LinearAccount.Registered(Workspace, "Brian Hall", CredentialReference.File(KeyFileName)),
            (request, _) =>
            {
                seen.Add(request);
                return answer();
            });

    private static Func<Task<LinearResponse>> Answering(int status, string body) =>
        () => Task.FromResult(new LinearResponse(status, body));

    private async Task<DomainException> RefusalAsync(LinearWorkItemProvider? provider, ProjectDetails project)
    {
        Func<Task> resolve = () => LinearPublishTracking.ResolveAsync(provider, project, _taskId, Token);
        return (await resolve.Should().ThrowAsync<DomainException>()).Which;
    }

    private void AssertNamesTheWayToPublishWithoutLinear(DomainException refusal)
    {
        string shortId = DomainId.Short(_taskId);
        refusal.Message.Should().Contain($"Task {shortId} was not published")
            .And.Contain("nothing was created in Linear")
            .And.Contain($"h9k task publish {shortId} --untracked");
    }

    [Fact]
    public async Task A_team_Linear_can_see_resolves_with_its_id_and_default_state_in_one_call()
    {
        List<LinearRequest> seen = [];
        LinearWorkItemProvider provider = Provider(seen, Answering(
            200,
            """{"data":{"teams":{"nodes":[{"id":"team-1","key":"ENG","name":"Engineering","defaultIssueState":{"id":"state-backlog"}}]}}}"""));

        LinearPublishTarget target = await LinearPublishTracking.ResolveAsync(provider, Project(), _taskId, Token);

        target.Team.Should().Be(new LinearTeam("team-1", "ENG", "Engineering", "state-backlog"));
        seen.Should().ContainSingle();
    }

    [Fact]
    public async Task No_Linear_connection_on_this_install_refuses_naming_the_connection_command()
    {
        DomainException refusal = await RefusalAsync(null, Project());

        AssertNamesTheWayToPublishWithoutLinear(refusal);
        refusal.Message.Should().Contain("h9k connection add linear");
    }

    [Fact]
    public async Task A_rejected_key_refuses_without_asking_anything_else()
    {
        List<LinearRequest> seen = [];
        LinearWorkItemProvider provider = Provider(seen, Answering(
            401, """{"errors":[{"message":"Authentication required","extensions":{"type":"authentication error"}}]}"""));

        DomainException refusal = await RefusalAsync(provider, Project());

        AssertNamesTheWayToPublishWithoutLinear(refusal);
        refusal.Message.Should().Contain("rejected the API key").And.Contain("h9k connection add linear");
        seen.Should().ContainSingle();
    }

    [Fact]
    public async Task A_project_with_no_team_bound_refuses_naming_the_command_that_binds_one_and_asks_Linear_nothing()
    {
        List<LinearRequest> seen = [];
        LinearWorkItemProvider provider = Provider(seen, Answering(200, "{}"));

        DomainException refusal = await RefusalAsync(provider, Project(team: null));

        AssertNamesTheWayToPublishWithoutLinear(refusal);
        refusal.Message.Should().Contain("h9k project set hall9k --linear <TEAM>");
        seen.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_team_refuses_saying_it_may_not_exist_or_the_key_may_be_limited()
    {
        LinearWorkItemProvider provider = Provider([], Answering(200, """{"data":{"teams":{"nodes":[]}}}"""));

        DomainException refusal = await RefusalAsync(provider, Project());

        AssertNamesTheWayToPublishWithoutLinear(refusal);
        refusal.Message.Should().Contain("no team ENG").And.Contain("may not exist").And.Contain("limited to other teams");
    }

    [Fact]
    public async Task Linear_not_answering_refuses_rather_than_publishing_unlinked()
    {
        LinearWorkItemProvider provider = Provider([], Answering(500, "Service Unavailable"));

        DomainException refusal = await RefusalAsync(provider, Project());

        AssertNamesTheWayToPublishWithoutLinear(refusal);
        refusal.Message.Should().Contain("Linear failed");
    }

    [Fact]
    public async Task A_timeout_refuses_too()
    {
        LinearWorkItemProvider provider = Provider(
            [], () => Task.FromException<LinearResponse>(new TaskCanceledException("timed out")));

        DomainException refusal = await RefusalAsync(provider, Project());

        AssertNamesTheWayToPublishWithoutLinear(refusal);
        refusal.Message.Should().Contain("did not answer");
    }
}

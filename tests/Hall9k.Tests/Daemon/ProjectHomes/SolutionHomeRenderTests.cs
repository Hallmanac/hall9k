using FluentAssertions;
using Hall9k.Daemon.ProjectHomes;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Project.Rendering;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hall9k.Tests.Daemon.ProjectHomes;

/// <summary>
/// The render sweep's solution step, run over registered projects in memory against a temporary
/// platform home: it keeps an existing solution home's AGENTS.md current as the membership, the
/// name and this node's joins change, writes only when the bytes differ, and never invents a home.
/// </summary>
public sealed class SolutionHomeRenderTests : IDisposable
{
    private const string ApiKey = "01JAAAAAAAAAAAAAAAAAAAAAAA";
    private const string WebKey = "01JBBBBBBBBBBBBBBBBBBBBBBB";
    private const string Fingerprint = "root-fingerprint";

    private readonly ScopedTestHome _scopedHome = new();
    private readonly Guid _ownerId = DomainId.New();

    private static SolutionMember Api => new(ApiKey, "shop-api", new Uri("https://github.com/acme/shop-api.git"));

    private static SolutionMember Web => new(WebKey, "shop-web", new Uri("https://github.com/acme/shop-web.git"));

    private ProjectDetails Project(SolutionMember member, SolutionMembership carried, bool joined) => new()
    {
        Id = DomainId.New(),
        Name = member.Name,
        ProjectKey = member.ProjectKey,
        OwnerId = _ownerId,
        Solution = carried,
        HomeDirectory = ProjectHome.Parse(Path.Combine(_scopedHome.Home, "projects", member.Name)),
        Members = joined ? new Dictionary<string, ProjectMemberRole> { [Fingerprint] = ProjectMemberRole.Owner } : [],
    };

    private int Sweep(params ProjectDetails[] projects) =>
        ProjectHomeRenderEngine.RenderSolutionHomes(
            projects, new Dictionary<Guid, string?> { [_ownerId] = Fingerprint }, NullLogger.Instance);

    private static string AgentsFor(string solutionName) =>
        File.ReadAllText(ProjectHomePaths.AgentsFile(SolutionHomePaths.DefaultFor(solutionName)));

    private string MakeHome(string name, string content = "")
    {
        string home = SolutionHomePaths.DefaultFor(name);
        Directory.CreateDirectory(home);
        if (content.Length > 0)
        {
            File.WriteAllText(ProjectHomePaths.AgentsFile(home), content);
        }

        return home;
    }

    [Fact]
    public void A_solution_with_no_home_on_this_node_gets_none()
    {
        SolutionMembership membership = SolutionDecider.Create("01JSSSSSSSSSSSSSSSSSSSSSSS", "storefront", [Api]);

        Sweep(Project(Api, membership, joined: true)).Should().Be(0);

        Directory.Exists(SolutionHomePaths.DefaultFor("storefront")).Should().BeFalse();
    }

    [Fact]
    public void An_existing_home_gets_its_agents_file_and_a_second_sweep_writes_nothing()
    {
        SolutionMembership membership = SolutionDecider.Create("01JSSSSSSSSSSSSSSSSSSSSSSS", "storefront", [Api]);
        MakeHome("storefront");
        ProjectDetails api = Project(Api, membership, joined: true);

        Sweep(api).Should().Be(1);
        AgentsFor("storefront").Should().Contain("# storefront: solution home").And.Contain("shop-api");

        Sweep(api).Should().Be(0, "identical bytes are never rewritten");
    }

    [Fact]
    public void A_member_joined_later_and_a_member_replicated_in_each_change_the_next_render()
    {
        const string key = "01JSSSSSSSSSSSSSSSSSSSSSSS";
        MakeHome("storefront");
        SolutionMembership one = SolutionDecider.Create(key, "storefront", [Api]);
        Sweep(Project(Api, one, joined: true));

        SolutionMembership two = SolutionDecider.Create(key, "storefront", [Api, Web]);
        ProjectDetails apiAgain = Project(Api, two, joined: true);
        Sweep(apiAgain).Should().Be(1);
        AgentsFor("storefront").Should().Contain("### shop-web").And.Contain(SolutionAgentsDocument.NotJoinedStatement);

        ProjectDetails web = Project(Web, two, joined: true);
        Sweep(apiAgain, web).Should().Be(1);
        AgentsFor("storefront").Should().NotContain(SolutionAgentsDocument.NotJoinedStatement)
            .And.Contain(web.HomeDirectory.Value);
    }

    [Fact]
    public void A_foreign_agents_file_is_left_alone()
    {
        SolutionMembership membership = SolutionDecider.Create("01JSSSSSSSSSSSSSSSSSSSSSSS", "storefront", [Api]);
        MakeHome("storefront", "# hand written\n");

        Sweep(Project(Api, membership, joined: true)).Should().Be(0);

        AgentsFor("storefront").Should().Be("# hand written\n");
    }

    public void Dispose() => _scopedHome.Dispose();
}

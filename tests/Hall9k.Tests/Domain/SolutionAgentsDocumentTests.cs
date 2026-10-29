using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Project.Rendering;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The solution home's AGENTS.md, asserted on the rendered text: which facts it carries, how a
/// member this node has joined differs from one it has not, and that a rename or a replicated
/// member changes the bytes. No store and no daemon; the render is a pure function of the
/// membership and the registered projects.
/// </summary>
public sealed class SolutionAgentsDocumentTests : IDisposable
{
    private const string ApiKey = "01JAAAAAAAAAAAAAAAAAAAAAAA";
    private const string WebKey = "01JBBBBBBBBBBBBBBBBBBBBBBB";
    private const string AdminKey = "01JCCCCCCCCCCCCCCCCCCCCCCC";
    private const string SolutionKey = "01JSSSSSSSSSSSSSSSSSSSSSSS";
    private const string Fingerprint = "root-fingerprint";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"solution-agents-{Guid.NewGuid():N}");
    private readonly Guid _ownerId = DomainId.New();

    private static SolutionMember Api => new(ApiKey, "shop-api", new Uri("https://github.com/acme/shop-api.git"));

    private static SolutionMember Web => new(WebKey, "shop-web", new Uri("https://github.com/acme/shop-web.git"));

    private static SolutionMember Admin => new(AdminKey, "shop-admin", new Uri("https://github.com/acme/shop-admin.git"));

    private ProjectDetails JoinedProject(SolutionMember member, string home) => new()
    {
        Id = DomainId.New(),
        Name = member.Name,
        ProjectKey = member.ProjectKey,
        OwnerId = _ownerId,
        HomeDirectory = ProjectHome.Parse(home),
        Members = new Dictionary<string, ProjectMemberRole> { [Fingerprint] = ProjectMemberRole.Owner },
    };

    private string? FingerprintOf(Guid ownerId) => ownerId == _ownerId ? Fingerprint : null;

    private string Render(SolutionMembership membership, params ProjectDetails[] projects) =>
        SolutionAgentsDocument.Render(membership, projects, FingerprintOf);

    [Fact]
    public void A_joined_member_is_listed_with_its_repository_url_and_its_home_path_on_this_node()
    {
        string apiHome = Path.Combine(_directory, "projects", "shop-api");
        SolutionMembership membership = SolutionDecider.Create(SolutionKey, "storefront", [Api, Web]);

        string text = Render(membership, JoinedProject(Api, apiHome));

        text.Should().Contain("# storefront: solution home")
            .And.Contain(SolutionAgentsDocument.GeneratedMarker)
            .And.Contain("### shop-api")
            .And.Contain("- Repository: https://github.com/acme/shop-api.git")
            .And.Contain($"- Home on this node: {apiHome}");
    }

    [Fact]
    public void A_member_this_node_has_not_joined_is_listed_with_a_plain_statement_and_no_home_path()
    {
        SolutionMembership membership = SolutionDecider.Create(SolutionKey, "storefront", [Api, Web]);

        string text = Render(membership, JoinedProject(Api, Path.Combine(_directory, "shop-api")));

        string webSection = text[text.IndexOf("### shop-web", StringComparison.Ordinal)..];
        webSection.Should().Contain("- Repository: https://github.com/acme/shop-web.git")
            .And.Contain($"- Home on this node: {SolutionAgentsDocument.NotJoinedStatement}");
    }

    [Fact]
    public void A_project_registered_here_but_not_joined_reads_as_not_joined()
    {
        SolutionMembership membership = SolutionDecider.Create(SolutionKey, "storefront", [Api]);
        ProjectDetails registered = JoinedProject(Api, Path.Combine(_directory, "shop-api"));
        registered.Members = [];

        string text = Render(membership, registered);

        text.Should().Contain(SolutionAgentsDocument.NotJoinedStatement)
            .And.NotContain(_directory);
    }

    [Fact]
    public void Joining_a_member_later_changes_the_render_from_the_statement_to_the_path()
    {
        SolutionMembership membership = SolutionDecider.Create(SolutionKey, "storefront", [Api, Web]);
        string webHome = Path.Combine(_directory, "projects", "shop-web");

        string before = Render(membership, JoinedProject(Api, Path.Combine(_directory, "shop-api")));
        string after = Render(
            membership, JoinedProject(Api, Path.Combine(_directory, "shop-api")), JoinedProject(Web, webHome));

        before.Should().NotBe(after);
        before.Should().Contain(SolutionAgentsDocument.NotJoinedStatement);
        after.Should().NotContain(SolutionAgentsDocument.NotJoinedStatement)
            .And.Contain($"- Home on this node: {webHome}");
    }

    [Fact]
    public void A_renamed_solution_carries_the_new_name_and_the_same_key()
    {
        SolutionMembership membership = SolutionDecider.Create(SolutionKey, "storefront", [Api]);
        SolutionMembership renamed = SolutionDecider.Rename(membership, "marketplace");

        string text = Render(renamed);

        text.Should().Contain("# marketplace: solution home")
            .And.Contain("h9k solution show \"marketplace\"")
            .And.Contain($"Solution key: `{SolutionKey}`")
            .And.NotContain("storefront");
    }

    [Fact]
    public void A_member_added_by_replication_appears_in_the_next_render()
    {
        SolutionMembership membership = SolutionDecider.Create(SolutionKey, "storefront", [Api, Web]);
        SolutionMembership replicated = SolutionDecider.Create(SolutionKey, "storefront", [Api, Web, Admin]);

        string before = Render(membership);
        string after = Render(replicated);

        before.Should().NotContain("shop-admin");
        after.Should().Contain("### shop-admin")
            .And.Contain("- Repository: https://github.com/acme/shop-admin.git")
            .And.Contain(SolutionAgentsDocument.NotJoinedStatement);
    }

    [Fact]
    public void A_closed_solution_says_so_with_its_reason()
    {
        SolutionMembership closed = SolutionDecider.Close(
            SolutionDecider.Create(SolutionKey, "storefront", [Api]), "retired for the marketplace");

        Render(closed).Should().Contain("State: closed (retired for the marketplace)");
    }

    [Fact]
    public void Writing_creates_the_file_then_leaves_identical_bytes_alone_and_rewrites_a_changed_render()
    {
        Directory.CreateDirectory(_directory);
        SolutionMembership membership = SolutionDecider.Create(SolutionKey, "storefront", [Api]);
        string first = Render(membership);
        string path = ProjectHomePaths.AgentsFile(_directory);

        SolutionAgentsDocument.Write(_directory, SolutionKey, first).Should().Be(SolutionAgentsWriteOutcome.Created);
        DateTime stamped = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamped);

        SolutionAgentsDocument.Write(_directory, SolutionKey, first).Should().Be(SolutionAgentsWriteOutcome.Unchanged);
        File.GetLastWriteTimeUtc(path).Should().Be(stamped, "identical bytes are never rewritten");

        string second = Render(SolutionDecider.Create(SolutionKey, "storefront", [Api, Web]));
        SolutionAgentsDocument.Write(_directory, SolutionKey, second).Should().Be(SolutionAgentsWriteOutcome.Rewritten);
        File.ReadAllText(path).Should().Be(second);
    }

    [Fact]
    public void A_file_the_platform_did_not_render_is_refused_and_left_untouched()
    {
        Directory.CreateDirectory(_directory);
        string path = ProjectHomePaths.AgentsFile(_directory);
        File.WriteAllText(path, "# My own notes\n");
        SolutionMembership membership = SolutionDecider.Create(SolutionKey, "storefront", [Api]);

        SolutionAgentsDocument.IsForeign(_directory, SolutionKey).Should().BeTrue();
        SolutionAgentsDocument.Write(_directory, SolutionKey, Render(membership))
            .Should().Be(SolutionAgentsWriteOutcome.Refused);
        File.ReadAllText(path).Should().Be("# My own notes\n");
    }

    [Fact]
    public void Another_solutions_render_at_the_same_path_is_refused_too()
    {
        Directory.CreateDirectory(_directory);
        SolutionMembership other = SolutionDecider.Create("01JOOOOOOOOOOOOOOOOOOOOOOO", "storefront", [Api]);
        File.WriteAllText(ProjectHomePaths.AgentsFile(_directory), Render(other));

        SolutionAgentsDocument.IsForeign(_directory, SolutionKey).Should().BeTrue();
        SolutionAgentsDocument.IsForeign(_directory, "01JOOOOOOOOOOOOOOOOOOOOOOO").Should().BeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}

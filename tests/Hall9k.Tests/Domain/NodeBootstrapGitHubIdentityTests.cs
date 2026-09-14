using FluentAssertions;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The pure mapping half of the bootstrap's GitHub identity read (idea 202383dc, A2b, item 1),
/// split from the raw <c>gh api user</c> call so it is testable against recorded output without a
/// process in the loop — mirroring every other GitHub-JSON mapper in this codebase
/// (<c>GitHubReviewAssignments.ParseReviewRequested</c>, for one).
/// </summary>
public sealed class NodeBootstrapGitHubIdentityTests
{
    [Fact]
    public void ParseGhIdentity_reads_the_numeric_id_and_login_gh_reports()
    {
        NodeBootstrap.GitHubAccountIdentity? identity = NodeBootstrap.ParseGhIdentity(
            """{"login":"hallmanac","id":4181388,"name":"Brian Hall"}""");

        identity.Should().NotBeNull();
        identity!.Id.Should().Be(4181388);
        identity.Login.Should().Be("hallmanac");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("""{"login":"hallmanac"}""")]
    [InlineData("""{"id":4181388}""")]
    [InlineData("""{"id":"not-a-number","login":"hallmanac"}""")]
    [InlineData("[]")]
    public void ParseGhIdentity_reads_null_rather_than_throwing_on_anything_unreadable(string? json)
    {
        NodeBootstrap.ParseGhIdentity(json).Should().BeNull();
    }
}

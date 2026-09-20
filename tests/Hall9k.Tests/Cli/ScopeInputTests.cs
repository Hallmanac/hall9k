using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// idea 8c5993c5: the exact wording <c>h9k idea show</c> and <c>h9k task show</c> print for a
/// scope — pinned as a golden, since both commands render it through this one shared method
/// rather than composing their own markup.
/// </summary>
public sealed class ScopeInputTests
{
    [Fact]
    public void Private_scope_reads_this_node_only()
    {
        ScopeInput.Markup(ReplicationScope.Private).Should().Be("[red]Private[/] [dim](this node only)[/]");
    }

    [Fact]
    public void Fleet_scope_reads_every_node_this_owner_runs()
    {
        ScopeInput.Markup(ReplicationScope.Fleet).Should().Be("[yellow]Fleet[/] [dim](every node this owner runs)[/]");
    }

    [Fact]
    public void Team_scope_reads_every_project_members_own_fleet()
    {
        ScopeInput.Markup(ReplicationScope.Team).Should().Be("[green]Team[/] [dim](every project member's own fleet)[/]");
    }

    [Theory]
    [InlineData("private", "Private")]
    [InlineData("PRIVATE", "Private")]
    [InlineData("fleet", "Fleet")]
    [InlineData("Team", "Team")]
    public void Parse_is_case_insensitive_and_recognizes_the_three_words(string input, string expected)
    {
        ScopeInput.Parse(input).Value.Should().Be(expected);
    }

    [Fact]
    public void Parse_refuses_anything_else_and_names_the_three_words()
    {
        Action act = () => ScopeInput.Parse("public");

        act.Should().Throw<DomainValidationException>().WithMessage("*private*fleet*team*");
    }
}

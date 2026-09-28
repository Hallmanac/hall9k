using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The line <c>h9k project show</c> prints for a project's verify gates. An unaccepted set is
/// marked, never hidden (task: the project home's generated AGENTS.md never lists an unaccepted
/// gate) — <c>h9k project accept-gates</c> is where an operator reviews the actual command list
/// for vetting, this row only says whether that review is still owed.
/// </summary>
public sealed class VerifyGatesRowTests
{
    private static ProjectDetails Project() => new() { Id = DomainId.New(), Name = "hall9k" };

    [Fact]
    public void No_gates_configured_names_how_to_add_one()
    {
        ProjectShowCommand.VerifyGatesRow(Project())
            .Should().Contain("none — add one").And.Contain("--verify");
    }

    [Fact]
    public void An_accepted_set_lists_the_commands_unmarked()
    {
        ProjectDetails project = Project();
        VerifyCommand gate = new("test", "dotnet test");
        project.VerifyCommands.Add(gate);
        project.AcceptedVerifyCommands = [gate];

        string row = ProjectShowCommand.VerifyGatesRow(project);

        row.Should().Contain("test").And.Contain("dotnet test");
        row.Should().NotContain("not accepted on this node");
    }

    [Fact]
    public void A_set_never_accepted_on_this_node_is_marked_not_hidden()
    {
        ProjectDetails project = Project();
        project.VerifyCommands.Add(new VerifyCommand("test", "dotnet test"));
        project.AcceptedVerifyCommands = null;

        string row = ProjectShowCommand.VerifyGatesRow(project);

        row.Should().Contain("not accepted on this node");
        row.Should().Contain("h9k project accept-gates hall9k");
        row.Should().Contain("test").And.Contain("dotnet test", "marked, not hidden — accept-gates is where the list is vetted");
    }

    [Fact]
    public void A_set_changed_since_this_node_last_accepted_is_marked_too()
    {
        ProjectDetails project = Project();
        project.AcceptedVerifyCommands = [new VerifyCommand("test", "dotnet test")];
        project.VerifyCommands.Add(new VerifyCommand("test", "dotnet test --filter Foo"));

        ProjectShowCommand.VerifyGatesRow(project).Should().Contain("not accepted on this node");
    }
}

using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Cli;

public sealed class ProjectResolverTests
{
    private static ProjectDetails Project(string name) => new() { Id = DomainId.New(), Name = name };

    [Fact]
    public void An_unambiguous_fragment_names_the_project()
    {
        ProjectDetails[] projects = [Project("hall9k"), Project("orbital-docs")];

        ProjectResolver.Match(projects, "orb").Name.Should().Be("orbital-docs");
    }

    [Fact]
    public void An_exact_name_wins_over_the_longer_project_it_is_a_fragment_of()
    {
        // "hall9k" and "hall9k-docs" both contain "hall9k"; naming one exactly is never
        // ambiguous, or a project could be made unreachable by registering its superstring.
        ProjectDetails[] projects = [Project("hall9k"), Project("hall9k-docs")];

        ProjectResolver.Match(projects, "hall9k").Name.Should().Be("hall9k");
        ProjectResolver.Match(projects, "HALL9K").Name.Should().Be("hall9k", "case is not part of the name");
    }

    [Fact]
    public void An_ambiguous_fragment_names_the_candidates_so_the_caller_can_correct_itself()
    {
        ProjectDetails[] projects = [Project("hall9k"), Project("hall9k-docs"), Project("orbital")];

        Action resolve = () => ProjectResolver.Match(projects, "hall");

        resolve.Should().Throw<DomainConflictException>()
            .WithMessage("*2 matches*hall9k*hall9k-docs*use more characters*");
    }

    [Fact]
    public void No_match_lists_what_is_registered_and_how_to_register_this_one()
    {
        ProjectDetails[] projects = [Project("hall9k")];

        Action resolve = () => ProjectResolver.Match(projects, "orbital");

        resolve.Should().Throw<DomainNotFoundException>()
            .WithMessage("*No project matches 'orbital'*Registered: hall9k*h9k project add --name orbital*");
    }

    [Fact]
    public void An_empty_registry_teaches_registration_rather_than_listing_nothing()
    {
        Action resolve = () => ProjectResolver.Match([], "hall9k");

        resolve.Should().Throw<DomainNotFoundException>()
            .WithMessage("*No projects are registered yet*h9k project add*");
    }

    [Fact]
    public void A_long_candidate_list_is_bounded_so_the_message_still_teaches()
    {
        ProjectDetails[] projects = [.. Enumerable.Range(0, 14).Select(index => Project($"project-{index:00}"))];

        Action resolve = () => ProjectResolver.Match(projects, "nothing-like-this");

        resolve.Should().Throw<DomainNotFoundException>().WithMessage("*and 4 more*");
    }
}

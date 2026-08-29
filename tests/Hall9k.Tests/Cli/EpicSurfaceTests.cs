using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// h9k epic list's footer, held to the same bounded-list discipline as
/// <see cref="TaskListCommand.Footer"/> and <see cref="IdeaListCommand.Footer"/>: a filtered view
/// never reads as the whole truth (conformance review, cycle 3).
/// </summary>
public sealed class EpicSurfaceTests
{
    [Fact]
    public void The_footer_names_what_the_default_open_only_filter_is_hiding()
    {
        IReadOnlyList<EpicDetails> scoped = [Epic(EpicState.Open), Epic(EpicState.Closed), Epic(EpicState.Closed)];
        IReadOnlyList<EpicDetails> matched = [scoped[0]];

        string footer = EpicListCommand.Footer(matched, scoped, project: null, state: EpicState.Open);

        footer.Should().Contain("1 epic");
        footer.Should().Contain("2 closed", "the default filter hid two closed epics");
        footer.Should().Contain("h9k epic list --state all");
    }

    [Fact]
    public void The_footer_stays_quiet_when_the_state_filter_is_hiding_nothing()
    {
        IReadOnlyList<EpicDetails> scoped = [Epic(EpicState.Open), Epic(EpicState.Open)];

        string footer = EpicListCommand.Footer(scoped, scoped, project: null, state: EpicState.Open);

        footer.Should().NotContain("--state all", "nothing was hidden, so nothing points at seeing more");
    }

    [Fact]
    public void The_footer_says_every_state_when_the_reader_asked_for_all()
    {
        IReadOnlyList<EpicDetails> scoped = [Epic(EpicState.Open), Epic(EpicState.Closed)];

        string footer = EpicListCommand.Footer(scoped, scoped, project: null, state: null);

        footer.Should().Contain("every state");
        footer.Should().NotContain("--state all", "everything is already shown, there is nothing left to see");
    }

    [Fact]
    public void The_footer_names_the_project_the_reader_scoped_to()
    {
        ProjectDetails project = new() { Id = DomainId.New(), Name = "alpha" };
        IReadOnlyList<EpicDetails> scoped = [Epic(EpicState.Open, project.Id), Epic(EpicState.Closed, project.Id)];
        IReadOnlyList<EpicDetails> matched = [scoped[0]];

        string footer = EpicListCommand.Footer(matched, scoped, project, state: EpicState.Open);

        footer.Should().Contain("in alpha open");
        footer.Should().Contain("h9k epic list --state all --project alpha");
    }

    private static EpicDetails Epic(EpicState state, Guid? projectId = null) => new()
    {
        Id = DomainId.New(),
        ProjectId = projectId ?? DomainId.New(),
        Title = "Some epic",
        State = state,
        AddedAt = DateTimeOffset.UtcNow,
        AddedByOwnerId = DomainId.New(),
    };
}

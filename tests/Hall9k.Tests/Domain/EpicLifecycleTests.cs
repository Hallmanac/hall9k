using FluentAssertions;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// An epic is a first-class named grouping of tasks (Decisions Log #100): its own id,
/// title, and open state, event-sourced like everything else. The rules are deliberately few —
/// a name and a project to add one, and closing is always an explicit human act with a reason,
/// never automatic.
/// </summary>
public sealed class EpicLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = DomainId.New();

    [Fact]
    public void Add_creates_an_open_epic_from_a_project_and_a_title_alone()
    {
        EpicAdded added = EpicDecider.Add(DomainId.New(), DomainId.New(), "Interactive mode", Now, Owner);

        EpicAggregate epic = new();
        epic.Apply(added);

        epic.State.Should().Be(EpicState.Open);
        epic.Title.Should().Be("Interactive mode");
    }

    [Fact]
    public void Add_without_a_title_is_refused()
    {
        Action act = () => EpicDecider.Add(DomainId.New(), DomainId.New(), " ", Now, Owner);

        act.Should().Throw<DomainValidationException>().WithMessage("*title*");
    }

    [Fact]
    public void Add_without_a_project_is_refused()
    {
        Action act = () => EpicDecider.Add(DomainId.New(), Guid.Empty, "Interactive mode", Now, Owner);

        act.Should().Throw<DomainValidationException>().WithMessage("*project*");
    }

    [Fact]
    public void Close_needs_a_reason_and_ends_the_epic()
    {
        EpicAggregate epic = Open();

        Action noReason = () => EpicDecider.Close(epic, " ", Now, Owner);
        noReason.Should().Throw<DomainValidationException>().WithMessage("*h9k epic close*");

        epic.Apply(EpicDecider.Close(epic, "Interactive mode shipped", Now, Owner));

        epic.State.Should().Be(EpicState.Closed);
        epic.CloseReason.Should().Be("Interactive mode shipped");
    }

    /// <summary>
    /// The standing never-auto-close doctrine: an epic that is already closed refuses a second
    /// close rather than accepting a redundant one, exactly as a task's terminal states refuse
    /// a second ending. Nothing about this epic's own tasks factors into the decision at all —
    /// there is no code path here that even reads member state.
    /// </summary>
    [Fact]
    public void An_already_closed_epic_refuses_a_second_close()
    {
        EpicAggregate epic = Open();
        epic.Apply(EpicDecider.Close(epic, "Done", Now, Owner));

        Action act = () => EpicDecider.Close(epic, "Done again", Now, Owner);

        act.Should().Throw<DomainConflictException>().WithMessage("*Closed*");
    }

    [Fact]
    public void LinkJira_records_the_reference_exactly_as_given_key_or_url()
    {
        EpicAggregate epic = Open();

        epic.Apply(EpicDecider.LinkJira(epic, "PROJ-45", Now, Owner));
        epic.JiraReference.Should().Be("PROJ-45");

        EpicAggregate epicWithUrl = Open();
        epicWithUrl.Apply(EpicDecider.LinkJira(
            epicWithUrl, "https://your-org.atlassian.net/browse/PROJ-45", Now, Owner));
        epicWithUrl.JiraReference.Should().Be("https://your-org.atlassian.net/browse/PROJ-45");
    }

    [Fact]
    public void LinkJira_repeating_the_same_reference_is_quiet_but_a_different_one_conflicts()
    {
        EpicAggregate epic = Open();
        epic.Apply(EpicDecider.LinkJira(epic, "PROJ-45", Now, Owner));

        EpicDecider.AlreadyLinkedTo(epic, "PROJ-45").Should().BeTrue();
        EpicDecider.AlreadyLinkedTo(epic, "PROJ-99").Should().BeFalse();

        Action act = () => EpicDecider.LinkJira(epic, "PROJ-99", Now, Owner);
        act.Should().Throw<DomainConflictException>().WithMessage("*already linked*");
    }

    [Fact]
    public void LinkJira_on_a_closed_epic_is_refused()
    {
        EpicAggregate epic = Open();
        epic.Apply(EpicDecider.Close(epic, "Done", Now, Owner));

        Action act = () => EpicDecider.LinkJira(epic, "PROJ-45", Now, Owner);

        act.Should().Throw<DomainConflictException>().WithMessage("*closed*");
    }

    private static EpicAggregate Open()
    {
        EpicAggregate epic = new();
        epic.Apply(EpicDecider.Add(DomainId.New(), DomainId.New(), "Interactive mode", Now, Owner));
        return epic;
    }
}

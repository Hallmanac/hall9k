using FluentAssertions;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The one read model h9k epic list and h9k epic show both read, built without a database
/// (Decisions Log #100).
/// </summary>
public sealed class EpicDetailsProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = DomainId.New();

    [Fact]
    public void Create_builds_an_open_epic_from_its_name_and_project()
    {
        EpicDetailsProjection projection = new();
        Guid id = DomainId.New();
        Guid projectId = DomainId.New();

        EpicDetails view = projection.Create(new FakeEvent<EpicAdded>(
            new EpicAdded(id, projectId, "Interactive mode", Now, Owner)));

        view.Id.Should().Be(id);
        view.ProjectId.Should().Be(projectId);
        view.Title.Should().Be("Interactive mode");
        view.State.Should().Be(EpicState.Open);
        view.AddedAt.Should().Be(Now);
    }

    [Fact]
    public void LinkedToJira_records_the_reference_exactly_as_given()
    {
        EpicDetailsProjection projection = new();
        Guid id = DomainId.New();

        EpicDetails view = projection.Create(new FakeEvent<EpicAdded>(
            new EpicAdded(id, DomainId.New(), "Interactive mode", Now, Owner)));
        projection.Apply(new FakeEvent<EpicLinkedToJira>(
            new EpicLinkedToJira(id, "PROJ-45", Now.AddDays(1), Owner)), view);

        view.JiraReference.Should().Be("PROJ-45");
    }

    [Fact]
    public void Closed_ends_the_epic_and_carries_the_reason_and_close_time()
    {
        EpicDetailsProjection projection = new();
        Guid id = DomainId.New();

        EpicDetails view = projection.Create(new FakeEvent<EpicAdded>(
            new EpicAdded(id, DomainId.New(), "Interactive mode", Now, Owner)));
        projection.Apply(new FakeEvent<EpicClosed>(
            new EpicClosed(id, "Interactive mode shipped", Now.AddDays(2), Owner)), view);

        view.State.Should().Be(EpicState.Closed);
        view.CloseReason.Should().Be("Interactive mode shipped");
        view.ClosedAt.Should().Be(Now.AddDays(2));
    }
}

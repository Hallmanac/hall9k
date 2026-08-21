using FluentAssertions;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The one read model both idea surfaces use, built without a database. What it has to carry
/// beyond the current note is the discovery trail: every version the note has had, and what
/// the idea became.
/// </summary>
public sealed class IdeaDetailsProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Capture_then_revisions_build_the_note_and_its_history()
    {
        IdeaDetailsProjection projection = new();
        Guid id = DomainId.New();
        Guid ownerId = DomainId.New();

        IdeaDetails view = projection.Create(new FakeEvent<IdeaCaptured>(
            new IdeaCaptured(id, ownerId, "A rough thought", ProjectId: null, Now)));
        projection.Apply(new FakeEvent<IdeaRevised>(
            new IdeaRevised(id, "A sharper thought", Now.AddHours(3), ownerId)), view);
        projection.Apply(new FakeEvent<IdeaRevised>(
            new IdeaRevised(id, "The thought, finally", Now.AddDays(2), ownerId)), view);

        view.Text.Should().Be("The thought, finally");
        view.Revisions.Should().Be(2);
        view.History.Select(note => note.Text).Should().Equal(
            "A rough thought", "A sharper thought", "The thought, finally");
        view.History[0].WrittenAt.Should().Be(Now, "the oldest entry is the capture itself");
        view.State.Should().Be(IdeaState.Captured);
    }

    [Fact]
    public void An_assignment_binds_the_project_capture_did_not_know()
    {
        IdeaDetailsProjection projection = new();
        Guid id = DomainId.New();
        Guid projectId = DomainId.New();

        IdeaDetails view = projection.Create(new FakeEvent<IdeaCaptured>(
            new IdeaCaptured(id, DomainId.New(), "note", ProjectId: null, Now)));
        view.ProjectId.Should().BeNull();

        projection.Apply(new FakeEvent<IdeaAssignedToProject>(
            new IdeaAssignedToProject(id, projectId, PreviousProjectId: null, Now.AddDays(1), DomainId.New())), view);

        view.ProjectId.Should().Be(projectId);
    }

    [Fact]
    public void Promotion_records_what_the_idea_became()
    {
        IdeaDetailsProjection projection = new();
        Guid id = DomainId.New();
        Guid taskId = DomainId.New();
        Guid projectId = DomainId.New();

        IdeaDetails view = projection.Create(new FakeEvent<IdeaCaptured>(
            new IdeaCaptured(id, DomainId.New(), "Give ideas a workspace", ProjectId: null, Now)));
        projection.Apply(new FakeEvent<IdeaPromoted>(
            new IdeaPromoted(id, taskId, projectId, "Give ideas a workspace", Now.AddDays(1), DomainId.New())), view);

        view.State.Should().Be(IdeaState.Promoted);
        view.PromotedTaskId.Should().Be(taskId);
        view.ProjectId.Should().Be(projectId, "promotion is also where a project finally gets settled");
        view.PromotedAt.Should().Be(Now.AddDays(1));
    }

    [Fact]
    public void A_discard_keeps_the_note_and_carries_the_reason()
    {
        IdeaDetailsProjection projection = new();
        Guid id = DomainId.New();

        IdeaDetails view = projection.Create(new FakeEvent<IdeaCaptured>(
            new IdeaCaptured(id, DomainId.New(), "A thought that did not survive", ProjectId: null, Now)));
        projection.Apply(new FakeEvent<IdeaDiscarded>(
            new IdeaDiscarded(id, "Superseded by attachments", Now.AddDays(4), DomainId.New())), view);

        view.State.Should().Be(IdeaState.Discarded);
        view.DiscardReason.Should().Be("Superseded by attachments");
        view.DiscardedAt.Should().Be(Now.AddDays(4));
        view.Text.Should().Be("A thought that did not survive", "discarding is recorded, never deleted");
    }
}

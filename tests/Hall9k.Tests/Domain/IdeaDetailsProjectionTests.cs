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
    public void Cutting_a_task_fans_out_the_idea_without_ending_it()
    {
        IdeaDetailsProjection projection = new();
        Guid id = DomainId.New();
        Guid firstTask = DomainId.New();
        Guid secondTask = DomainId.New();

        IdeaDetails view = projection.Create(new FakeEvent<IdeaCaptured>(
            new IdeaCaptured(id, DomainId.New(), "Give ideas a workspace", ProjectId: null, Now)));
        projection.Apply(new FakeEvent<IdeaTaskCut>(
            new IdeaTaskCut(id, firstTask, "Give ideas a workspace directory", Now.AddDays(1), DomainId.New())), view);
        projection.Apply(new FakeEvent<IdeaTaskCut>(
            new IdeaTaskCut(id, secondTask, "Render the path on idea show", Now.AddDays(2), DomainId.New())), view);

        view.State.Should().Be(IdeaState.Captured, "cutting a task never ends the idea on its own");
        view.CutTaskIds.Should().Equal(firstTask, secondTask);
    }

    [Fact]
    public void Concluding_records_why_something_came_of_it()
    {
        IdeaDetailsProjection projection = new();
        Guid id = DomainId.New();

        IdeaDetails view = projection.Create(new FakeEvent<IdeaCaptured>(
            new IdeaCaptured(id, DomainId.New(), "Give ideas a workspace", ProjectId: null, Now)));
        projection.Apply(new FakeEvent<IdeaConcluded>(
            new IdeaConcluded(id, "Cut two tasks; discovery is done here", Now.AddDays(1), DomainId.New())), view);

        view.State.Should().Be(IdeaState.Concluded);
        view.ConcludeReason.Should().Be("Cut two tasks; discovery is done here");
        view.ConcludedAt.Should().Be(Now.AddDays(1));
    }

    [Fact]
    public void An_archive_keeps_the_note_and_carries_the_reason()
    {
        IdeaDetailsProjection projection = new();
        Guid id = DomainId.New();

        IdeaDetails view = projection.Create(new FakeEvent<IdeaCaptured>(
            new IdeaCaptured(id, DomainId.New(), "A thought that did not survive", ProjectId: null, Now)));
        projection.Apply(new FakeEvent<IdeaArchived>(
            new IdeaArchived(id, "Superseded by attachments", Now.AddDays(4), DomainId.New())), view);

        view.State.Should().Be(IdeaState.Archived);
        view.ArchiveReason.Should().Be("Superseded by attachments");
        view.ArchivedAt.Should().Be(Now.AddDays(4));
        view.Text.Should().Be("A thought that did not survive", "archiving is recorded, never deleted");
    }

    /// <summary>
    /// A document a legacy IdeaPromoted/IdeaDiscarded last wrote reads under the vocabulary it
    /// was reconciled into (backlog 31), never a state that no longer exists.
    /// </summary>
    [Fact]
    public void A_legacy_promotion_or_discard_replays_into_the_reconciled_states()
    {
        IdeaDetailsProjection projection = new();
        Guid promotedId = DomainId.New();
        Guid discardedId = DomainId.New();
        Guid taskId = DomainId.New();

        IdeaDetails promoted = projection.Create(new FakeEvent<IdeaCaptured>(
            new IdeaCaptured(promotedId, DomainId.New(), "Give ideas a workspace", ProjectId: null, Now)));
        projection.Apply(new FakeEvent<IdeaPromoted>(
            new IdeaPromoted(promotedId, taskId, DomainId.New(), "Give ideas a workspace", Now.AddDays(1), DomainId.New())), promoted);

        promoted.State.Should().Be(IdeaState.Concluded, "a promotion always meant something came of the idea");
        promoted.CutTaskIds.Should().Equal(taskId);

        IdeaDetails discarded = projection.Create(new FakeEvent<IdeaCaptured>(
            new IdeaCaptured(discardedId, DomainId.New(), "A thought that did not survive", ProjectId: null, Now)));
        projection.Apply(new FakeEvent<IdeaDiscarded>(
            new IdeaDiscarded(discardedId, "Superseded by attachments", Now.AddDays(4), DomainId.New())), discarded);

        discarded.State.Should().Be(IdeaState.Archived, "a discard always meant nothing came of the idea");
        discarded.ArchiveReason.Should().Be("Superseded by attachments");
    }
}

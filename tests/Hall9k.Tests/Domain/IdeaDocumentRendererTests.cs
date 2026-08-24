using FluentAssertions;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Idea.Rendering;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>idea.md (backlog 48): the same one-way-render shape as a task, one stage earlier.</summary>
public sealed class IdeaDocumentRendererTests
{
    [Fact]
    public void Render_carries_the_note_verbatim_in_the_body()
    {
        IdeaDetails idea = SomeIdea();
        idea.Text = "The attention pane should teach the next command.";

        string rendered = IdeaDocumentRenderer.Render(idea, "hall9k");

        rendered.Should().Contain(idea.Text);
        rendered.Should().Contain(IdeaDocumentRenderer.GeneratedMarker);
        rendered.Should().Contain($"h9k idea revise {DomainId.Short(idea.Id)}");
        rendered.Should().NotContain("task revise",
            "idea revise has no --file form; the header must not send anyone looking for the task command instead");
    }

    [Fact]
    public void Directory_name_is_short_id_plus_a_slug_of_the_note()
    {
        IdeaDetails idea = SomeIdea();
        idea.Text = "Project directory and tracker mirroring";

        string name = IdeaDocumentRenderer.DirectoryName(idea);

        name.Should().Be($"{DomainId.Short(idea.Id)}-project-directory-and-tracker-mirroring");
    }

    [Fact]
    public void A_promoted_idea_names_the_task_it_became()
    {
        IdeaDetails idea = SomeIdea();
        idea.PromotedTaskId = DomainId.New();
        idea.State = IdeaState.Promoted;

        string rendered = IdeaDocumentRenderer.Render(idea, "hall9k");

        rendered.Should().Contain($"promoted-task: {DomainId.Short(idea.PromotedTaskId.Value)}");
    }

    private static IdeaDetails SomeIdea() => new()
    {
        Id = DomainId.New(),
        OwnerId = DomainId.New(),
        Text = "An idea worth capturing",
        State = IdeaState.Captured,
        CapturedAt = DateTimeOffset.UtcNow,
    };
}

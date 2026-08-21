using FluentAssertions;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Discovery as its own phase (Decisions Log #35): capture asks for nothing but the thought,
/// the note is revisable for as long as the idea is being figured out, and the two endings —
/// promoted into a draft, or discarded with a reason — both refuse to pretend anything else
/// is still happening.
/// </summary>
public sealed class IdeaLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = DomainId.New();

    [Fact]
    public void Capture_asks_for_the_thought_and_nothing_else()
    {
        IdeaCaptured captured = IdeaDecider.Capture(
            DomainId.New(), Owner, "Ideas should have a discovery workspace", projectId: null, Now);

        IdeaAggregate idea = new();
        idea.Apply(captured);

        idea.State.Should().Be(IdeaState.Captured);
        idea.ProjectId.Should().BeNull("an idea may precede its project, or become one");
        idea.Text.Should().Be("Ideas should have a discovery workspace");
    }

    [Fact]
    public void Capture_refuses_an_empty_thought_and_says_what_capture_costs()
    {
        Action act = () => IdeaDecider.Capture(DomainId.New(), Owner, "   ", projectId: null, Now);

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*h9k idea add*")
            .WithMessage("*project is optional*", "the message must not imply more is required");
    }

    [Fact]
    public void A_project_given_at_capture_is_kept_and_an_empty_one_is_read_as_none()
    {
        Guid projectId = DomainId.New();

        IdeaDecider.Capture(DomainId.New(), Owner, "note", projectId, Now).ProjectId.Should().Be(projectId);
        IdeaDecider.Capture(DomainId.New(), Owner, "note", Guid.Empty, Now).ProjectId.Should().BeNull();
    }

    [Fact]
    public void Revising_keeps_every_version_on_the_stream()
    {
        IdeaAggregate idea = Captured("A rough thought");

        idea.Apply(IdeaDecider.Revise(idea, "A sharper thought", Now.AddHours(2), Owner));

        idea.Text.Should().Be("A sharper thought");
        idea.Revisions.Should().Be(1);
        idea.State.Should().Be(IdeaState.Captured, "revising is what discovery does; it is not an ending");
    }

    [Fact]
    public void Revising_to_the_same_words_records_nothing()
    {
        IdeaAggregate idea = Captured("A rough thought");

        Action act = () => IdeaDecider.Revise(idea, "  A rough thought  ", Now, Owner);

        act.Should().Throw<DomainValidationException>().WithMessage("*already reads exactly that*");
    }

    [Fact]
    public void A_project_can_be_set_after_capture_and_changed_after_that()
    {
        IdeaAggregate idea = Captured("Stacked PRs for dependency chains");
        Guid first = DomainId.New();
        Guid second = DomainId.New();

        IdeaAssignedToProject assigned = IdeaDecider.AssignToProject(idea, first, Now, Owner);
        assigned.PreviousProjectId.Should().BeNull("capture did not know one");
        idea.Apply(assigned);

        IdeaAssignedToProject moved = IdeaDecider.AssignToProject(idea, second, Now.AddDays(1), Owner);
        moved.PreviousProjectId.Should().Be(first, "where it used to belong is observed history");
        idea.Apply(moved);

        idea.ProjectId.Should().Be(second);
    }

    [Fact]
    public void Assigning_an_idea_to_the_project_it_is_already_in_changes_nothing()
    {
        Guid projectId = DomainId.New();
        IdeaAggregate idea = Captured("note", projectId);

        Action act = () => IdeaDecider.AssignToProject(idea, projectId, Now, Owner);

        act.Should().Throw<DomainConflictException>().WithMessage("*already assigned*");
    }

    [Fact]
    public void Promotion_needs_a_project_and_teaches_the_one_path_to_a_new_one()
    {
        IdeaAggregate idea = Captured("Give ideas a discovery workspace");

        Action act = () => IdeaDecider.Promote(idea, DomainId.New(), projectId: null, "objective", Now, Owner);

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*--project*")
            .WithMessage("*h9k project add*", "an idea that IS a new project needs the registration first")
            .WithMessage("*will not invent a repository*");
    }

    [Fact]
    public void Promotion_uses_the_project_the_idea_was_already_assigned_to()
    {
        Guid projectId = DomainId.New();
        IdeaAggregate idea = Captured("Give ideas a discovery workspace", projectId);
        Guid taskId = DomainId.New();

        IdeaPromoted promoted = IdeaDecider.Promote(idea, taskId, projectId: null, "Give ideas a workspace", Now, Owner);
        idea.Apply(promoted);

        promoted.ProjectId.Should().Be(projectId);
        promoted.TaskId.Should().Be(taskId, "the idea's stream names the task it became");
        idea.State.Should().Be(IdeaState.Promoted);
        idea.PromotedTaskId.Should().Be(taskId);
    }

    [Fact]
    public void An_idea_promotes_once_and_the_refusal_names_the_task_it_became()
    {
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        IdeaAggregate idea = Captured("note", projectId);
        idea.Apply(IdeaDecider.Promote(idea, taskId, projectId, "objective", Now, Owner));

        Action act = () => IdeaDecider.Promote(idea, DomainId.New(), projectId, "objective", Now.AddDays(1), Owner);

        act.Should().Throw<DomainConflictException>()
            .WithMessage($"*{taskId}*")
            .WithMessage("*h9k task show*");
    }

    [Fact]
    public void A_promoted_idea_is_not_revised_or_discarded_the_draft_is_where_the_work_moved()
    {
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        IdeaAggregate idea = Captured("note", projectId);
        idea.Apply(IdeaDecider.Promote(idea, taskId, projectId, "objective", Now, Owner));

        Action revise = () => IdeaDecider.Revise(idea, "second thoughts", Now.AddDays(1), Owner);
        Action discard = () => IdeaDecider.Discard(idea, "changed my mind", Now.AddDays(1), Owner);

        revise.Should().Throw<DomainConflictException>().WithMessage("*h9k task show*");
        discard.Should().Throw<DomainConflictException>().WithMessage("*h9k task abandon*");
    }

    [Fact]
    public void Discarding_records_the_reason_and_keeps_the_idea()
    {
        IdeaAggregate idea = Captured("A thought that did not survive contact");

        IdeaDiscarded discarded = IdeaDecider.Discard(idea, "Superseded by the attachments design", Now, Owner);
        idea.Apply(discarded);

        idea.State.Should().Be(IdeaState.Discarded);
        idea.DiscardReason.Should().Be("Superseded by the attachments design");
        idea.Text.Should().Be("A thought that did not survive contact", "nothing is deleted");
    }

    [Fact]
    public void Discarding_without_a_reason_is_refused_because_the_reason_is_the_signal()
    {
        IdeaAggregate idea = Captured("note");

        Action act = () => IdeaDecider.Discard(idea, "  ", Now, Owner);

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*--reason*")
            .WithMessage("*keeps coming back*");
    }

    [Fact]
    public void A_discarded_idea_stays_discarded_and_the_refusal_quotes_why()
    {
        IdeaAggregate idea = Captured("note");
        idea.Apply(IdeaDecider.Discard(idea, "Not worth the complexity", Now, Owner));

        Action act = () => IdeaDecider.Revise(idea, "unless…", Now.AddDays(30), Owner);

        act.Should().Throw<DomainConflictException>()
            .WithMessage("*Not worth the complexity*")
            .WithMessage("*h9k idea add*", "a returning thought is a fresh idea, not a resurrection");
    }

    private static IdeaAggregate Captured(string text, Guid? projectId = null)
    {
        IdeaAggregate idea = new();
        idea.Apply(IdeaDecider.Capture(DomainId.New(), Owner, text, projectId, Now));
        return idea;
    }
}

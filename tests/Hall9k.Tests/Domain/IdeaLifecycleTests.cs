using FluentAssertions;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Discovery as its own phase (backlog 31): capture asks for nothing but the thought, the note
/// is revisable for as long as the idea is being figured out, cutting a task is repeatable and
/// never ends the idea on its own, and the two endings — concluded, or archived — both refuse to
/// pretend anything else is still happening.
/// </summary>
public sealed class IdeaLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = DomainId.New();

    [Fact]
    public void Capture_asks_for_the_thought_and_nothing_else()
    {
        IdeaCaptured captured = IdeaDecider.Capture(
            DomainId.New(), Owner, "Ideas should have a discovery workspace", projectId: null, Now, ProjectHome.None);

        IdeaAggregate idea = new();
        idea.Apply(captured);

        idea.State.Should().Be(IdeaState.Captured);
        idea.ProjectId.Should().BeNull("an idea may precede its project, or become one");
        idea.Text.Should().Be("Ideas should have a discovery workspace");
    }

    [Fact]
    public void Capture_refuses_an_empty_thought_and_says_what_capture_costs()
    {
        Action act = () => IdeaDecider.Capture(DomainId.New(), Owner, "   ", projectId: null, Now, ProjectHome.None);

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*h9k idea add*")
            .WithMessage("*project is optional*", "the message must not imply more is required");
    }

    [Fact]
    public void A_project_given_at_capture_is_kept_and_an_empty_one_is_read_as_none()
    {
        Guid projectId = DomainId.New();

        IdeaDecider.Capture(DomainId.New(), Owner, "note", projectId, Now, ProjectHome.None).ProjectId.Should().Be(projectId);
        IdeaDecider.Capture(DomainId.New(), Owner, "note", Guid.Empty, Now, ProjectHome.None).ProjectId.Should().BeNull();
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
    public void Cutting_a_task_is_repeatable_and_never_ends_the_idea()
    {
        IdeaAggregate idea = Captured("Give ideas a discovery workspace");
        Guid firstTask = DomainId.New();
        Guid secondTask = DomainId.New();

        IdeaTaskCut first = IdeaDecider.CutTask(idea, firstTask, "Give ideas a workspace directory", Now, Owner);
        idea.Apply(first);
        IdeaTaskCut second = IdeaDecider.CutTask(idea, secondTask, "Render the workspace path on idea show", Now.AddDays(1), Owner);
        idea.Apply(second);

        idea.State.Should().Be(IdeaState.Captured, "cutting a task never ends the idea on its own");
        idea.CutTaskIds.Should().Equal(firstTask, secondTask);
        first.Objective.Should().Be("Give ideas a workspace directory");
    }

    [Fact]
    public void Cutting_a_task_needs_its_own_objective()
    {
        IdeaAggregate idea = Captured("Give ideas a discovery workspace");

        Action act = () => IdeaDecider.CutTask(idea, DomainId.New(), "  ", Now, Owner);

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*own objective*")
            .WithMessage("*--from-idea*");
    }

    [Fact]
    public void Concluding_records_why_and_ends_the_idea()
    {
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        IdeaAggregate idea = Captured("Give ideas a discovery workspace", projectId);
        idea.Apply(IdeaDecider.CutTask(idea, taskId, "Give ideas a workspace directory", Now, Owner));

        IdeaConcluded concluded = IdeaDecider.Conclude(idea, "Cut one task; discovery is done here", Now.AddDays(1), Owner);
        idea.Apply(concluded);

        idea.State.Should().Be(IdeaState.Concluded);
        idea.ConcludeReason.Should().Be("Cut one task; discovery is done here");
        idea.CutTaskIds.Should().Equal(taskId);
    }

    [Fact]
    public void Concluding_without_a_reason_is_refused()
    {
        IdeaAggregate idea = Captured("note");

        Action act = () => IdeaDecider.Conclude(idea, "  ", Now, Owner);

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*--reason*")
            .WithMessage("*h9k idea conclude*");
    }

    [Fact]
    public void A_concluded_idea_refuses_further_cuts_and_the_refusal_points_at_idea_show()
    {
        Guid projectId = DomainId.New();
        IdeaAggregate idea = Captured("note", projectId);
        idea.Apply(IdeaDecider.Conclude(idea, "Nothing more coming", Now, Owner));

        Action cut = () => IdeaDecider.CutTask(idea, DomainId.New(), "objective", Now.AddDays(1), Owner);
        Action revise = () => IdeaDecider.Revise(idea, "second thoughts", Now.AddDays(1), Owner);
        Action archive = () => IdeaDecider.Archive(idea, "changed my mind", Now.AddDays(1), Owner);

        cut.Should().Throw<DomainConflictException>().WithMessage("*h9k idea show*");
        revise.Should().Throw<DomainConflictException>().WithMessage("*h9k idea show*");
        archive.Should().Throw<DomainConflictException>().WithMessage("*h9k idea show*");
    }

    [Fact]
    public void Archiving_records_the_reason_and_keeps_the_idea()
    {
        IdeaAggregate idea = Captured("A thought that did not survive contact");

        IdeaArchived archived = IdeaDecider.Archive(idea, "Superseded by the attachments design", Now, Owner);
        idea.Apply(archived);

        idea.State.Should().Be(IdeaState.Archived);
        idea.ArchiveReason.Should().Be("Superseded by the attachments design");
        idea.Text.Should().Be("A thought that did not survive contact", "nothing is deleted");
    }

    [Fact]
    public void Archiving_without_a_reason_is_refused_because_the_reason_is_the_signal()
    {
        IdeaAggregate idea = Captured("note");

        Action act = () => IdeaDecider.Archive(idea, "  ", Now, Owner);

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*--reason*")
            .WithMessage("*keeps coming back*");
    }

    [Fact]
    public void An_archived_idea_stays_archived_and_the_refusal_quotes_why()
    {
        IdeaAggregate idea = Captured("note");
        idea.Apply(IdeaDecider.Archive(idea, "Not worth the complexity", Now, Owner));

        Action act = () => IdeaDecider.Revise(idea, "unless…", Now.AddDays(30), Owner);

        act.Should().Throw<DomainConflictException>()
            .WithMessage("*Not worth the complexity*")
            .WithMessage("*h9k idea add*", "a returning thought is a fresh idea, not a resurrection");
    }

    /// <summary>
    /// A legacy promotion replays into exactly the fan-out shape a fresh cut-then-conclude
    /// would have left: the task joins <see cref="IdeaAggregate.CutTaskIds"/>, and the idea
    /// reaches <see cref="IdeaState.Concluded"/> with no reason recorded (the original promote
    /// never asked for one). This is the mechanism <c>h9k idea show</c> depends on to display a
    /// pre-existing promoted idea's fan-out correctly: it reads the aggregate fresh from events
    /// rather than trusting <c>IdeaDetails.CutTaskIds</c>, which an already-materialized
    /// document from before this field existed would never carry.
    /// </summary>
    [Fact]
    public void A_legacy_promotion_replays_as_a_fan_out_of_one_and_a_conclusion()
    {
        IdeaAggregate idea = Captured("Give ideas a discovery workspace", DomainId.New());
        Guid taskId = DomainId.New();
        Guid projectId = DomainId.New();

        idea.Apply(new IdeaPromoted(idea.Id, taskId, projectId, "Give ideas a workspace", Now.AddDays(1), Owner));

        idea.State.Should().Be(IdeaState.Concluded, "a promotion always meant something came of the idea");
        idea.CutTaskIds.Should().Equal(taskId);
        idea.ConcludeReason.Should().BeNull("the original promote never asked for a reason");
        idea.ConcludedAt.Should().Be(Now.AddDays(1));
    }

    /// <summary>The discard counterpart of the test above.</summary>
    [Fact]
    public void A_legacy_discard_replays_as_an_archive()
    {
        IdeaAggregate idea = Captured("A thought that did not survive contact");

        idea.Apply(new IdeaDiscarded(idea.Id, "Superseded by attachments", Now.AddDays(4), Owner));

        idea.State.Should().Be(IdeaState.Archived, "a discard always meant nothing came of the idea");
        idea.ArchiveReason.Should().Be("Superseded by attachments");
        idea.ArchivedAt.Should().Be(Now.AddDays(4));
    }

    /// <summary>
    /// Whether the workspace started life under a project's home is a fact captured once,
    /// never re-derived from current state (backlog 49): a home gained after capture must not
    /// silently redirect the read path away from a workspace a human may already have used.
    /// </summary>
    [Fact]
    public void Capture_records_whether_the_workspace_started_under_a_home()
    {
        Guid ideaId = DomainId.New();
        ProjectHome home = ProjectHome.Parse(Path.Combine(Path.GetTempPath(), "hall9k-idea-home"));

        IdeaCaptured withHome = IdeaDecider.Capture(ideaId, Owner, "note", projectId: null, Now, home);
        IdeaAggregate captured = new();
        captured.Apply(withHome);
        captured.WorkspaceHome.Should().Be(home);

        IdeaCaptured withoutHome = IdeaDecider.Capture(DomainId.New(), Owner, "note", projectId: null, Now, ProjectHome.None);
        IdeaAggregate captureless = new();
        captureless.Apply(withoutHome);
        captureless.WorkspaceHome.Should().Be(ProjectHome.None);
    }

    private static IdeaAggregate Captured(string text, Guid? projectId = null)
    {
        IdeaAggregate idea = new();
        idea.Apply(IdeaDecider.Capture(DomainId.New(), Owner, text, projectId, Now, ProjectHome.None));
        return idea;
    }
}

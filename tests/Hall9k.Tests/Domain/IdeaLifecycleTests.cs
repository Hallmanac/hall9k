using FluentAssertions;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
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

    /// <summary>
    /// A replicated <see cref="IdeaCaptured"/> carries whatever the CAPTURING node's own host
    /// recorded — a Windows path applied on macOS, or the reverse — since <c>WorkspaceHomeDirectory</c>
    /// is a node-local fact, not something this receiver ever resolved to a directory of its own.
    /// <see cref="ProjectHome.Parse"/> must accept the foreign shape rather than refuse it as "not
    /// absolute", which used to abort every later event from that sender.
    /// </summary>
    [Fact]
    public void A_capture_carrying_the_other_operating_systems_path_form_never_throws_and_round_trips_verbatim()
    {
        string foreignWorkspaceHome = OperatingSystem.IsWindows()
            ? "/Users/bob/.hall9k/projects/hall9k"
            : @"C:\Users\bob\.hall9k\projects\hall9k";
        IdeaCaptured captured = new(
            DomainId.New(), Owner, "A replicated thought", ProjectId: null, Now, foreignWorkspaceHome);

        IdeaAggregate idea = new();
        idea.Apply(captured);

        idea.WorkspaceHome.Value.Should().Be(foreignWorkspaceHome);
        idea.WorkspaceHome.IsNativeForm.Should().BeFalse();
        idea.State.Should().Be(IdeaState.Captured, "the replay itself must complete, not just the workspace field");
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

    // idea 8c5993c5: replication scope — defaults, share, the pre-8c5993c5 set-private alias, and
    // the one-way rule once an idea has been shared with the team.

    [Fact]
    public void A_freshly_captured_idea_starts_at_fleet_scope()
    {
        IdeaAggregate idea = Captured("A fresh thought");

        idea.Scope.Should().Be(ReplicationScope.Fleet, "the owner should be able to work an idea alone or within their fleet");
        idea.IsPrivate.Should().BeFalse();
    }

    [Fact]
    public void Share_moves_a_captured_idea_to_team_scope()
    {
        IdeaAggregate idea = Captured("An idea worth the team's eyes");

        idea.Apply(IdeaDecider.Share(idea, Now, Owner)!);

        idea.Scope.Should().Be(ReplicationScope.Team);
    }

    [Fact]
    public void Share_works_on_an_idea_regardless_of_its_lifecycle_state()
    {
        IdeaAggregate idea = Captured("Discovery finished here");
        idea.Apply(IdeaDecider.Archive(idea, "Superseded", Now, Owner));

        idea.Apply(IdeaDecider.Share(idea, Now.AddMinutes(1), Owner)!);

        idea.Scope.Should().Be(ReplicationScope.Team);
    }

    [Fact]
    public void Set_private_on_is_sugar_for_private_scope_and_off_is_sugar_for_fleet_scope()
    {
        IdeaAggregate idea = Captured("A note kept close for now");

        idea.Apply(IdeaDecider.SetPrivate(idea, isPrivate: true, Now, Owner));
        idea.Scope.Should().Be(ReplicationScope.Private);
        idea.IsPrivate.Should().BeTrue();

        idea.Apply(IdeaDecider.SetPrivate(idea, isPrivate: false, Now.AddMinutes(1), Owner));
        idea.Scope.Should().Be(ReplicationScope.Fleet, "off never jumps straight to team on its own");
    }

    [Fact]
    public void Team_scope_is_one_way_and_refuses_to_narrow_back_to_fleet_or_private()
    {
        IdeaAggregate idea = Captured("Shared with the team already");
        idea.Apply(IdeaDecider.Share(idea, Now, Owner)!);

        Action toFleet = () => IdeaDecider.SetScope(idea, ReplicationScope.Fleet, Now.AddMinutes(1), Owner);
        Action toPrivate = () => IdeaDecider.SetPrivate(idea, isPrivate: true, Now.AddMinutes(1), Owner);

        toFleet.Should().Throw<DomainConflictException>().WithMessage("*one-way*");
        toPrivate.Should().Throw<DomainConflictException>().WithMessage("*one-way*");
    }

    [Fact]
    public void Sharing_an_idea_already_at_team_scope_is_an_idempotent_no_op()
    {
        IdeaAggregate idea = Captured("Shared with the team already");
        idea.Apply(IdeaDecider.Share(idea, Now, Owner)!);

        IdeaScopeSet? shareAgain = IdeaDecider.Share(idea, Now.AddMinutes(1), Owner);

        shareAgain.Should().BeNull("it is already at team scope, so sharing again is a no-op success rather than a refusal");
        idea.Scope.Should().Be(ReplicationScope.Team);
    }

    [Fact]
    public void Setting_the_same_scope_again_is_refused_as_nothing_to_change()
    {
        IdeaAggregate idea = Captured("Already fleet");

        Action act = () => IdeaDecider.SetScope(idea, ReplicationScope.Fleet, Now, Owner);

        act.Should().Throw<DomainConflictException>().WithMessage("*already*");
    }

    [Fact]
    public void An_idea_captured_before_scope_existed_reads_team_when_not_private_and_private_when_it_was()
    {
        // idea 8c5993c5: existing items keep their current effective scope — a legacy capture, with
        // no InitialScope recorded, was already fully team-visible under the old private flag alone.
        IdeaAggregate neverPrivate = new();
        neverPrivate.Apply(new IdeaCaptured(DomainId.New(), Owner, "legacy idea", null, Now));
        neverPrivate.Scope.Should().Be(ReplicationScope.Team);

        IdeaAggregate onceMadePrivate = new();
        onceMadePrivate.Apply(new IdeaCaptured(DomainId.New(), Owner, "legacy private idea", null, Now));
        onceMadePrivate.Apply(new IdeaPrivacySet(onceMadePrivate.Id, IsPrivate: true, Now, Owner));
        onceMadePrivate.Scope.Should().Be(ReplicationScope.Private);
    }

    [Fact]
    public void A_scope_widen_applied_before_its_own_capture_is_never_narrowed_back_down()
    {
        // independent pre-PR review, idea 19489eff, cycle 11, conformance, high: a replicated
        // stream can apply IdeaCaptured after IdeaScopeSet for the same idea when the two arrive
        // at a node through different relays. IdeaCaptured's own InitialScope must never undo a
        // widen that was already applied — it only ever supplies the starting point.
        Guid ideaId = DomainId.New();
        IdeaAggregate idea = new();
        idea.Apply(new IdeaScopeSet(ideaId, ReplicationScope.Team, Now, Owner));

        idea.Apply(new IdeaCaptured(ideaId, Owner, "out-of-order idea", null, Now.AddSeconds(1), InitialScope: ReplicationScope.Fleet));

        idea.Scope.Should().Be(
            ReplicationScope.Team, "the earlier-applied widen must survive a later-processed capture that only ever knew about Fleet");
    }

    private static IdeaAggregate Captured(string text, Guid? projectId = null)
    {
        IdeaAggregate idea = new();
        idea.Apply(IdeaDecider.Capture(DomainId.New(), Owner, text, projectId, Now, ProjectHome.None));
        return idea;
    }
}

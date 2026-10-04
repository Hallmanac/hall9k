using FluentAssertions;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Idea.Rendering;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Trust;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// An idea's assignee (card D of idea 8d0b724b): who has laid hold of an idea is its own fact,
/// separate from its project, carried by <see cref="IdeaAssigneeSet"/> and
/// <see cref="IdeaAssigneeCleared"/>. The rule that says who may set or clear it is judged elsewhere,
/// over verified owner roots; this pins the model, the decider's own refusals, the read model, and
/// that <see cref="TaskOwnerRule"/> answers an idea the way the task commands answer a task.
/// </summary>
public sealed class IdeaAssigneeTests
{
    private const string CreatorRoot = "creator-root-fingerprint";
    private const string AssigneeRoot = "assignee-root-fingerprint";
    private const string TeammateRoot = "teammate-root-fingerprint";
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Creator = DomainId.New();

    [Fact]
    public void Capturing_an_idea_assigns_nobody_not_even_its_creator()
    {
        IdeaAggregate idea = Captured();

        idea.AssigneeOwnerId.Should().BeNull();
        idea.AssigneeOwnerFingerprint.Should().BeNull();
        idea.OwnerId.Should().Be(Creator, "the creator fallback is what decides an idea nobody holds");
    }

    [Fact]
    public void Setting_then_clearing_the_assignee_replays_through_the_aggregate_and_the_projection()
    {
        IdeaAggregate idea = Captured();
        Guid assignee = DomainId.New();
        IdeaAssigneeSet set = new(idea.Id, assignee, AssigneeRoot, Now, Creator);
        idea.Apply(set);

        idea.AssigneeOwnerId.Should().Be(assignee);
        idea.AssigneeOwnerFingerprint.Should().Be(AssigneeRoot);
        idea.State.Should().Be(IdeaState.Captured, "holding an idea does not move its discovery state");

        IdeaDetailsProjection projection = new();
        IdeaDetails view = projection.Create(new FakeEvent<IdeaCaptured>(
            new IdeaCaptured(idea.Id, Creator, "A rough thought", ProjectId: null, Now)));
        projection.Apply(new FakeEvent<IdeaAssigneeSet>(set), view);
        view.AssigneeOwnerId.Should().Be(assignee);
        view.AssigneeOwnerFingerprint.Should().Be(AssigneeRoot);

        IdeaAssigneeCleared cleared = new(idea.Id, "no time", Now.AddHours(1), assignee);
        idea.Apply(cleared);
        projection.Apply(new FakeEvent<IdeaAssigneeCleared>(cleared), view);

        idea.AssigneeOwnerId.Should().BeNull();
        idea.AssigneeOwnerFingerprint.Should().BeNull();
        view.AssigneeOwnerId.Should().BeNull();
        view.AssigneeOwnerFingerprint.Should().BeNull();
    }

    [Fact]
    public void Moving_an_idea_to_a_project_leaves_who_holds_it_alone()
    {
        IdeaAggregate idea = Captured();
        Guid assignee = DomainId.New();
        idea.Apply(new IdeaAssigneeSet(idea.Id, assignee, AssigneeRoot, Now, Creator));

        Guid projectId = DomainId.New();
        idea.Apply(IdeaDecider.AssignToProject(idea, projectId, Now, Creator));

        idea.ProjectId.Should().Be(projectId);
        idea.AssigneeOwnerId.Should().Be(assignee);
    }

    [Fact]
    public void Naming_another_member_on_an_idea_below_team_scope_is_refused_and_names_idea_share()
    {
        IdeaAggregate idea = Captured();
        idea.Scope.Should().Be(ReplicationScope.Fleet, "a fresh capture is fleet scope, which a teammate never receives");

        Action handOff = () => IdeaDecider.SetAssignee(
            idea, DomainId.New(), TeammateRoot, assigneeIsActor: false, Now, Creator);

        handOff.Should().Throw<DomainConflictException>().Which.Message
            .Should().Contain("h9k idea share").And.Contain(idea.Id.ToString());
    }

    [Fact]
    public void Laying_hold_of_a_fleet_scoped_idea_for_yourself_needs_no_sharing()
    {
        IdeaAggregate idea = Captured();

        IdeaAssigneeSet? set = IdeaDecider.SetAssignee(idea, Creator, CreatorRoot, assigneeIsActor: true, Now, Creator);

        set.Should().NotBeNull();
        set!.AssigneeOwnerRootFingerprint.Should().Be(CreatorRoot);
    }

    [Fact]
    public void Naming_another_member_on_a_team_scoped_idea_is_recorded_with_their_root()
    {
        IdeaAggregate idea = Captured();
        idea.Apply(new IdeaScopeSet(idea.Id, ReplicationScope.Team, Now, Creator));
        Guid teammate = DomainId.New();

        IdeaAssigneeSet? set = IdeaDecider.SetAssignee(idea, teammate, TeammateRoot, assigneeIsActor: false, Now, Creator);

        set.Should().NotBeNull();
        set!.AssigneeOwnerId.Should().Be(teammate);
        set.AssigneeOwnerRootFingerprint.Should().Be(TeammateRoot);
        set.OnBehalfOfOwnerRootFingerprint.Should().BeNull("an owner's own act records no override");
    }

    [Fact]
    public void Assigning_the_member_who_already_holds_the_idea_records_nothing()
    {
        IdeaAggregate idea = Captured();
        Guid assignee = DomainId.New();
        idea.Apply(new IdeaAssigneeSet(idea.Id, assignee, AssigneeRoot, Now, Creator));

        IdeaDecider.SetAssignee(idea, assignee, AssigneeRoot, assigneeIsActor: true, Now, assignee).Should().BeNull();
    }

    [Fact]
    public void An_ended_idea_has_nothing_left_to_hand_on_or_let_go_of()
    {
        IdeaAggregate idea = Captured();
        Guid assignee = DomainId.New();
        idea.Apply(new IdeaAssigneeSet(idea.Id, assignee, AssigneeRoot, Now, Creator));
        idea.Apply(new IdeaArchived(idea.Id, "not worth it", Now, assignee));

        Action assign = () => IdeaDecider.SetAssignee(idea, assignee, AssigneeRoot, assigneeIsActor: true, Now, assignee);
        Action clear = () => IdeaDecider.ClearAssignee(idea, null, Now, assignee);

        assign.Should().Throw<DomainConflictException>().Which.Message.Should().Contain("archived");
        clear.Should().Throw<DomainConflictException>().Which.Message.Should().Contain("archived");
    }

    [Fact]
    public void Letting_go_of_an_idea_nobody_holds_is_refused()
    {
        Action clear = () => IdeaDecider.ClearAssignee(Captured(), null, Now, Creator);

        clear.Should().Throw<DomainConflictException>().Which.Message.Should().Contain("no assignee");
    }

    [Fact]
    public void A_naming_with_no_member_at_all_is_refused()
    {
        Action assign = () => IdeaDecider.SetAssignee(Captured(), Guid.Empty, null, assigneeIsActor: true, Now, Creator);

        assign.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void An_idea_whose_creator_cannot_be_resolved_is_unknown_and_the_refusal_says_idea_and_names_the_override()
    {
        Guid ideaId = DomainId.New();
        TaskOwnerFacts facts = new(OwnerRootFact.Absent, OwnerRootFact.Absent, OwnerRootFact.Unresolved);
        TaskOwnerCheck check = TaskOwnerRule.Decide(CreatorRoot, facts);

        TaskOwnerOverrideDecision decision = TaskOwnerOverride.Decide(
            ideaId, "conclude", check, ownerLabel: null, assigneeLabel: null, holder: null, reason: null,
            OwnerRoleCheck.NotChecked, TaskOwnerRefusal.IdeaNoun);

        check.Outcome.Should().Be(TaskOwnerOutcome.Unknown);
        decision.Outcome.Should().Be(TaskOwnerOverrideOutcome.Refused);
        decision.Message.Should().StartWith($"Idea {DomainId.Short(ideaId)}")
            .And.Contain("creator cannot be resolved")
            .And.Contain("Owner-role member may conclude it")
            .And.Contain("--holder").And.Contain("--reason");
    }

    [Fact]
    public void The_override_words_a_task_gets_are_unchanged_by_the_noun_an_idea_adds()
    {
        Guid taskId = DomainId.New();
        TaskOwnerCheck check = new(TaskOwnerOutcome.NotOwner, CreatorRoot, null, null);

        TaskOwnerOverrideDecision decision = TaskOwnerOverride.Decide(
            taskId, "abandon", check, "Ryan", null, holder: null, reason: null, OwnerRoleCheck.NotChecked);

        decision.Message.Should().StartWith($"Task {DomainId.Short(taskId)} belongs to Ryan");
    }

    [Fact]
    public void The_rendered_idea_document_names_the_assignee_and_writes_no_line_for_an_idea_nobody_holds()
    {
        IdeaDetails nobody = Details();
        IdeaDetails held = Details();
        held.AssigneeOwnerId = DomainId.New();
        held.AssigneeOwnerFingerprint = AssigneeRoot;

        IdeaDocumentRenderer.Render(nobody, "hall9k").Should().NotContain("assignee:");
        IdeaDocumentRenderer.Render(held, "hall9k", "Ryan").Should().Contain("assignee: Ryan");
        IdeaDocumentRenderer.Render(held, "hall9k").Should().Contain("assignee: assignee-roo",
            "an assignee this node cannot name falls back to the root's short form, never to a blank");
    }

    [Fact]
    public void The_assignee_label_prefers_this_nodes_own_record_then_the_project_label_then_the_short_id()
    {
        Guid assigneeId = DomainId.New();
        Guid projectId = DomainId.New();
        IdeaDetails idea = Details();
        idea.ProjectId = projectId;
        idea.AssigneeOwnerId = assigneeId;
        idea.AssigneeOwnerFingerprint = AssigneeRoot;
        OwnerDetails local = new() { Id = assigneeId, Name = "Ryan", RootFingerprint = AssigneeRoot };
        ProjectMemberLabels labels = new()
        {
            Id = projectId,
            Labels = [new ProjectMemberLabel(AssigneeRoot, [], DisplayName.Trusted("Ryan Smith"), null)],
        };

        IdeaAssigneeLabel.Of(idea, new Dictionary<Guid, OwnerDetails> { [assigneeId] = local }, new Dictionary<Guid, ProjectMemberLabels>())
            .Should().Be("Ryan");
        IdeaAssigneeLabel.Of(idea, new Dictionary<Guid, OwnerDetails>(), new Dictionary<Guid, ProjectMemberLabels> { [projectId] = labels })
            .Should().Be("Ryan Smith");
        idea.AssigneeOwnerFingerprint = null;
        IdeaAssigneeLabel.Of(idea, new Dictionary<Guid, OwnerDetails>(), new Dictionary<Guid, ProjectMemberLabels>())
            .Should().Be(DomainId.Short(assigneeId));
        IdeaAssigneeLabel.Of(Details(), new Dictionary<Guid, OwnerDetails>(), new Dictionary<Guid, ProjectMemberLabels>())
            .Should().BeNull("an idea nobody holds has no assignee to label");
    }

    private static IdeaAggregate Captured()
    {
        IdeaAggregate idea = new();
        idea.Apply(IdeaDecider.Capture(DomainId.New(), Creator, "A rough thought", projectId: null, Now, ProjectHome.None));
        return idea;
    }

    private static IdeaDetails Details() => new()
    {
        Id = DomainId.New(),
        OwnerId = Creator,
        Text = "An idea worth capturing",
        State = IdeaState.Captured,
        CapturedAt = Now,
    };
}

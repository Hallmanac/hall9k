using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// A task carries one external item and the platform has no unlink, so a second link is refused. The
/// one exception is a Linear issue that moved to another team: Linear gives it a new identifier, and
/// the new one replaces the recorded one only on Linear's own word that the recorded identifier is a
/// previous identifier of the same issue.
/// </summary>
public sealed class LinearRelinkTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = DomainId.New();

    private static ExternalReference Linear(string identifier) => new(WorkItemProvider.Linear, identifier);

    private static WorkItemLinked Link(
        TaskAggregate task, ExternalReference reference, IReadOnlyList<string>? previous = null) =>
        TaskDecider.LinkWorkItem(
            task, reference, observedTitle: "Add rate limiting", observedStatus: "Todo (open)",
            observedAt: Now, linkedAt: Now, linkedByOwnerId: Owner, previousIdentifiers: previous);

    private static TaskAggregate TaskLinkedTo(ExternalReference reference)
    {
        TaskAggregate task = Queued();
        task.Apply(Link(task, reference));
        return task;
    }

    [Fact]
    public void A_moved_issue_replaces_the_link_when_linear_lists_the_recorded_identifier_as_a_previous_one()
    {
        TaskAggregate task = TaskLinkedTo(Linear("ENG-123"));

        WorkItemLinked relinked = Link(task, Linear("PLAT-9"), previous: ["OPS-2", "ENG-123"]);
        task.Apply(relinked);

        task.ExternalReference.Should().Be(Linear("PLAT-9"));
        task.ExternalReference!.Key.Should().Be("PLAT-9");
    }

    [Fact]
    public void The_proof_is_matched_without_regard_to_case()
    {
        TaskAggregate task = TaskLinkedTo(Linear("ENG-123"));

        Action relink = () => Link(task, Linear("PLAT-9"), previous: ["eng-123"]);

        relink.Should().NotThrow();
    }

    [Fact]
    public void A_different_linear_issue_with_no_proof_is_refused_as_every_second_link_is()
    {
        TaskAggregate task = TaskLinkedTo(Linear("ENG-123"));

        Action withoutProof = () => Link(task, Linear("ENG-124"));
        Action unrelatedProof = () => Link(task, Linear("ENG-124"), previous: ["ENG-7"]);
        Action emptyProof = () => Link(task, Linear("ENG-124"), previous: []);

        withoutProof.Should().Throw<DomainConflictException>().WithMessage("*already linked to linear:ENG-123*");
        unrelatedProof.Should().Throw<DomainConflictException>();
        emptyProof.Should().Throw<DomainConflictException>();
        task.ExternalReference.Should().Be(Linear("ENG-123"));
    }

    [Theory]
    [InlineData(WorkItemProviderCase.Jira)]
    [InlineData(WorkItemProviderCase.GitHub)]
    public void The_exception_is_for_a_linear_link_replaced_by_a_linear_link_and_nothing_else(WorkItemProviderCase existing)
    {
        ExternalReference recorded = existing == WorkItemProviderCase.Jira
            ? new ExternalReference(WorkItemProvider.Jira, "ENG-123")
            : new ExternalReference(WorkItemProvider.GitHub, "Hallmanac/hall9k#42");
        TaskAggregate task = TaskLinkedTo(recorded);

        Action toLinear = () => Link(task, Linear("PLAT-9"), previous: [recorded.Reference]);
        Action jiraToJira = () => TaskDecider.LinkWorkItem(
            task, new ExternalReference(WorkItemProvider.Jira, "PROJ-99"), "t", "To Do", Now, Now, Owner,
            previousIdentifiers: ["ENG-123"]);

        toLinear.Should().Throw<DomainConflictException>("a Jira or GitHub link has no moved-issue exception");
        jiraToJira.Should().Throw<DomainConflictException>("only a Linear link may be replaced, whatever proof is offered");
    }

    [Fact]
    public void Linking_the_same_issue_again_is_not_a_replacement()
    {
        TaskAggregate task = TaskLinkedTo(Linear("ENG-123"));

        TaskDecider.AlreadyLinkedTo(task, Linear("ENG-123")).Should().BeTrue();
        Action relink = () => Link(task, Linear("ENG-123"), previous: ["ENG-123"]);
        relink.Should().Throw<DomainConflictException>("the command answers 'already linked' before the decider is asked");
    }

    [Fact]
    public void A_branch_cut_under_the_old_identifier_keeps_its_name_after_the_relink()
    {
        BranchNameTemplate template = BranchNameTemplate.Parse("{key}-{slug}");
        TaskAggregate task = TaskLinkedTo(Linear("ENG-123"));
        string branchRecordedAtDispatch = template.Render(task.Id, task.Objective, task.ExternalReference!.Key);

        task.Apply(Link(task, Linear("PLAT-9"), previous: ["ENG-123"]));

        branchRecordedAtDispatch.Should().StartWith("ENG-123-");
        template.Render(task.Id, task.Objective, task.ExternalReference!.Key)
            .Should().StartWith("PLAT-9-", "a render from the new reference differs, which is why nothing re-renders:")
            .And.NotBe(branchRecordedAtDispatch);
    }

    public enum WorkItemProviderCase
    {
        Jira,
        GitHub,
    }

    private static TaskAggregate Queued()
    {
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Add rate limiting to auth endpoints",
            ["429 returned past the limit"], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null,
            addedAt: Now, addedByOwnerId: Owner));
        task.Apply(TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, Owner));
        task.Apply(TaskDecider.Assign(task, Owner, [], Now, Owner));
        return task;
    }
}

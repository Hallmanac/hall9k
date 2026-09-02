using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The load-bearing rule under <see cref="BranchNameTemplate"/>: no token may resolve to a
/// different value at dispatch than it does at push. <c>RunDispatched</c> records the rendered
/// branch and <c>PullRequestOpener</c> pushes that recorded name verbatim, hours or days later, so
/// a token derived from task state a human can still edit would recreate the exact failure a
/// hand-renamed branch caused in the field on 2026-08-31 — the push hits a refspec that no longer
/// exists and the task parks Failed — except through a supported feature.
/// <para>
/// Each test here asserts the gate that actually freezes one token, in the decider that owns it,
/// rather than asserting the branch name twice. If a future change loosens one of these gates,
/// the token it protects has to leave the template's token set with it.
/// </para>
/// </summary>
public sealed class BranchNameTokensAreFixedAtDispatchTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = DomainId.New();

    /// <summary>
    /// Every token the template ships, read from <see cref="BranchNameTemplate.KnownTokens"/> —
    /// the same dispatch table <see cref="BranchNameTemplate.Render"/> renders from — rather than
    /// a hand-copied list, so a token added to that table without a freezing gate below fails this
    /// test rather than reaching a project's branch names unproven.
    /// </summary>
    [Fact]
    public void The_shipped_token_set_is_exactly_the_three_this_file_proves_fixed()
    {
        BranchNameTemplate.KnownTokens.Should().BeEquivalentTo(["shortid", "slug", "key"]);
    }

    /// <summary>
    /// <c>{shortid}</c>: the task's own id, assigned once at <c>h9k task add</c> and never
    /// rewritten by any event on the stream.
    /// </summary>
    [Fact]
    public void The_shortid_token_is_the_task_id_itself_and_nothing_can_edit_it()
    {
        TaskAggregate task = QueuedTask();
        Guid idAtDispatch = task.Id;

        task.Apply(TaskDecider.Claim(task, DomainId.New(), Owner, DomainId.New(), Now));

        task.Id.Should().Be(idAtDispatch);
        BranchNameTemplate.Parse("{shortid}").Render(task.Id, task.Objective, externalKey: null)
            .Should().Be(DomainId.Short(idAtDispatch));
    }

    /// <summary>
    /// <c>{slug}</c>: the objective. Revision is Draft-only (Decisions Log #34) and a task past
    /// Published cannot get back to Draft, so nothing a dispatched task can reach edits it again.
    /// </summary>
    [Fact]
    public void The_slug_token_cannot_be_revised_once_the_task_is_dispatchable()
    {
        TaskAggregate queued = QueuedTask();

        Action revise = () => TaskDecider.Revise(
            queued, objective: "A completely different objective",
            acceptanceCriteria: Optional<IReadOnlyList<string>>.None,
            agentContext: Optional<string>.None,
            blockedBy: Optional<IReadOnlyList<Guid>>.None,
            type: Optional<TaskType>.None,
            model: Optional<AgentModel>.None,
            revisedAt: Now, revisedByOwnerId: Owner);

        revise.Should().Throw<DomainConflictException>("only a draft can be revised");
    }

    /// <summary>
    /// The other half of the same gate: the way back to Draft is refused from Queued onward, so
    /// the unassign-first ceremony cannot be skipped into an edit while a node may already be
    /// cutting the branch.
    /// </summary>
    [Fact]
    public void The_slug_token_cannot_be_reopened_for_revision_from_a_dispatchable_state()
    {
        TaskAggregate queued = QueuedTask();
        TaskAggregate claimed = QueuedTask();
        claimed.Apply(TaskDecider.Claim(claimed, DomainId.New(), Owner, DomainId.New(), Now));

        Action fromQueued = () => TaskDecider.ReturnToDraft(queued, reason: null, Now, Owner);
        Action fromClaimed = () => TaskDecider.ReturnToDraft(claimed, reason: null, Now, Owner);

        fromQueued.Should().Throw<DomainConflictException>();
        fromClaimed.Should().Throw<DomainConflictException>();
    }

    /// <summary>
    /// <c>{key}</c>: a task carries one external item, and the platform has no unlink at all, so a
    /// key present when the branch was cut is that task's key forever.
    /// </summary>
    [Fact]
    public void The_key_token_cannot_change_once_the_task_carries_one()
    {
        TaskAggregate linked = QueuedTask();
        linked.Apply(TaskDecider.LinkWorkItem(
            linked, new ExternalReference(WorkItemProvider.Jira, "ARX-14"),
            observedTitle: "Add rate limiting", observedStatus: "In Progress",
            observedAt: Now, linkedAt: Now, linkedByOwnerId: Owner));

        Action relink = () => TaskDecider.LinkWorkItem(
            linked, new ExternalReference(WorkItemProvider.Jira, "ARX-99"),
            observedTitle: "Something else", observedStatus: "To Do",
            observedAt: Now, linkedAt: Now, linkedByOwnerId: Owner);

        relink.Should().Throw<DomainConflictException>("a task carries one external item");
        linked.ExternalReference!.Key.Should().Be("ARX-14");
    }

    /// <summary>
    /// The one case that is not frozen — a task with no reference at dispatch can be linked mid-run
    /// — is safe because it never re-renders: the branch was named from what was observed at
    /// dispatch, the run's recorded name is what gets pushed, and a link arriving afterwards
    /// changes nothing about the ref that exists on disk. This test states that as the design
    /// rather than leaving it to be rediscovered.
    /// </summary>
    [Fact]
    public void A_link_arriving_after_dispatch_does_not_change_the_name_the_branch_was_cut_under()
    {
        BranchNameTemplate template = BranchNameTemplate.Parse("{key}-{slug}");
        TaskAggregate task = QueuedTask();
        string branchCutAtDispatch = template.Render(task.Id, task.Objective, externalKey: null);

        task.Apply(TaskDecider.LinkWorkItem(
            task, new ExternalReference(WorkItemProvider.Jira, "ARX-14"),
            observedTitle: "Add rate limiting", observedStatus: "In Progress",
            observedAt: Now, linkedAt: Now, linkedByOwnerId: Owner));

        branchCutAtDispatch.Should().StartWith($"{BranchNameTemplate.NoExternalKey}-");
        template.Render(task.Id, task.Objective, task.ExternalReference!.Key)
            .Should().NotBe(branchCutAtDispatch,
                "which is exactly why nothing downstream re-renders — PullRequestOpener pushes the "
                + "name recorded on RunDispatched, never a freshly derived one");
    }

    private static TaskAggregate QueuedTask()
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

using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Tasks.Handlers;

/// <summary>
/// The single home for task decisions. Both doors call it: the CLI appends its returned
/// events directly (no Wolverine host), the daemon's handlers adapt over it (TASK-MODEL.md §7).
/// Terminal states are Done and Abandoned only; Failed is a needs-human waypoint whose
/// three exits are Retry, Resolve, and Abandon (Decisions Log #27).
/// Task development and task dispatch are separate lifecycles (Decisions Log #34): Add
/// produces a Draft, Publish is the readiness gate, and Assign — always an explicit human
/// act — is the dispatch trigger that makes a task claimable.
/// </summary>
public static class TaskDecider
{
    public static TaskAdded Add(
        Guid id,
        Guid projectId,
        string objective,
        IReadOnlyList<string> acceptanceCriteria,
        TaskType type,
        string? agentContext,
        TaskConstraints? constraints,
        ExternalReference? externalReference,
        DateTimeOffset addedAt,
        Guid addedByOwnerId,
        AgentModel? model = null,
        IReadOnlyList<Guid>? blockedBy = null,
        Guid? sourceIdeaId = null)
    {
        if (projectId == Guid.Empty)
        {
            throw new DomainValidationException("A task belongs to a project.");
        }

        if (objective.IsBlank())
        {
            throw new DomainValidationException(
                "A task requires an outcome-phrased objective — it is what the draft is about.");
        }

        // Creation is identity, not readiness (Decisions Log #34): a draft exists in order to
        // be developed, so acceptance criteria are gathered rather than demanded here. The
        // readiness contract is enforced once, at Publish, as an invariant of that state.
        string[] criteria = [.. acceptanceCriteria.Where(c => c.IsNotBlank())];
        Guid[] dependencies = Dependencies(id, blockedBy);

        return new TaskAdded(
            id, projectId, objective, criteria, type, agentContext, constraints,
            externalReference, addedAt, addedByOwnerId, VetModel(model), dependencies,
            StartsAsDraft: true, SourceIdeaId: sourceIdeaId);
    }

    /// <summary>
    /// The override reaches the executor's /bin/sh command line, so it is vetted here rather
    /// than quoted and hoped for; Unknown simply states no preference (Decisions Log #33).
    /// </summary>
    private static AgentModel VetModel(AgentModel? model)
    {
        AgentModel chosen = AgentModel.FromInput(model);
        return chosen == AgentModel.Unknown || chosen.IsWellFormed
            ? chosen
            : throw new DomainValidationException(
                $"'{chosen.Value}' is not a usable model name. Use a tier alias "
                + $"({AgentModel.Fable}, {AgentModel.Opus}, {AgentModel.Sonnet}, {AgentModel.Haiku}) or an exact "
                + $"model id (for example {AgentModel.PlatformFallback}); letters, digits, and . _ - : / @ [ ] only.");
    }

    /// <summary>
    /// The readiness gate, Draft -> Published (Decisions Log #34). Everything a Published task
    /// promises is checked exactly here: the contract is complete, every dependency names a
    /// real task, and no cycle is reachable through the chain. After this the text is frozen —
    /// a Published task may be assigned at any moment, and revising one would break that.
    /// </summary>
    public static TaskPublished Publish(
        TaskAggregate task,
        TaskDependencyGraph graph,
        DateTimeOffset publishedAt,
        Guid publishedByOwnerId)
    {
        if (task.State != TaskState.Draft)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is {task.State.Value}, not Draft — only a draft publishes. " + task.State switch
                {
                    var state when state == TaskState.Published =>
                        $"It is already published; assign it with h9k task assign {task.Id}.",
                    var state when state.IsAssigned =>
                        $"Return it to Draft first: h9k task unassign {task.Id} && h9k task draft {task.Id}.",
                    _ => "It has already been dispatched, and unassign and draft are both refused from here. "
                        + "Work that has run gets a new task, not a second publication.",
                });
        }

        if (task.Objective.IsBlank())
        {
            throw new DomainValidationException(
                "A task requires an outcome-phrased objective — the readiness contract, PLAN.md §4. " +
                $"Set one with: h9k task revise {task.Id} --objective <one sentence>");
        }

        if (task.AcceptanceCriteria.Count == 0)
        {
            throw new DomainValidationException(
                "A task requires at least one checkable acceptance criterion before it can be published. " +
                "If you can't write acceptance criteria, the task isn't ready (PLAN.md §4). " +
                $"Add them with: h9k task revise {task.Id} --criteria <criterion> (repeat the option for more)");
        }

        if (graph.Missing(task.BlockedBy) is { Count: > 0 } missing)
        {
            throw new DomainNotFoundException(
                $"Task {task.Id} depends on {missing.Count} task(s) the platform does not know: " +
                $"{string.Join(", ", missing)}. Drop them with h9k task revise {task.Id} --blocked-by <id> " +
                "(the option replaces the whole set) or --clear-dependencies.");
        }

        // Drafts may transiently hold a cycle while a graph is authored; publishing is where
        // that stops, because a cycle can never become assignable — every task in it would
        // wait forever on another that waits on it.
        if (graph.FindCycle(task.Id, task.BlockedBy) is { } cycle)
        {
            throw new DomainBusinessRuleException(
                "Publishing would close a dependency cycle, and nothing in a cycle can ever run: " +
                $"{graph.DescribeCycle(cycle, task.Id, task.Objective)}. " +
                "Break the cycle with h9k task revise on any task in it, then publish.");
        }

        return new TaskPublished(task.Id, publishedAt, publishedByOwnerId);
    }

    /// <summary>
    /// Revision is Draft-only (Decisions Log #34), because every later state carries a promise
    /// editing would break: Published promises a human may assign it at any moment and that it
    /// satisfies the readiness contract; assigned promises a node may read it at any moment,
    /// and revising a claimable task races the dispatcher. The revert ceremony
    /// (unassign -> draft -> revise -> publish -> assign) is deliberate, not accidental friction.
    /// Absent fields are left alone; only what is passed is recorded.
    /// </summary>
    public static TaskRevised Revise(
        TaskAggregate task,
        Optional<string> objective,
        Optional<IReadOnlyList<string>> acceptanceCriteria,
        Optional<string> agentContext,
        Optional<IReadOnlyList<Guid>> blockedBy,
        Optional<TaskType> type,
        Optional<AgentModel> model,
        DateTimeOffset revisedAt,
        Guid revisedByOwnerId)
    {
        if (task.State != TaskState.Draft)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is {task.State.Value} — only a draft can be revised. " + task.State switch
                {
                    var state when state == TaskState.Published =>
                        $"Return it to Draft first: h9k task draft {task.Id}.",
                    var state when state.IsAssigned =>
                        $"Unassign it, then return it to Draft: h9k task unassign {task.Id} && h9k task draft {task.Id}.",
                    _ => "A task that has already run gets a new task, not a rewritten contract.",
                });
        }

        if (objective.HasValue && objective.Value.IsBlank())
        {
            throw new DomainValidationException(
                "A revision cannot blank the objective — it is what the task is about. " +
                "Pass a new one, or walk away with h9k task abandon.");
        }

        Optional<IReadOnlyList<string>> criteria = acceptanceCriteria.HasValue
            ? Optional<IReadOnlyList<string>>.Of([.. (acceptanceCriteria.Value ?? []).Where(c => c.IsNotBlank())])
            : Optional<IReadOnlyList<string>>.None;

        Optional<IReadOnlyList<Guid>> dependencies = blockedBy.HasValue
            ? Optional<IReadOnlyList<Guid>>.Of(Dependencies(task.Id, blockedBy.Value))
            : Optional<IReadOnlyList<Guid>>.None;

        Optional<AgentModel> chosenModel = model.HasValue
            ? Optional<AgentModel>.Of(VetModel(model.Value))
            : Optional<AgentModel>.None;

        if (!objective.HasValue && !criteria.HasValue && !agentContext.HasValue
            && !dependencies.HasValue && !type.HasValue && !chosenModel.HasValue)
        {
            throw new DomainValidationException(
                "A revision needs something to revise. Pass --objective, --criteria, --context, " +
                "--type, --model, --blocked-by, or --clear-dependencies.");
        }

        return new TaskRevised(
            task.Id, objective, criteria, agentContext, dependencies, type, chosenModel,
            revisedAt, revisedByOwnerId);
    }

    /// <summary>
    /// Published -> Draft: the explicit revert that reopens a task for revision. Refused from
    /// Queued and Blocked onward — unassign first, so returning a task the dispatcher can see
    /// to an editable state is never one accidental keystroke (Decisions Log #34).
    /// </summary>
    public static TaskReturnedToDraft ReturnToDraft(
        TaskAggregate task, string? reason, DateTimeOffset returnedAt, Guid returnedByOwnerId)
    {
        if (task.State == TaskState.Draft)
        {
            throw new DomainConflictException($"Task {task.Id} is already a draft.");
        }

        if (task.State != TaskState.Published)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is {task.State.Value} — only a published task returns to Draft. " +
                (task.State.IsAssigned
                    ? $"It is assigned; unassign it first: h9k task unassign {task.Id}."
                    : "A task that has already run cannot be edited back into a draft; add a new one."));
        }

        return new TaskReturnedToDraft(task.Id, reason, returnedAt, returnedByOwnerId);
    }

    /// <summary>
    /// The dispatch trigger, and the only way a task becomes claimable (Decisions Log #34).
    /// Always an explicit human act: no monitor and no CLI convenience appends this without
    /// being asked. Dependencies decide where it lands — Queued when every one has reached
    /// true closeout, Blocked otherwise — and the claim guard reads the assigned owner, so a
    /// node runs only its own owner's work.
    /// </summary>
    public static TaskAssigned Assign(
        TaskAggregate task,
        Guid assignedOwnerId,
        IReadOnlyList<TaskDependency> dependencies,
        DateTimeOffset assignedAt,
        Guid assignedByOwnerId)
    {
        if (task.State != TaskState.Published)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is {task.State.Value} — only a published task is assignable. " + task.State switch
                {
                    var state when state == TaskState.Draft => $"Publish it first: h9k task publish {task.Id}.",
                    var state when state.IsAssigned =>
                        $"It is already assigned; unassign it first: h9k task unassign {task.Id}.",
                    _ => "Its story has already ended.",
                });
        }

        if (assignedOwnerId == Guid.Empty)
        {
            throw new DomainValidationException("An assignment names the owner whose nodes may claim the task.");
        }

        if (task.BlockedBy.Except(dependencies.Select(dependency => dependency.Id)).ToArray() is { Length: > 0 } unresolved)
        {
            throw new DomainNotFoundException(
                $"Task {task.Id} depends on {unresolved.Length} task(s) the platform does not know: " +
                $"{string.Join(", ", unresolved)}.");
        }

        return new TaskAssigned(
            task.Id,
            assignedOwnerId,
            [.. dependencies.Where(dependency => dependency.Blocks).Select(dependency => dependency.Id)],
            assignedAt,
            assignedByOwnerId);
    }

    /// <summary>
    /// Queued or Blocked -> Published: takes the task back out of the dispatcher's sight so it
    /// can be revised. Refused while a lease is held — a node is running it, and pulling the
    /// contract out from under a live agent is exactly the race the lifecycle exists to
    /// prevent. Let the run finish, or abandon the task.
    /// </summary>
    public static TaskUnassigned Unassign(
        TaskAggregate task, string? reason, bool leaseHeld, DateTimeOffset unassignedAt, Guid unassignedByOwnerId)
    {
        if (!task.State.IsAssigned)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is {task.State.Value} — only an assigned task (Queued or Blocked) unassigns." +
                (task.State == TaskState.Published ? " It is already published and unassigned." : string.Empty));
        }

        if (leaseHeld)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is leased by a node right now — unassigning it would pull the contract out " +
                "from under a running agent. Let the run finish, or abandon the task.");
        }

        return new TaskUnassigned(task.Id, reason, unassignedAt, unassignedByOwnerId);
    }

    /// <summary>Whether this task still waits on that dependency — the re-evaluation pre-check.</summary>
    public static bool AwaitsDependency(TaskAggregate task, Guid dependencyId) =>
        task.State == TaskState.Blocked && task.UnmetDependencies.Contains(dependencyId);

    /// <summary>
    /// One blocker reached true closeout. Clearing the last one moves Blocked -> Queued, which
    /// is the only unblocking path there is: no weaker completion signal exists to abuse.
    /// </summary>
    public static TaskDependencyCompleted DependencyCompleted(
        TaskAggregate task, Guid dependencyId, DateTimeOffset completedAt)
    {
        if (!AwaitsDependency(task, dependencyId))
        {
            throw new DomainConflictException(
                $"Task {task.Id} is {task.State.Value} and is not waiting on dependency {dependencyId}.");
        }

        return new TaskDependencyCompleted(
            task.Id,
            dependencyId,
            [.. task.UnmetDependencies.Where(id => id != dependencyId)],
            completedAt);
    }

    /// <summary>Whether that blocker's death is already recorded on this task.</summary>
    public static bool HasRecordedDependencyFailure(TaskAggregate task, Guid dependencyId) =>
        task.DeadDependencies.Contains(dependencyId);

    /// <summary>
    /// A blocker reached Failed or Abandoned. The dependent stays Blocked and surfaces as
    /// NeedsHuman with the reason: silently unblocking would dispatch work whose premise died,
    /// and silence would strand it. Recorded once per dependency — repeating the observation
    /// every sweep tells the human nothing new.
    /// </summary>
    public static TaskDependencyFailed DependencyFailed(
        TaskAggregate task, Guid dependencyId, string reason, DateTimeOffset observedAt)
    {
        if (!AwaitsDependency(task, dependencyId))
        {
            throw new DomainConflictException(
                $"Task {task.Id} is {task.State.Value} and is not waiting on dependency {dependencyId}.");
        }

        if (HasRecordedDependencyFailure(task, dependencyId))
        {
            throw new DomainConflictException(
                $"Task {task.Id} already records dependency {dependencyId} as dead.");
        }

        if (reason.IsBlank())
        {
            throw new DomainValidationException("A dead dependency is recorded with what was observed about it.");
        }

        return new TaskDependencyFailed(task.Id, dependencyId, reason, observedAt);
    }

    /// <summary>
    /// Dependency ids as the stream should carry them: de-duplicated, order preserved, and
    /// never the task itself — a self-edge is a cycle of one and could never be published.
    /// </summary>
    private static Guid[] Dependencies(Guid id, IReadOnlyList<Guid>? blockedBy)
    {
        Guid[] dependencies = [.. (blockedBy ?? []).Where(dependency => dependency != Guid.Empty).Distinct()];
        return dependencies.Contains(id)
            ? throw new DomainValidationException("A task cannot depend on itself.")
            : dependencies;
    }

    /// <summary>
    /// The claim guard is one rule and there is no other path to a claim (Decisions Log #34):
    /// the task is Queued <em>and</em> its assigned owner is this node's owner. Queued is only
    /// reachable through an explicit human assignment whose dependencies are all closed out,
    /// so both halves of "should this run, and on whose nodes" are answered before a node ever
    /// looks at the task.
    /// </summary>
    public static TaskClaimed Claim(TaskAggregate task, Guid nodeId, Guid ownerId, Guid runId, DateTimeOffset claimedAt)
    {
        if (task.State != TaskState.Queued)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is {task.State.Value}, not Queued — it cannot be claimed.");
        }

        if (task.AssignedOwnerId != ownerId)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is assigned to {(task.AssignedOwnerId is { } assignee ? assignee.ToString() : "nobody")}, " +
                $"not to this node's owner ({ownerId}) — a node claims only its own owner's work.");
        }

        return new TaskClaimed(task.Id, nodeId, ownerId, task.LeaseGeneration + 1, runId, claimedAt);
    }

    public static TaskRequeued Requeue(TaskAggregate task, RequeueReason reason, DateTimeOffset requeuedAt)
    {
        if (task.State != TaskState.Claimed && task.State != TaskState.NeedsHuman)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is {task.State.Value} — only claimed or needs-human tasks requeue.");
        }

        return new TaskRequeued(task.Id, reason, requeuedAt);
    }

    public static QuestionAsked Ask(TaskAggregate task, Guid questionId, Guid runId, string question, DateTimeOffset askedAt)
    {
        if (task.State != TaskState.Claimed)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is {task.State.Value} — only a claimed task's run can ask.");
        }

        if (question.IsBlank())
        {
            throw new DomainValidationException("A question needs content.");
        }

        return new QuestionAsked(task.Id, questionId, runId, question, askedAt);
    }

    public static AnswerProvided Answer(TaskAggregate task, Guid questionId, string answer, DateTimeOffset answeredAt, Guid answeredByOwnerId)
    {
        if (task.PendingQuestionId is null)
        {
            throw new DomainConflictException($"Task {task.Id} has no pending question.");
        }

        if (task.PendingQuestionId != questionId)
        {
            throw new DomainConflictException(
                $"Question {questionId} is not task {task.Id}'s pending question.");
        }

        if (answer.IsBlank())
        {
            throw new DomainValidationException("An answer needs content.");
        }

        return new AnswerProvided(task.Id, questionId, answer, answeredAt, answeredByOwnerId);
    }

    public static TaskCompleted Complete(TaskAggregate task, Guid runId, string? pullRequestUrl, DateTimeOffset completedAt)
    {
        if (task.State != TaskState.Claimed)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is {task.State.Value} — only a claimed task completes.");
        }

        return new TaskCompleted(task.Id, runId, pullRequestUrl, completedAt);
    }

    /// <summary>
    /// Done is terminal for the work, not for the pull request: reopening queues a
    /// follow-up run on the existing PR branch (Decisions Log #20). Only from Done —
    /// Failed has its own human-only exits (Retry, Resolve, Abandon; logs #25/#27);
    /// Abandoned stays a dead end.
    /// </summary>
    public static TaskReopened Reopen(
        TaskAggregate task,
        Guid previousRunId,
        string branch,
        string? reason,
        FollowUpKind kind,
        bool automatic,
        DateTimeOffset reopenedAt,
        Guid reopenedByOwnerId)
    {
        if (task.State != TaskState.Done)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is {task.State.Value} — only a done task reopens for a follow-up run.");
        }

        if (task.PullRequestUrl.IsBlank())
        {
            throw new DomainConflictException(
                $"Task {task.Id} has no pull request — there is no review feedback to resolve.");
        }

        if (branch.IsBlank())
        {
            throw new DomainValidationException("A follow-up run needs the existing pull-request branch.");
        }

        return new TaskReopened(task.Id, previousRunId, branch, reason, reopenedAt, reopenedByOwnerId, kind, automatic);
    }

    /// <summary>
    /// The re-run exit from Failed (Decisions Log #25): failure of the machinery around
    /// the work must not permanently condemn the task that contains the work. Failed-only —
    /// Abandoned stays a dead end, and a done task's lever is Reopen — and human-only: no
    /// monitor calls this (a failure that repeats without human eyes is the never-loop-on-
    /// judgment rule, log #11). The next claim increments the lease generation as usual.
    /// The other two exits from Failed are Resolve (objective already met, log #27) and
    /// Abandon (walk away).
    /// </summary>
    public static TaskRetried Retry(
        TaskAggregate task,
        Guid? previousRunId,
        string? branch,
        string reason,
        DateTimeOffset retriedAt,
        Guid retriedByOwnerId)
    {
        if (task.State != TaskState.Failed)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is {task.State.Value} — only a failed task retries. " +
                "Abandoned is a dead end by design; a done task's follow-up lever is h9k pr resolve.");
        }

        if (reason.IsBlank())
        {
            throw new DomainValidationException(
                "A retry needs a reason — the stream records why the failure deserved another attempt.");
        }

        return new TaskRetried(task.Id, previousRunId, branch, reason, retriedAt, retriedByOwnerId);
    }

    /// <summary>
    /// Whether Fail would accept the task as it stands — the daemon's pre-check before
    /// appending. Failed is a needs-human waypoint rather than a terminal state (Decisions
    /// Log #27), but it still rejects a second Fail: piling failures onto a task that
    /// already waits for a human adds nothing the human doesn't know.
    /// </summary>
    public static bool CanFail(TaskAggregate task) =>
        task.State != TaskState.Failed && !task.State.IsTerminal;

    public static TaskFailed Fail(TaskAggregate task, Guid runId, string reason, DateTimeOffset failedAt)
    {
        if (!CanFail(task))
        {
            throw new DomainConflictException($"Task {task.Id} is already {task.State.Value}.");
        }

        return new TaskFailed(task.Id, runId, reason, failedAt);
    }

    /// <summary>
    /// The attestation exit from Failed (Decisions Log #27): the run failed but the
    /// objective was met anyway, so the task ends Done — with the failure still on the
    /// stream, never rewritten. Failed-only and human-only; the reason is required because
    /// an attestation without a why is a guess (the AGENTS.md never-guess rule). The other
    /// two exits from Failed are Retry (re-run) and Abandon (walk away).
    /// </summary>
    public static TaskResolved Resolve(
        TaskAggregate task,
        string reason,
        string? pullRequestUrl,
        DateTimeOffset resolvedAt,
        Guid resolvedByOwnerId)
    {
        if (task.State != TaskState.Failed)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is {task.State.Value} — only a failed task resolves to done. " +
                "Resolve is the attestation that the objective was met despite a run failure; " +
                "a task that hasn't failed has nothing to resolve.");
        }

        if (reason.IsBlank())
        {
            throw new DomainValidationException(
                "A resolution needs a reason — the attestation of why the objective counts as met " +
                "despite the failure. Without one the stream would be guessing (never guess at " +
                "unobserved facts, AGENTS.md).");
        }

        return new TaskResolved(task.Id, reason, pullRequestUrl, resolvedAt, resolvedByOwnerId);
    }

    /// <summary>
    /// The walk-away ending, from any non-terminal state — including Failed, where it is
    /// one of the three exits (retry, resolve, abandon; Decisions Log #27): "ended in
    /// failure" is only true when a human walks away, and that is what Abandoned means.
    /// </summary>
    public static TaskAbandoned Abandon(TaskAggregate task, string? reason, DateTimeOffset abandonedAt, Guid abandonedByOwnerId)
    {
        if (task.State.IsTerminal)
        {
            throw new DomainConflictException($"Task {task.Id} is already {task.State.Value}.");
        }

        return new TaskAbandoned(task.Id, reason, abandonedAt, abandonedByOwnerId);
    }
}

using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Tasks.Handlers;

/// <summary>
/// The single home for task decisions. Both doors call it: the CLI appends its returned
/// events directly (no Wolverine host), the daemon's handlers adapt over it (TASK-MODEL.md §7).
/// Terminal states are Done and Abandoned only; Failed is a needs-human waypoint whose
/// three exits are Retry, Resolve, and Abandon (Decisions Log #27).
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
        AgentModel? model = null)
    {
        if (projectId == Guid.Empty)
        {
            throw new DomainValidationException("A task belongs to a project.");
        }

        if (objective.IsBlank())
        {
            throw new DomainValidationException(
                "A task requires an outcome-phrased objective — the readiness contract, PLAN.md §4.");
        }

        string[] criteria = [.. acceptanceCriteria.Where(c => c.IsNotBlank())];
        if (criteria.Length == 0)
        {
            throw new DomainValidationException(
                "A task requires at least one checkable acceptance criterion. " +
                "If you can't write acceptance criteria, the task isn't ready (PLAN.md §4).");
        }

        // The override reaches the executor's /bin/sh command line, so it is vetted here
        // rather than quoted and hoped for; Unknown simply states no preference.
        AgentModel chosen = AgentModel.FromInput(model);
        if (chosen != AgentModel.Unknown && !chosen.IsWellFormed)
        {
            throw new DomainValidationException(
                $"'{chosen.Value}' is not a usable model name. Use a tier alias "
                + $"({AgentModel.Fable}, {AgentModel.Opus}, {AgentModel.Sonnet}, {AgentModel.Haiku}) or an exact "
                + $"model id (for example {AgentModel.PlatformFallback}); letters, digits, and . _ - : / @ [ ] only.");
        }

        return new TaskAdded(
            id, projectId, objective, criteria, type, agentContext, constraints,
            externalReference, addedAt, addedByOwnerId, chosen);
    }

    public static TaskClaimed Claim(TaskAggregate task, Guid nodeId, Guid ownerId, Guid runId, DateTimeOffset claimedAt)
    {
        if (task.State != TaskState.Queued)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is {task.State.Value}, not Queued — it cannot be claimed.");
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

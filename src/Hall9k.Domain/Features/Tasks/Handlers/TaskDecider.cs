using Hall9k.Domain.Features.Project;
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
        Guid? sourceIdeaId = null,
        Guid? epicId = null)
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
            StartsAsDraft: true, SourceIdeaId: sourceIdeaId, EpicId: epicId);
    }

    /// <summary>
    /// The override reaches the executor's shell command line, so it is vetted here rather
    /// than quoted and hoped for; Unknown simply states no preference (Decisions Log #33).
    /// <para>
    /// Public because a caller may need the answer before it has an event to build: h9k task add
    /// prompts a human for an objective and acceptance criteria between reading its options and
    /// reaching this decider, and refusing an unusable model only at the end would throw away
    /// what they typed in between. Asking early does not move the rule; the decider still vets.
    /// </para>
    /// </summary>
    public static AgentModel VetModel(AgentModel? model)
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
    /// <para>
    /// A project tracking its backlog (<paramref name="backlogPolicy"/> is Jira or GitHub
    /// issues) is also a dedup gate: a task with no <see cref="TaskAggregate.ExternalReference"/>
    /// and no <see cref="TaskAggregate.PendingPublicationProvider"/> already under way refuses to
    /// publish unless <paramref name="noExistingItemAttested"/> says a search already came back
    /// empty, or <paramref name="untracked"/> says this task should deliberately skip tracking
    /// altogether — an internal chore or platform task that should not pollute a team's tracker
    /// (backlog: a task can be published deliberately untracked under a tracking backlog policy).
    /// The platform never searches the tracker itself — that is the human's or the orchestrator's
    /// job, the same relay pattern every other park uses — so this only ever refuses or accepts
    /// an attestation, never checks it. Each attestation is recorded on <see cref="TaskPublished"/>
    /// only when the gate actually asked for it; a flag passed on a publish the gate never gated
    /// is clamped to false rather than asserting an unobserved fact on the stream.
    /// </para>
    /// </summary>
    public static TaskPublished Publish(
        TaskAggregate task,
        TaskDependencyGraph graph,
        DateTimeOffset publishedAt,
        Guid publishedByOwnerId,
        BacklogPolicy? backlogPolicy = null,
        bool noExistingItemAttested = false,
        bool untracked = false)
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

        BacklogPolicy policy = backlogPolicy ?? BacklogPolicy.None;

        // Two flags asking opposite things is an input error regardless of policy: one confirms
        // a search came back empty and proceeds to create or link an item, the other says skip
        // tracking this task altogether. Neither can be what the caller meant by the other.
        if (untracked && noExistingItemAttested)
        {
            throw new DomainValidationException(
                $"--untracked and --no-existing-item say opposite things for task {task.Id}: "
                + "--no-existing-item confirms none exists and proceeds to create or link one, while "
                + "--untracked skips tracking this task entirely. Pass one, not both.");
        }

        // --untracked only means something where there is tracking to skip, and a policy that is
        // neither Jira nor GitHubIssues — none, or a persisted value this build's closed set no
        // longer recognizes (the same "reads as no tracking" convention needsExistingItemCheck
        // uses below) — is the case where there is categorically none: the flag has nothing to
        // attest, so it is refused rather than silently ignored — unlike a defensively-passed
        // --no-existing-item, which clamps to false, --untracked is asserting a deliberate
        // choice, and a choice nobody asked for is worth teaching rather than swallowing. Below
        // are two more states the gate never asks an attestation for, and they are NOT treated
        // alike: an already-linked task's flag clamps silently, because nothing would be created
        // for it regardless of the attestation, while a publication already pending is refused,
        // just below, because that session mints a card whether or not this flag is honored, and
        // a silent clamp there would let it override the operator's choice without a word.
        if (untracked && policy != BacklogPolicy.Jira && policy != BacklogPolicy.GitHubIssues)
        {
            string policyDescription = policy == BacklogPolicy.None
                ? "policy none"
                : "an unrecognized policy, which reads as no tracking";
            throw new DomainValidationException(
                $"Task {task.Id}'s project does not track a backlog ({policyDescription}), so --untracked has "
                + $"nothing to skip. Publish without it: h9k task publish {task.Id}.");
        }

        // A publication already pending (h9k task push-to-jira, run by hand while the task was
        // still a Draft) is not "nothing to skip": the session it kicked off keeps running and
        // still mints the card regardless of what publish does here, so clamping --untracked
        // silently — the way an already-linked task's flag clamps below, harmlessly, because
        // nothing would be created for it either way — would instead let that in-flight work
        // defeat the very choice the operator just made. Refused with the same "teach rather
        // than swallow" reasoning as the policy check above, before the gate below ever gets to
        // decide whether an attestation is needed.
        if (untracked && task.PendingPublicationProvider is { } pendingProvider)
        {
            throw new DomainBusinessRuleException(
                $"Task {task.Id} already has a {pendingProvider.Value} publication request outstanding"
                + (task.PublicationSessionDispatched ? " and its session is running" : " and is waiting for the daemon")
                + ", and it still runs to completion regardless — --untracked cannot cancel it. Publish "
                + $"without it: h9k task publish {task.Id}.");
        }

        // A pending publication (h9k task push-to-jira, run by hand while the task was still a
        // Draft) is already a session on its way to minting the card this gate exists to avoid
        // duplicating — TrackInBacklogAsync already recognises and skips this exact state, so the
        // gate must too, or the only way through is an attestation that is factually wrong.
        bool needsExistingItemCheck = (policy == BacklogPolicy.Jira || policy == BacklogPolicy.GitHubIssues)
            && task.ExternalReference is null
            && task.PendingPublicationProvider is null;
        if (needsExistingItemCheck && !noExistingItemAttested && !untracked)
        {
            string tracker = policy == BacklogPolicy.Jira ? "Jira" : "GitHub issues";
            string linkCommand = policy == BacklogPolicy.Jira
                ? $"h9k task link-jira {task.Id} <key>"
                : $"h9k task link-issue {task.Id} <issue>";

            throw new DomainBusinessRuleException(
                $"This project tracks its backlog in {tracker}, and task {task.Id} carries no linked "
                + $"item yet. Search {tracker} for an open item that already covers this objective "
                + "before publishing, so this does not mint a duplicate. Found one? Link it: "
                + linkCommand + ". Confirmed none exists? Publish anyway with the attestation: "
                + $"h9k task publish {task.Id} --no-existing-item. Don't want this task tracked in "
                + $"{tracker} at all? Skip tracking for it deliberately: h9k task publish {task.Id} "
                + "--untracked.");
        }

        // Recorded only when the gate actually asked for one — a flag passed defensively on a
        // publish the gate never gated would otherwise assert an unobserved fact on the stream.
        // noExistingItemRecorded reaches this clamp from policy none, an already-linked task, or
        // one with a publication pending. untrackedRecorded reaches it only from an already-linked
        // task: policy none and an unrecognized policy were refused outright above (line ~180),
        // and so was a pending publication (line ~198), so by the time untracked is still true
        // here the only never-asked state left standing is ExternalReference already set.
        bool noExistingItemRecorded = needsExistingItemCheck && noExistingItemAttested;
        bool untrackedRecorded = needsExistingItemCheck && untracked;
        return new TaskPublished(
            task.Id, publishedAt, publishedByOwnerId, noExistingItemRecorded, untrackedRecorded);
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
        Guid revisedByOwnerId,
        Optional<Guid?> epicId = default)
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

        // TaskAddCommand refuses the same mismatch at adoption time — --type pr-review needs
        // --from-pr, and --from-pr implies --type pr-review — but that check runs only once,
        // there. Revise is the other door onto a task's type (Decisions Log, "the edit-after-
        // the-fact path"), so an ordinary task revised to pr-review with no pull-request
        // reference would otherwise pass here and only fail at dispatch, with a message naming
        // neither the mismatch nor the fix (RunLauncher's ExternalReference.IsBlank() guard has
        // no idea why the reference is missing).
        if (type.HasValue && type.Value == TaskType.PrReview
            && task.ExternalReference?.Provider != WorkItemProvider.GitHubPullRequest)
        {
            throw new DomainValidationException(
                "A pr-review task reviews an existing pull request, so --type pr-review needs a task "
                + "already adopted from one. Create it with h9k task add --from-pr <url> instead of "
                + $"revising task {task.Id} to pr-review — it would be left with no pull request to review.");
        }

        // The reverse mismatch: TaskAddCommand refuses --from-pr with any --type but pr-review
        // at creation ("--from-pr adopts a pull request to review, which is always a pr-review
        // task"), so revise must hold that same invariant on the way out. Without this, a task
        // adopted from a foreign pull request could be revised to an ordinary build type and
        // dispatched as ordinary work against that foreign PR's title and body, while the task
        // still carries the pull-request ExternalReference the platform recorded it under.
        if (type.HasValue && type.Value != TaskType.PrReview
            && task.ExternalReference?.Provider == WorkItemProvider.GitHubPullRequest)
        {
            throw new DomainValidationException(
                $"Task {task.Id} was adopted from a pull request with h9k task add --from-pr, which is "
                + "always a pr-review task — it cannot be revised to any other type. Abandon it and "
                + "create a new task instead if the work is not a pull-request review.");
        }

        if (!objective.HasValue && !criteria.HasValue && !agentContext.HasValue
            && !dependencies.HasValue && !type.HasValue && !chosenModel.HasValue && !epicId.HasValue)
        {
            throw new DomainValidationException(
                "A revision needs something to revise. Pass --objective, --criteria, --context, " +
                "--type, --model, --blocked-by, --clear-dependencies, --epic, or --clear-epic.");
        }

        return new TaskRevised(
            task.Id, objective, criteria, agentContext, dependencies, type, chosenModel,
            revisedAt, revisedByOwnerId, epicId);
    }

    /// <summary>
    /// Sets this task's own override of how many agent sessions its run may hold simultaneously
    /// (Decisions Log #109, Brian's ruling 2026-08-30) — deliberately state-agnostic, unlike
    /// <see cref="Revise"/>: it is meant to be set "even mid-run", including against a task whose
    /// run is Claimed and UnderReview right now, so the daemon can pick it up at the run's very
    /// next session dispatch. Lowering it never terminates a session already spawned; raising it
    /// only widens what the <em>next</em> phase may fan out to. <paramref name="sessionCap"/> is
    /// <see langword="null"/> to clear this task's own override, returning it to the node's global
    /// default — the recovery <c>TaskDetails.SessionCap</c>'s own doc already promises but that,
    /// before this, no command could actually reach (independent pre-PR review, cycle 1,
    /// adversarial lens).
    /// </summary>
    public static TaskSessionCapOverridden OverrideSessionCap(
        TaskAggregate task, int? sessionCap, DateTimeOffset overriddenAt, Guid overriddenByOwnerId)
    {
        if (sessionCap is { } value && value < 1)
        {
            throw new DomainValidationException(
                $"The session cap must be at least 1 (task {task.Id}) — a cap of zero would dispatch nothing for "
                + "this run's next session.");
        }

        return new TaskSessionCapOverridden(task.Id, sessionCap, overriddenAt, overriddenByOwnerId);
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
    /// A blocker can no longer reach true closeout. The dependent stays Blocked and surfaces as
    /// NeedsHuman with the reason: silently unblocking would dispatch work whose premise died,
    /// and silence would strand it. The <em>same</em> observation is recorded once — repeating
    /// it every sweep tells the human nothing new — but a blocker that died a different death
    /// since (a failed task the human resolved, so the remedy is no longer "retry or resolve
    /// it") is re-recorded, because a hold whose stated reason has gone stale is the same
    /// crying-wolf problem the recovery event exists to fix (Decisions Log #61).
    /// </summary>
    public static TaskDependencyFailed DependencyFailed(
        TaskAggregate task, Guid dependencyId, string reason, DateTimeOffset observedAt)
    {
        if (!AwaitsDependency(task, dependencyId))
        {
            throw new DomainConflictException(
                $"Task {task.Id} is {task.State.Value} and is not waiting on dependency {dependencyId}.");
        }

        if (reason.IsBlank())
        {
            throw new DomainValidationException("A dead dependency is recorded with what was observed about it.");
        }

        if (task.RecordedDependencyFailure(dependencyId) == reason)
        {
            throw new DomainConflictException(
                $"Task {task.Id} already records dependency {dependencyId} as dead, for that same reason.");
        }

        return new TaskDependencyFailed(task.Id, dependencyId, reason, observedAt);
    }

    /// <summary>
    /// A blocker recorded as dead was observed capable of reaching true closeout again — the
    /// human retried it, and the hold that named it is no longer true (Decisions Log #61). The
    /// dependent returns to plain Blocked; the failure record stays on the stream, because the
    /// hold happened. The caller supplies what it observed about this one blocker; what still
    /// holds the task afterwards is derived on apply, from the deaths the reader has recorded,
    /// rather than snapshotted here where a concurrent death is invisible.
    /// </summary>
    public static TaskDependencyRecovered DependencyRecovered(
        TaskAggregate task,
        Guid dependencyId,
        string observation,
        DateTimeOffset observedAt)
    {
        if (!AwaitsDependency(task, dependencyId))
        {
            throw new DomainConflictException(
                $"Task {task.Id} is {task.State.Value} and is not waiting on dependency {dependencyId}.");
        }

        if (!HasRecordedDependencyFailure(task, dependencyId))
        {
            throw new DomainConflictException(
                $"Task {task.Id} does not record dependency {dependencyId} as dead — there is no hold to lift.");
        }

        if (observation.IsBlank())
        {
            throw new DomainValidationException(
                "A recovered dependency is recorded with what was observed about it, never with a bare flag.");
        }

        return new TaskDependencyRecovered(task.Id, dependencyId, observation, observedAt);
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

    /// <summary>
    /// h9k task work's claim: the operator's mirror of <see cref="Claim"/>, same guard (Queued,
    /// and this owner's own work), same <see cref="TaskClaimed"/> event and lease-generation
    /// fencing — but NodeId is the sentinel <see cref="Guid.Empty"/> rather than a real node's
    /// id, which is what <see cref="TaskAggregate.IsInteractiveClaim"/> reads back. No
    /// <c>TaskLease</c> document is written for this claim (the CLI caller's job, not this
    /// decider's): an interactive claim is held by the human, not a process, so there is
    /// nothing here for a heartbeat to renew or an expiry sweep to reclaim.
    /// </summary>
    public static TaskClaimed ClaimInteractively(TaskAggregate task, Guid ownerId, Guid runId, DateTimeOffset claimedAt)
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
                $"not to this owner ({ownerId}) — an operator claims only their own owner's work.");
        }

        return new TaskClaimed(task.Id, Guid.Empty, ownerId, task.LeaseGeneration + 1, runId, claimedAt);
    }

    /// <summary>
    /// h9k task release: the operator gives an interactive claim back to the dispatch queue,
    /// exactly as <see cref="Requeue"/> already does for any other claimed task — refused when
    /// the current claim is a node's (running headless work), which releases through its own
    /// levers (h9k task abandon, or letting the run finish) rather than through this one.
    /// </summary>
    public static TaskRequeued ReleaseInteractiveClaim(TaskAggregate task, DateTimeOffset releasedAt)
    {
        if (task.State != TaskState.Claimed || !task.IsInteractiveClaim)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is {task.State.Value} — only a task with an active interactive claim " +
                "releases this way." + (task.State == TaskState.Claimed
                    ? " This task is claimed by a node running headless work, not an interactive session — " +
                      "let the run finish, or h9k task abandon it."
                    : string.Empty));
        }

        return Requeue(task, RequeueReason.HumanRequested, releasedAt);
    }

    /// <summary>
    /// h9k task handback: an operator working a task interactively hands it to a headless agent
    /// partway through. Refused unless the current claim is theirs to hand back for the same
    /// reason <see cref="ReleaseInteractiveClaim"/> is scoped to an interactive claim — this is
    /// not how a node's own run is redirected. <paramref name="branch"/> is the branch the
    /// operator cut (or resumed) under this claim; the next headless claim resumes it through
    /// the same RetryBranch path a human-requested retry already uses.
    /// </summary>
    public static TaskHandedBack HandBack(
        TaskAggregate task, Guid runId, string branch, string? reason, DateTimeOffset handedBackAt, Guid handedBackByOwnerId)
    {
        if (task.State != TaskState.Claimed || !task.IsInteractiveClaim)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is {task.State.Value} — only a task with an active interactive claim " +
                "hands back this way." + (task.State == TaskState.Claimed
                    ? " This task is claimed by a node running headless work already."
                    : string.Empty));
        }

        if (branch.IsBlank())
        {
            throw new DomainValidationException(
                "A handback needs the branch the interactive session worked on, so the headless agent that " +
                "continues resumes it instead of starting clean.");
        }

        return new TaskHandedBack(task.Id, runId, branch, reason, handedBackAt, handedBackByOwnerId);
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
        Guid reopenedByOwnerId,
        string? obstructionKey = null,
        string? obstructionSummary = null,
        IReadOnlyList<string>? knownHumanReviewThreadIds = null,
        IReadOnlyList<string>? knownPendingReviewRequestLogins = null)
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

        // A pr-review task's PullRequestUrl names the pull request it reviewed, not one this
        // platform ever opened or pushed to (AGENTS.md: it "never writes to the pull request or
        // the remote in any form"). Reopening it would resume a `pr/<n>` branch that never
        // existed and eventually run the remote branch-delete cleanup against that foreign
        // number once it merges — h9k pr resolve is the ordinary lever's reach, and a pr-review
        // task's only lever is a fresh h9k task add --from-pr.
        if (task.Type == TaskType.PrReview)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is a pull-request review, not work with a pull request of its own — "
                + "there is no branch to resume and nothing was ever pushed. Its PullRequestUrl names "
                + "the pull request it reviewed, not one to reopen. Start a fresh review instead with "
                + "h9k task add --from-pr.");
        }

        if (branch.IsBlank())
        {
            throw new DomainValidationException("A follow-up run needs the existing pull-request branch.");
        }

        return new TaskReopened(
            task.Id, previousRunId, branch, reason, reopenedAt, reopenedByOwnerId, kind, automatic,
            obstructionKey, obstructionSummary,
            knownHumanReviewThreadIds, knownPendingReviewRequestLogins);
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

    /// <summary>
    /// Ask for this task to be published as a card in an external system (backlog 18). The
    /// decision this makes is about the task and only about the task: whether there is work here
    /// worth a card, and whether one already exists. Everything about the card itself — its
    /// issue type, its required fields, which board it is routed to — is deliberately not
    /// modelled here, because those are the project's rules rather than the platform's, and the
    /// session this request becomes reads them from the project's own repo skills.
    /// <para>
    /// It is allowed from any live state, drafts included. A card is how a team sees that work
    /// exists, and a draft is exactly the stage where somebody wants that visible; making
    /// publication wait for Published would tie a Jira board to a readiness gate that has
    /// nothing to do with it.
    /// </para>
    /// </summary>
    public static WorkItemPublicationRequested RequestWorkItemPublication(
        TaskAggregate task,
        WorkItemProvider provider,
        JiraProjectKey projectKey,
        DateTimeOffset requestedAt,
        Guid requestedByOwnerId)
    {
        if (provider == WorkItemProvider.Unknown)
        {
            throw new DomainValidationException("Publishing a task needs a known destination (for example jira).");
        }

        // An abandoned task is one a human walked away from; filing a card for it would put work
        // on somebody's board that nobody here intends to do.
        if (task.State == TaskState.Abandoned)
        {
            throw new DomainConflictException(
                $"Task {task.Id} was abandoned, so there is no work to put on a board. "
                + "Write a new task if the work came back.");
        }

        if (task.ExternalReference is { } existing)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is already linked to {existing}. One task carries one external item "
                + "(PLAN.md §3.1a), so publishing again would create a second card for the same work. "
                + $"See it with h9k task show {task.Id}.");
        }

        if (task.PendingPublicationProvider is { } pending)
        {
            throw new DomainConflictException(
                $"Task {task.Id} already has a {pending.Value} publication outstanding"
                + (task.PublicationSessionDispatched ? " and its session is running" : " and is waiting for the daemon")
                + ". Two sessions would create two cards; wait for it to finish, or watch it with "
                + $"h9k task show {task.Id}.");
        }

        return new WorkItemPublicationRequested(task.Id, provider, projectKey, requestedAt, requestedByOwnerId);
    }

    /// <summary>
    /// Whether the task already carries exactly this reference. Asked before
    /// <see cref="LinkWorkItem"/> so that repeating the link is quiet rather than an error: the
    /// caller most likely to repeat it is an agent that could not tell whether its first attempt
    /// landed, and answering "you already told me that, and it is what I have" is the answer that
    /// lets it move on. A <em>different</em> reference is a real conflict and is refused below.
    /// </summary>
    public static bool AlreadyLinkedTo(TaskAggregate task, ExternalReference reference) =>
        task.ExternalReference is { } existing && existing == reference;

    /// <summary>
    /// The intent behind one Jira write (Brian's design, 2026-08-28): whether this task is in a
    /// position to have hall9k execute it, never whether the payload itself is a good idea — that
    /// judgment belongs to whoever composed it, and <see cref="JiraWritePayload.Validate"/> is
    /// where the executor's own guardrails (no transition, no close) are enforced regardless.
    /// <para>
    /// One write outstanding per task at a time, the same one-card-per-task discipline
    /// <see cref="RequestWorkItemPublication"/> already keeps for the agent-mediated create: a
    /// second request while the first is still pending could race twg against itself, and the
    /// retry path this design calls for exists precisely so a stuck write is resumed rather than
    /// duplicated by a second one.
    /// </para>
    /// </summary>
    public static JiraWriteRequested RequestJiraWrite(
        TaskAggregate task,
        JiraWriteOperation operation,
        string? issueKey,
        string payloadJson,
        Guid writeId,
        DateTimeOffset requestedAt,
        Guid requestedByOwnerId)
    {
        if (operation == JiraWriteOperation.Unknown)
        {
            throw new DomainValidationException(
                "A Jira write needs a known operation: create, update, or comment.");
        }

        // An abandoned task is one a human walked away from; filing or updating a real card for
        // it would put work nobody intends to do on a team's board, and — for a create — leave it
        // permanently unlinkable, since LinkWorkItem refuses an abandoned task too (mirrors the
        // guard RequestWorkItemPublication already applies for the same reason; independent
        // pre-PR review, cycle 5).
        if (task.State == TaskState.Abandoned)
        {
            throw new DomainConflictException(
                $"Task {task.Id} was abandoned, so there is no work to write to Jira. "
                + "Write a new task if the work came back.");
        }

        if (task.PendingJiraWriteId is { } outstanding)
        {
            throw new DomainConflictException(
                $"Task {task.Id} already has a Jira write outstanding ({outstanding}). Two writes in "
                + "flight could race twg against itself; wait for it to resolve, or check "
                + $"h9k task show {task.Id}.");
        }

        if (operation == JiraWriteOperation.Create)
        {
            // The decider's cheap half of the dedup gate (backlog: mirroring the GitHub read-back
            // gate): a task already carrying an item refuses here before twg is ever asked. The
            // executor's own physical dedup — searching for a task marker before it calls
            // twg jira workitem create — is what catches the harder case, a crash between twg
            // creating the card and this event ever landing.
            if (task.ExternalReference is { } existing)
            {
                throw new DomainConflictException(
                    $"Task {task.Id} is already linked to {existing}. One task carries one external "
                    + "item; creating another would file a second card for the same work.");
            }

            return new JiraWriteRequested(task.Id, writeId, operation, null, payloadJson, requestedByOwnerId, requestedAt);
        }

        // Resolved once, here, rather than left for the executor to re-derive later: the event is
        // the complete record of what was requested, and a task that gets relinked to a different
        // item between this request and its retry must not silently change which item a pending
        // write targets.
        string? targetKey = issueKey.IsNotBlank()
            ? issueKey
            : task.ExternalReference?.Provider == WorkItemProvider.Jira
                ? task.ExternalReference.Reference
                : null;
        if (targetKey.IsBlank())
        {
            throw new DomainValidationException(
                $"Task {task.Id} carries no linked Jira item to {operation.Value.ToLowerInvariant()}. "
                + $"Link one first (h9k task link-jira {task.Id} <key>), or create one with --op create.");
        }

        return new JiraWriteRequested(task.Id, writeId, operation, targetKey, payloadJson, requestedByOwnerId, requestedAt);
    }

    public static JiraWriteSucceeded RecordJiraWriteSuccess(
        TaskAggregate task, Guid writeId, string issueKey, string summary, DateTimeOffset succeededAt)
    {
        if (task.PendingJiraWriteId != writeId)
        {
            throw new DomainConflictException(
                $"Task {task.Id} has no outstanding Jira write {writeId} to record an outcome for.");
        }

        return new JiraWriteSucceeded(task.Id, writeId, issueKey, summary, succeededAt);
    }

    /// <summary>
    /// Closeout could not submit its merge notice because another Jira write was already
    /// outstanding on this task, so the notice is queued instead of lost (Brian's design,
    /// 2026-08-28). Refused when one is already queued: closeout's merge notice runs exactly once
    /// per task (the closeout step that calls this is itself one-shot), so a second queue attempt
    /// would only mean something else appended this event out of turn.
    /// </summary>
    public static JiraMergeNoticeQueued QueueJiraMergeNotice(TaskAggregate task, DateTimeOffset queuedAt)
    {
        if (task.HasQueuedJiraMergeNotice)
        {
            throw new DomainConflictException($"Task {task.Id} already has a merge notice queued.");
        }

        return new JiraMergeNoticeQueued(task.Id, queuedAt);
    }

    /// <summary>
    /// Marks a queued merge notice attempted, clearing the marker regardless of what the attempt
    /// itself came to — that outcome lands on the ordinary Jira write event trail exactly like any
    /// other write's does.
    /// </summary>
    public static JiraMergeNoticeAttempted RecordJiraMergeNoticeAttempted(TaskAggregate task, DateTimeOffset attemptedAt)
    {
        if (!task.HasQueuedJiraMergeNotice)
        {
            throw new DomainConflictException($"Task {task.Id} has no queued merge notice to attempt.");
        }

        return new JiraMergeNoticeAttempted(task.Id, attemptedAt);
    }

    /// <summary>
    /// A write attempt that did not land. <paramref name="isAuthFailure"/> is what keeps it
    /// pending rather than ending it (see <see cref="JiraWriteFailed"/>'s own doc comment) — an
    /// expired or missing twg login is an expected, handled state, and the identical payload
    /// succeeds on a later attempt once <c>twg login</c> runs, so nothing here forgets it.
    /// </summary>
    public static JiraWriteFailed RecordJiraWriteFailure(
        TaskAggregate task, Guid writeId, string reason, bool isAuthFailure, DateTimeOffset failedAt)
    {
        if (task.PendingJiraWriteId != writeId)
        {
            throw new DomainConflictException(
                $"Task {task.Id} has no outstanding Jira write {writeId} to record an outcome for.");
        }

        if (reason.IsBlank())
        {
            throw new DomainValidationException("A failed Jira write is recorded with what was observed about it.");
        }

        return new JiraWriteFailed(task.Id, writeId, reason, isAuthFailure, failedAt);
    }

    /// <summary>
    /// Record the external item this task is linked to, from what the platform observed rather
    /// than from what anybody claimed (backlog 18). The caller reads the item through the
    /// registered connection first and passes what came back; this decides only whether the task
    /// is in a position to accept it.
    /// </summary>
    public static WorkItemLinked LinkWorkItem(
        TaskAggregate task,
        ExternalReference reference,
        string observedTitle,
        string observedStatus,
        DateTimeOffset observedAt,
        DateTimeOffset linkedAt,
        Guid linkedByOwnerId)
    {
        if (reference.Provider == WorkItemProvider.Unknown || reference.Reference.IsBlank())
        {
            throw new DomainValidationException(
                "A link needs a provider and a reference (for example jira:PROJ-123).");
        }

        if (task.State == TaskState.Abandoned)
        {
            throw new DomainConflictException(
                $"Task {task.Id} was abandoned, so linking it to {reference} would attach live work to a "
                + "task nobody is doing. Link the card to a task that is still going, or write one.");
        }

        // The already-linked case is a conflict rather than an overwrite, and the reference the
        // task carries is quoted so the human (or agent) can see which of the two is wrong. The
        // identical-reference case never reaches here: AlreadyLinkedTo answers it first.
        if (task.ExternalReference is { } existing)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is already linked to {existing}, and a task carries one external item "
                + $"(PLAN.md §3.1a). {reference} is a different item: if it is the right one, the link on "
                + "record is wrong and that is worth a human looking at, because two cards for one task "
                + "means one of them is now a duplicate somebody has to close.");
        }

        return new WorkItemLinked(
            task.Id, reference, observedTitle, observedStatus, observedAt, linkedAt, linkedByOwnerId);
    }
}

using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Run;
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
        Guid? epicId = null,
        string? reviewStageComposition = null,
        bool reviewStageCompositionAcknowledged = false,
        Guid? stackedOnTaskId = null,
        int? stackedOnPullRequestNumber = null,
        PreApprovalMode? preApproval = null,
        TaskOrigin? origin = null)
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
        string? normalizedComposition = VetReviewStageComposition(
            reviewStageComposition, reviewStageCompositionAcknowledged, "--review-stage-composition");
        RefuseCompositionOnPrReview(type, normalizedComposition);
        StackedEdgeDeclaration stackedOn = VetStackedEdge(
            id, stackedOnTaskId, stackedOnPullRequestNumber, dependencies, type);
        // Same closed vocabulary Publish and SetPreApproved are held to, refused the same way: a
        // creation is the third door onto the same fact, and an unrecognized mode read as off here
        // would create the task without the pre-approval whoever asked for it believes it has.
        PreApprovalMode preApprovalGranted = VetPreApprovalMode(preApproval, id);

        return new TaskAdded(
            id, projectId, objective, criteria, type, agentContext, constraints,
            externalReference, addedAt, addedByOwnerId, VetModel(model), dependencies,
            StartsAsDraft: true, SourceIdeaId: sourceIdeaId, EpicId: epicId,
            ReviewStageComposition: normalizedComposition is { } normalizedWord
                ? Run.ReviewStageComposition.FromInput(normalizedWord)
                : null,
            ReviewStageCompositionAcknowledged: ReviewStageCompositionValidation.AcknowledgmentActuallyNeeded(
                normalizedComposition, reviewStageCompositionAcknowledged),
            StackedOnTaskId: stackedOn.TaskId,
            PreApproved: preApprovalGranted.LegacyPreApproved,
            Origin: origin,
            PreApproval: preApprovalGranted,
            StackedOnPullRequestNumber: stackedOn.PullRequestNumber);
    }

    /// <summary>
    /// A vetted stacked edge in whichever of its two forms was declared — at most one of them is
    /// ever set (task: a stacked child can stand on a pull request another install owns).
    /// </summary>
    /// <param name="TaskId">The local blocker this task is stacked on, or null.</param>
    /// <param name="PullRequestNumber">The pull request on GitHub this task is stacked on, or null.</param>
    public sealed record StackedEdgeDeclaration(Guid? TaskId, int? PullRequestNumber)
    {
        /// <summary>No stacked edge at all — every task's default.</summary>
        public static readonly StackedEdgeDeclaration None = new(null, null);
    }

    /// <summary>
    /// Vets a declared stacked edge in either form (task: a stacked pull-request edge exists as an
    /// explicit opt-in dependency; task: a stacked child can stand on a pull request another install
    /// owns). Both null passes through untouched — no edge is the default, and this method never
    /// invents one from <paramref name="dependencies"/>, because inferring a stacked edge from an
    /// ordinary blocked-by is precisely what Brian's cohesion ruling (2026-08-28) forbids.
    /// <para>
    /// The two forms are alternatives, never companions: a child stands on exactly one parent, and
    /// two declarations would name two bases for one branch. The remote form deliberately carries
    /// NO blocked-by requirement — there is no local task to name, which is the whole point of it
    /// (Brian's ruling, 2026-09-07: the edge is declared by pull request number, and the child never
    /// needs the parent adopted locally) — so the dependency-membership refusal below is the local
    /// form's alone. A pull request number must be positive, and a zero is refused with the
    /// negatives rather than normalized to "no edge at all": either names no pull request, and a
    /// declaration this method quietly dropped would dispatch the child onto the project's base
    /// with nobody told it had stopped standing on anything (Copilot review, pull request #277).
    /// </para>
    /// <para>
    /// Four refusals, each naming the fix. A self-edge is a stack of one.
    /// <paramref name="dependencies"/> must already contain the parent: a stacked edge is
    /// <em>also</em> a dependency edge, which is what lets the publish-time cycle walk and the
    /// unmet-set bookkeeping see it without either knowing stacking exists — the CLI merges the
    /// option into the blocked-by set for the caller, so reaching this refusal means the two were
    /// declared apart and disagree. And a pr-review task is refused as the child, since it has no
    /// branch or pull request of its own to stack (the same reason
    /// <see cref="Reopen"/> refuses the type): whether the <em>parent</em> is a pr-review task, and
    /// whether it even belongs to this task's own project, both need the dependency graph, so those
    /// halves are checked at <see cref="Publish"/>, where the graph is already loaded.
    /// </para>
    /// <para>
    /// Public for the same reason <see cref="VetModel"/> is: <c>h9k task add</c> prompts a human for
    /// an objective and acceptance criteria between reading its options and reaching this decider,
    /// and refusing an unusable edge only at the end would throw away what they typed in between.
    /// </para>
    /// </summary>
    public static StackedEdgeDeclaration VetStackedEdge(
        Guid id,
        Guid? stackedOnTaskId,
        int? stackedOnPullRequestNumber,
        IReadOnlyList<Guid> dependencies,
        TaskType type)
    {
        Guid? localParent = stackedOnTaskId is { } declared && declared != Guid.Empty ? declared : null;
        // Taken as passed, unlike the local form's Guid.Empty: null is int?'s own "nothing was
        // declared", so any value present here is a declaration, and a zero is one that names no
        // pull request — refused below with the negatives instead of read as no edge.
        int? remoteParent = stackedOnPullRequestNumber;

        if (localParent is null && remoteParent is null)
        {
            return StackedEdgeDeclaration.None;
        }

        if (localParent is not null && remoteParent is not null)
        {
            throw new DomainValidationException(
                "A task stands on one parent, and --stacked-on and --stacked-on-pull-request name two: the "
                + "first a task in this install's own records, the second a pull request on GitHub. Pass one. "
                + "Use --stacked-on-pull-request when the parent's run lives on somebody else's node, which is "
                + "the case this install can never see reach Delivered locally.");
        }

        if (type == TaskType.PrReview)
        {
            throw new DomainValidationException(
                "A pr-review task reviews someone else's pull request and never opens one of its own, so "
                + "there is no branch here to stack on anything. Drop --stacked-on / "
                + "--stacked-on-pull-request; declare a plain --blocked-by if the review really must wait "
                + "for that task.");
        }

        if (remoteParent is { } parentNumber)
        {
            return parentNumber > 0
                ? new StackedEdgeDeclaration(null, parentNumber)
                : throw new DomainValidationException(
                    $"#{parentNumber} is not a pull request number. Pass the parent's own number on this "
                    + "project's repository, as GitHub shows it (--stacked-on-pull-request 264).");
        }

        Guid parentId = localParent!.Value;
        if (parentId == id)
        {
            throw new DomainValidationException(
                "A task cannot be stacked on itself — a stack of one is not a stack.");
        }

        if (!dependencies.Contains(parentId))
        {
            throw new DomainValidationException(
                $"A stacked edge is also a dependency edge, and {parentId} is not among this task's "
                + "blocked-by set — so the publish-time cycle check and the unmet-dependency "
                + "bookkeeping would never see it. Declare it as both: pass --stacked-on "
                + $"{parentId} together with --blocked-by {parentId}.");
        }

        return new StackedEdgeDeclaration(parentId, null);
    }

    /// <summary>
    /// Vets a task-level review stage composition input the same way <see cref="VetModel"/> vets a
    /// model (task: the review pipeline's stage composition becomes configuration recorded per
    /// run): a blank value states no preference and returns null unchanged; anything else must
    /// parse to one of the five recognized compositions, and a composition that removes a
    /// load-bearing guarantee is refused unless <paramref name="acknowledged"/> says so. Public for
    /// the same reason <see cref="VetModel"/> is: <c>h9k task add</c> prompts for the objective and
    /// acceptance criteria between reading its options and reaching this decider.
    /// </summary>
    public static string? VetReviewStageComposition(string? input, bool acknowledged, string optionName) =>
        ReviewStageCompositionValidation.VetInput(input, acknowledged, optionName);

    /// <summary>
    /// A pr-review task's own pipeline is architecturally fixed, not merely defaulted
    /// (<c>Hall9k.Daemon.Review.PrReviewEngine</c>'s own class doc): its primary session already IS
    /// the adversarial lens, dispatched unconditionally by <c>RunLauncher</c> before
    /// <c>PrReviewEngine.ReviewAsync</c> is ever entered, and that engine's own
    /// <c>DispatchConformanceAsync</c> dispatches the conformance lens second, also
    /// unconditionally. There is no point left in that pipeline where an
    /// <see cref="Run.ReviewStageComposition.OpeningLenses"/>-driven choice could actually take
    /// effect — recording one anyway would let <c>h9k task show</c>'s Stages column state a
    /// pipeline shape the run never honors (independent pre-PR review, cycle 1, adversarial lens:
    /// <c>none --accept-reduced-review</c> recorded and attested on a pr-review task while both
    /// lenses still dispatched). Refused here, in the one decider both <see cref="Add"/> and
    /// <see cref="Revise"/> reach, rather than threading composition awareness into
    /// <c>PrReviewEngine</c> itself.
    /// <para>
    /// Public for the same reason <see cref="VetModel"/> and <see cref="VetReviewStageComposition"/>
    /// are: <c>h9k task add</c> knows the task's type before it prompts for acceptance criteria and
    /// adopts the pull request, so it vets this mismatch there too, rather than paying for both and
    /// discarding them when this decider throws (independent pre-PR review, cycle 1, conformance
    /// lens). Takes no task id: <c>Add</c>'s own call and <c>h9k task add</c>'s early duplicate of
    /// it both fire before any task exists to name, since the throw is exactly what stops that task
    /// from ever being created — an id here would be an unobserved fact, not the one <see
    /// cref="Revise"/> supplies for an already-persisted task (independent pre-PR review, cycle 1,
    /// conformance lens).
    /// </para>
    /// </summary>
    public static void RefuseCompositionOnPrReview(TaskType type, string? normalizedComposition)
    {
        if (normalizedComposition is null || type != TaskType.PrReview)
        {
            return;
        }

        throw new DomainValidationException(
            "This task is a pr-review task — its pipeline is fixed (the primary session already is the "
            + "adversarial lens, and the conformance lens always dispatches after it), so "
            + "--review-stage-composition has no pipeline shape left here to change. Leave it unset.");
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
        bool untracked = false,
        PreApprovalMode? preApproval = null,
        Optional<string?> closeLinkedIssue = default)
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

        // The two halves of the stacked-edge contract only the graph can answer (task: a stacked
        // pull-request edge exists as an explicit opt-in dependency) — what the PARENT is, and
        // where it lives. TaskDecider.Add and Revise already refused a pr-review CHILD from the
        // task's own fields; both questions here need the dependency it names to be loaded, and
        // this is the first gate that has it.
        // A pr-review parent never opens a pull request of its own (AGENTS.md: it "never writes to
        // the pull request or the remote in any form"), so there would be no branch to cut the
        // child from and no pull request to target — the child would sit Blocked until the review
        // task's own Done, then cut from a branch that does not exist.
        if (task.StackedOnTaskId is { } stackedOnId
            && graph.Node(stackedOnId) is { } parent)
        {
            if (parent.Type == TaskType.PrReview)
            {
                throw new DomainBusinessRuleException(
                    $"Task {task.Id} is stacked on {parent.Describe()}, which is a pull-request review — it "
                    + "never opens a pull request or pushes a branch of its own, so there is nothing there to "
                    + $"stack on. Drop the stacked edge with h9k task revise {task.Id} --clear-stacked-on "
                    + "(the blocked-by dependency itself is untouched).");
            }

            // The other half only the graph can answer (independent pre-PR review, cycle 1,
            // adversarial lens). A stacked child's worktree is cut from the parent's branch in the
            // CHILD's own repository, and a branch that exists only in another project's repository
            // is not there to cut from — so a cross-project edge vetted clean, published, and then
            // failed the task at dispatch with a raw could-not-resolve-start-point git error that
            // never named the edge, against this platform's own rule that a refusal quotes the rule
            // the caller broke. Refused here rather than at dispatch, and only for the stacked
            // edge: a plain cross-project blocked-by never reads the blocker's branch and stays
            // harmless. A parent whose id resolves to no known task at all is already refused by
            // the missing-dependency check above, so reaching here means the parent is real. An
            // OBSERVED mismatch only: a snapshot that recorded no project at all reads as unknown
            // rather than as a different one (TaskDependency.ProjectId's own doc).
            if (parent.ProjectId != Guid.Empty && parent.ProjectId != task.ProjectId)
            {
                throw new DomainBusinessRuleException(
                    $"Task {task.Id} is stacked on {parent.Describe()}, which belongs to a different project — "
                    + "a stacked child is cut from its parent's branch in its own project's repository, and "
                    + "that branch does not exist there. Drop the stacked edge with "
                    + $"h9k task revise {task.Id} --clear-stacked-on (the blocked-by dependency itself is "
                    + "untouched, and it stays harmless across projects: nothing ever reads a plain blocker's "
                    + "branch).");
            }
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

        // An unrecognized mode is refused rather than read as off: the publisher typed something,
        // and silently publishing without the pre-approval they asked for would be the worst of
        // the three answers. Null is not that — it is nothing passed at all, and pre-approval
        // already standing on the task is carried forward through it rather than overwritten, so a
        // publish that says nothing about pre-approval can only leave what was granted alone.
        // Without that, a task created pre-approved — which is how an adopted task carries the
        // adopting install's own answer across from Draft (task: a published task's GitHub issue
        // carries the whole task record) — would silently lose the grant to any publish that did
        // not repeat the flag. An explicit mode still wins, off included: the three-valued
        // vocabulary can say "off" out loud, which the boolean this replaced could not tell apart
        // from saying nothing, and honouring it here beats making `--pre-approved off` a silent
        // no-op. On a task nobody granted anything to, task.PreApproval is Off and this is exactly
        // the ordinary default publish.
        PreApprovalMode preApprovalRecorded =
            preApproval is null ? task.PreApproval : VetPreApprovalMode(preApproval, task.Id);
        Optional<CloseLinkedIssueRule?> closeLinkedIssueForEvent = closeLinkedIssue.HasValue
            ? Optional<CloseLinkedIssueRule?>.Of(CloseLinkedIssueRule.ParseOverride(closeLinkedIssue.Value))
            : Optional<CloseLinkedIssueRule?>.None;
        return new TaskPublished(
            task.Id, publishedAt, publishedByOwnerId, noExistingItemRecorded, untrackedRecorded,
            preApprovalRecorded.LegacyPreApproved, preApprovalRecorded, closeLinkedIssueForEvent);
    }

    /// <summary>
    /// Maps a caller's pre-approval input onto the closed three-valued vocabulary, refusing
    /// anything outside it with the vocabulary quoted (task: the people a pull request is waiting
    /// on are named, and pre-approval gains a mode that waits for human review). Null means
    /// nothing was passed, which is <see cref="PreApprovalMode.Off"/> — the default publish.
    /// Shared by <see cref="Publish"/> and <see cref="SetPreApproved"/> so the two doors onto the
    /// same fact cannot drift apart on what they accept.
    /// </summary>
    public static PreApprovalMode VetPreApprovalMode(PreApprovalMode? mode, Guid taskId)
    {
        if (mode is null)
        {
            return PreApprovalMode.Off;
        }

        PreApprovalMode chosen = PreApprovalMode.FromInput(mode);
        return chosen != PreApprovalMode.Unknown
            ? chosen
            : throw new DomainValidationException(
                $"'{mode.Value}' is not a pre-approval mode (task {taskId}) — pass off, on, or "
                + "after-human-review. off leaves you a synchronous gate at the pull request; on merges "
                + "as soon as GitHub's own gates read satisfied; after-human-review merges only once a "
                + "human reviewer has been requested on the pull request and every requested reviewer "
                + "has approved the current head.");
    }

    /// <summary>
    /// Flips a task's standing pre-approval after publish (task: a task can be published
    /// pre-approved) — deliberately settable "without the edit dance"
    /// (unassign/draft/revise/publish) the acceptance criteria call for, the same reasoning
    /// <see cref="OverrideSessionCap"/> and <see cref="OverrideReviewCaps"/> already give their own
    /// settable-anytime facts. Unlike those two, though, this has two guards they do not carry.
    /// A task whose pull request has actually merged, or an Abandoned one, refuses: there is no
    /// future pull request left for the flag to govern once the story has truly ended, so setting
    /// it there would record a fact nothing will ever read. <see cref="TaskState.Done"/> alone is
    /// NOT that terminal state — it is also the state a task carries for the entire window its
    /// pull request is open and <c>CloseoutEngine</c> is watching it (rendered as Delivered), which
    /// is exactly the window this flag governs, so <paramref name="taskClosedOut"/> is how the
    /// caller tells this decider whether Done means "still live" or "merge observed" — a
    /// distinction the aggregate alone cannot answer, since closeout is recorded on the run, not
    /// the task (independent pre-PR review, cycle 1, both lenses: the previous guard refused Done
    /// unconditionally and made the flag unreachable for the one window it matters). A Draft, by
    /// contrast, is accepted — and used to be the third refusal, which is history worth keeping
    /// because the reason it is gone is a change in <see cref="Publish"/> rather than a relaxed
    /// rule here. Publish once recorded <see cref="TaskPublished.PreApproval"/> from its own
    /// argument alone, so a value set here on a still-Draft task was silently clobbered back to
    /// off by an ordinary <c>h9k task publish</c> that forgot to repeat <c>--pre-approved</c>.
    /// Publish now carries a standing grant forward when it is passed no mode at all, so the value
    /// survives the publish that follows and the refusal has nothing left to protect (Decisions
    /// Log #151). The case that needed it gone is a task adopted from another install's record —
    /// it arrives as a Draft, and the adopting install's own answer to pre-approval has to be
    /// settable there.
    /// <para>
    /// Three-valued since the mode that waits for human review landed (task: the people a pull
    /// request is waiting on are named, and pre-approval gains a mode that waits for human review),
    /// and this is also the emergency door: flipping <see cref="PreApprovalMode.AfterHumanReview"/>
    /// to <see cref="PreApprovalMode.On"/> lets the next closeout sweep merge on GitHub's own gates
    /// alone, without waiting for a reviewer who is not coming.
    /// </para>
    /// </summary>
    /// <param name="taskClosedOut">
    /// Whether this task's pull request has actually merged (closeout observed the merge), the
    /// same fact <see cref="Hall9k.Domain.Features.Tasks.Queries.TaskDependencyQuery"/> computes
    /// as "task is Done and its current run reached RunState.Completed". The caller sources this
    /// from the task's current <c>RunDetails</c>, since the task aggregate itself never records
    /// closeout; a caller with no run to check (task never reached Done) passes false.
    /// </param>
    public static TaskPreApprovedSet SetPreApproved(
        TaskAggregate task, PreApprovalMode preApproval, DateTimeOffset setAt, Guid setByOwnerId, bool taskClosedOut)
    {
        if (task.State == TaskState.Abandoned)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is {task.State.Value} — there is no future pull request left for "
                + "pre-approval to govern, so it cannot be set on a task that has already ended.");
        }

        if (taskClosedOut)
        {
            throw new DomainConflictException(
                $"Task {task.Id}'s pull request has already merged — closeout observed the merge, so "
                + "there is no future pull request left for pre-approval to govern.");
        }

        // A draft is fair game now. It was refused until pre-approval could survive the publish that
        // followed it — the mode was recorded unconditionally on TaskPublished, so a value set on a
        // Draft was silently clobbered back to off by any publish that forgot to repeat
        // --pre-approved. Publish carries a standing grant forward (see Publish's own comment), so
        // the reason for the refusal is gone, and the case that needed it gone is a task adopted
        // from another install's record: it arrives as a Draft, and the adopting install's own
        // answer to pre-approval has to be settable there.
        PreApprovalMode chosen = VetPreApprovalMode(preApproval, task.Id);
        return new TaskPreApprovedSet(task.Id, chosen.LegacyPreApproved, setAt, setByOwnerId, chosen);
    }

    /// <summary>
    /// Revision is Draft-only (Decisions Log #34), because every later state carries a promise
    /// editing would break: Published promises a human may assign it at any moment and that it
    /// satisfies the readiness contract; assigned promises a node may read it at any moment,
    /// and revising a claimable task races the dispatcher. The revert ceremony
    /// (unassign -> draft -> revise -> publish -> assign) is deliberate, not accidental friction.
    /// Absent fields are left alone; only what is passed is recorded.
    /// <para>
    /// <paramref name="queuePriority"/> is the one exception to the Draft-only gate (task
    /// 45136b29, idea fcaded0b's R7 ruling): it is a scheduling fact, not part of the readiness
    /// contract the gate otherwise protects, so a call that touches only this field is let
    /// through regardless of state — settable on a Queued, Blocked, a currently Claimed task
    /// (for its next turn in the queue), or even a Done one (for the follow-up run a later
    /// Reopen might dispatch) — refused only on Abandoned, the one state nothing ever requeues
    /// from. A call that names it alongside anything else still needs the full ceremony; nothing
    /// here lets a wider revision hitch a ride on the exception.
    /// </para>
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
        Optional<Guid?> epicId = default,
        Optional<bool> queuePriority = default,
        Optional<string?> reviewStageComposition = default,
        bool reviewStageCompositionAcknowledged = false,
        bool clearInteractiveMode = false,
        Optional<Guid?> stackedOnTaskId = default,
        Optional<int?> stackedOnPullRequestNumber = default,
        Optional<string?> closeLinkedIssue = default)
    {
        // Both markers are scheduling/mode facts, not part of the readiness contract, so they
        // are the two exceptions Revise's own Draft-only gate carves out (task 45136b29 for
        // queue-first; the interactive-mode gap independent pre-PR review, cycle 1, both lenses,
        // found in h9k task start/handback/release: once a run leaves Dispatched/Running — a
        // headless follow-up CloseoutEngine dispatches under a real node claim while the flag is
        // still on, or the task has already reached Done with its pull request open — neither
        // ordinary exit door has an active interactive claim left to hand back or release, and
        // nothing else can turn the flag off).
        bool onlyMarkerFieldsChanging = (queuePriority.HasValue || clearInteractiveMode)
            && !objective.HasValue && !acceptanceCriteria.HasValue && !agentContext.HasValue
            && !blockedBy.HasValue && !type.HasValue && !model.HasValue && !epicId.HasValue
            && !reviewStageComposition.HasValue && !stackedOnTaskId.HasValue
            && !stackedOnPullRequestNumber.HasValue && !closeLinkedIssue.HasValue;

        if (task.State != TaskState.Draft && !onlyMarkerFieldsChanging)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is {task.State.Value} — only a draft can be revised. " + task.State switch
                {
                    var state when state == TaskState.Published =>
                        $"Return it to Draft first: h9k task draft {task.Id}.",
                    var state when state.IsAssigned =>
                        $"Unassign it, then return it to Draft: h9k task unassign {task.Id} && h9k task draft {task.Id}.",
                    _ => "A task that has already run gets a new task, not a rewritten contract. "
                        + "h9k task revise --queue-first and --clear-interactive-mode are the two "
                        + "exceptions, and each still needs something left to change.",
                });
        }

        // Done is not the dead end IsTerminal groups it with here: Reopen (below) runs from
        // Done exclusively, so a marker set while this task was still Claimed — the one case
        // Revise itself lets through — must stay settable and clearable on it, for the follow-up
        // run reopening might dispatch. Only Abandoned is a genuine dead end nothing ever
        // requeues from (independent pre-PR review, cycle 1, conformance lens).
        if (onlyMarkerFieldsChanging && task.State == TaskState.Abandoned)
        {
            // Named per marker actually requested (independent pre-PR review round 2, PR #224):
            // --queue-first/--clear-queue-first and --clear-interactive-mode are independent
            // marker-only revisions and both can be passed in the same call, so the refusal names
            // whichever combination is actually in play rather than assuming queuePriority is the
            // only one present whenever it is set at all.
            string doNothingDescription = (queuePriority.HasValue, clearInteractiveMode) switch
            {
                (true, true) => "a priority marker and the interactive-mode flag would both do nothing.",
                (true, false) => "a priority marker would do nothing.",
                _ => "the interactive-mode flag would do nothing.",
            };
            throw new DomainConflictException(
                $"Task {task.Id} is {task.State.Value} — nothing here will ever run again, so "
                + doNothingDescription);
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

        Optional<string?> normalizedComposition = reviewStageComposition.HasValue
            ? Optional<string?>.Of(ReviewStageCompositionValidation.VetInput(
                reviewStageComposition.Value, reviewStageCompositionAcknowledged, "--review-stage-composition"))
            : Optional<string?>.None;
        // The effective type after this revision, not task.Type alone: a task revised to
        // pr-review and given a composition override in the same call must be refused too, not
        // only one already pr-review before the revise (RefuseCompositionOnPrReview's own doc).
        // type.Value ?? task.Type, not a HasValue ternary: Optional<TaskType>.None leaves its
        // backing field at TaskType's own default (null, a reference type), so the two read
        // identically and this avoids a nullable-to-non-nullable assignment warning.
        TaskType effectiveType = type.Value ?? task.Type;
        RefuseCompositionOnPrReview(effectiveType, normalizedComposition.Value);

        // Vetted against the sets this revision LEAVES BEHIND, not the ones it arrived with: a
        // revision that rewrites the dependency set and declares a stacked edge in one call must
        // check the edge against the new set, and a revision that only rewrites the dependency set
        // must not silently strand an edge the task already carries outside it (the invariant
        // TaskAggregate.StackedOnTaskId promises). The same effectiveType the composition check
        // above uses, for the same reason — a task revised to pr-review in this call cannot keep an
        // edge that type can never honor.
        RevisedStackedEdge stackedOn = VetRevisedStackedEdge(
            task, stackedOnTaskId, stackedOnPullRequestNumber, dependencies, effectiveType);

        Optional<CloseLinkedIssueRule?> closeLinkedIssueForEvent = closeLinkedIssue.HasValue
            ? Optional<CloseLinkedIssueRule?>.Of(CloseLinkedIssueRule.ParseOverride(closeLinkedIssue.Value))
            : Optional<CloseLinkedIssueRule?>.None;

        if (!objective.HasValue && !criteria.HasValue && !agentContext.HasValue
            && !dependencies.HasValue && !type.HasValue && !chosenModel.HasValue && !epicId.HasValue
            && !queuePriority.HasValue && !normalizedComposition.HasValue && !clearInteractiveMode
            && !stackedOn.TaskId.HasValue && !stackedOn.PullRequestNumber.HasValue
            && !closeLinkedIssueForEvent.HasValue)
        {
            throw new DomainValidationException(
                "A revision needs something to revise. Pass --objective, --criteria, --context, " +
                "--type, --model, --blocked-by, --clear-dependencies, --epic, --clear-epic, " +
                "--stacked-on, --stacked-on-pull-request, --clear-stacked-on, --queue-first, " +
                "--clear-queue-first, --review-stage-composition, --clear-interactive-mode, or " +
                "--close-linked-issue.");
        }

        Optional<Run.ReviewStageComposition?> compositionForEvent = normalizedComposition.HasValue
            ? Optional<Run.ReviewStageComposition?>.Of(normalizedComposition.Value is { } normalizedWord
                ? Run.ReviewStageComposition.FromInput(normalizedWord)
                : null)
            : Optional<Run.ReviewStageComposition?>.None;

        return new TaskRevised(
            task.Id, objective, criteria, agentContext, dependencies, type, chosenModel,
            revisedAt, revisedByOwnerId, epicId, queuePriority, compositionForEvent,
            ReviewStageCompositionValidation.AcknowledgmentActuallyNeeded(
                normalizedComposition.Value, reviewStageCompositionAcknowledged),
            clearInteractiveMode,
            stackedOn.TaskId,
            stackedOn.PullRequestNumber,
            closeLinkedIssueForEvent);
    }

    /// <summary>What a revision records about the stacked edge, in both its forms.</summary>
    private sealed record RevisedStackedEdge(Optional<Guid?> TaskId, Optional<int?> PullRequestNumber);

    /// <summary>
    /// <see cref="VetStackedEdge"/>'s revise-side twin, which has one thing Add does not: an edge
    /// the task <em>already</em> carries, in either of its two forms. Three cases, and the third is
    /// why this exists at all.
    /// <list type="bullet">
    /// <item>Present with a value: vetted exactly as Add vets a fresh declaration.</item>
    /// <item>Present with null: that form of the edge is dropped, and nothing else moves — a local
    /// edge's blocked-by dependency stays declared, because those were two separate
    /// declarations.</item>
    /// <item>Absent, on a task that already carries an edge: the edge is re-vetted against this
    /// revision's own outcome anyway, and records nothing — a <c>--blocked-by</c> that no longer
    /// names the parent would otherwise leave the aggregate holding a local edge outside its own
    /// dependency set, invisible to the publish-time cycle walk and to the unmet-set bookkeeping,
    /// which is the exact invariant that edge is required to satisfy. Refusing beats silently
    /// dropping: the human declared the stack, so the platform says which two declarations now
    /// disagree rather than picking one for them.</item>
    /// </list>
    /// <para>
    /// Everything is decided against the edge this revision LEAVES the task holding rather than the
    /// one it arrived with, which is what lets a declaration in one form displace the other:
    /// repointing a stack at a new parent has always simply replaced the old one, and that stays
    /// true when the new parent is a pull request and the old one was a task.
    /// </para>
    /// </summary>
    private static RevisedStackedEdge VetRevisedStackedEdge(
        TaskAggregate task,
        Optional<Guid?> stackedOnTaskId,
        Optional<int?> stackedOnPullRequestNumber,
        Optional<IReadOnlyList<Guid>> dependencies,
        TaskType effectiveType)
    {
        // The dependency set this revision leaves behind — the revised one when it rewrote it, the
        // task's existing one otherwise.
        IReadOnlyList<Guid> effectiveDependencies = dependencies.HasValue
            ? dependencies.Value ?? []
            : task.BlockedBy;

        // Declaring a parent replaces whatever this task stood on, across forms as well as within
        // one: repointing a local edge at another task has always simply replaced it, and a human
        // repointing it at a pull request means the same thing, so the displaced form is dropped
        // rather than made into a second declaration to refuse. Naming BOTH forms in one call is
        // the genuinely ambiguous case, and it survives into the vet below to be refused there.
        bool declaresLocal = stackedOnTaskId is { HasValue: true, Value: not null };
        bool declaresRemote = stackedOnPullRequestNumber is { HasValue: true, Value: not null };

        // Whichever form this revision leaves the task holding — the declared one where it declared
        // one, nothing where the other form displaced it, and the task's existing one where the
        // revision left that form alone.
        Guid? effectiveTaskId = stackedOnTaskId.HasValue ? stackedOnTaskId.Value
            : declaresRemote ? null
            : task.StackedOnTaskId;
        int? effectiveNumber = stackedOnPullRequestNumber.HasValue ? stackedOnPullRequestNumber.Value
            : declaresLocal ? null
            : task.StackedOnPullRequestNumber;

        // The local form carries an invariant the remote form has none of: the parent must still be
        // among the blocked-by set this revision leaves behind. Answered HERE, ahead of the shared
        // vet below, for the one case the vet's own wording gets wrong — an edge this revision
        // never touched, stranded by a --blocked-by rewrite. The vet speaks to somebody declaring a
        // stacked edge and tells them to pass --blocked-by alongside it; the person here declared
        // nothing and needs to hear which two of their existing declarations now disagree.
        // Refusing beats dropping either way: the human declared the stack, so the platform says
        // what conflicts rather than picking one for them.
        if (!stackedOnTaskId.HasValue && effectiveTaskId is { } strandedParent
            && !effectiveDependencies.Contains(strandedParent))
        {
            throw new DomainBusinessRuleException(
                $"Task {task.Id} is stacked on {strandedParent}, and this revision's dependency set no longer "
                + "names it — a stacked edge is also a dependency edge, so the two would disagree. Keep "
                + $"the dependency (include --blocked-by {strandedParent}), or drop the stack in the same "
                + "revision with --clear-stacked-on.");
        }

        // The declared edge, vetted exactly as Add vets a fresh one — including the two-parents,
        // self-edge, dependency-membership and pr-review refusals, which apply identically here.
        StackedEdgeDeclaration vetted = VetStackedEdge(
            task.Id, effectiveTaskId, effectiveNumber, effectiveDependencies, effectiveType);

        // Absent and still valid records nothing. The aggregate already holds this edge, so
        // re-stating it would both write a field the revision never touched — the exact claim
        // Optional exists to avoid making — and count as "something to revise", so a revise call
        // that passed no options at all would silently succeed on any stacked task instead of
        // being refused (the guard below this method's call site). What IS recorded is the vetted
        // value rather than the raw one, so a Guid.Empty arrives on the stream as the "no edge"
        // null the vet normalized it to (a zero pull request number never arrives at all — the vet
        // refuses it) — and a form the OTHER form displaced is recorded
        // as cleared, because that displacement is a real change to what this task stands on and a
        // stream that left it out would replay into a child holding two parents.
        return new RevisedStackedEdge(
            stackedOnTaskId.HasValue ? Optional<Guid?>.Of(vetted.TaskId)
                : declaresRemote && task.StackedOnTaskId is not null ? Optional<Guid?>.Of(null)
                : Optional<Guid?>.None,
            stackedOnPullRequestNumber.HasValue ? Optional<int?>.Of(vetted.PullRequestNumber)
                : declaresLocal && task.StackedOnPullRequestNumber is not null ? Optional<int?>.Of(null)
                : Optional<int?>.None);
    }

    /// <summary>
    /// Sets this task's own override of how many agent sessions its run may hold simultaneously
    /// (Decisions Log #111, Brian's ruling 2026-08-30) — deliberately state-agnostic, unlike
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
        if (sessionCap is { } value && !IsUsableSessionCap(value))
        {
            throw new DomainValidationException(
                $"The session cap must be at least 1 (task {task.Id}) — a cap of zero would dispatch nothing for "
                + "this run's next session.");
        }

        return new TaskSessionCapOverridden(task.Id, sessionCap, overriddenAt, overriddenByOwnerId);
    }

    /// <summary>
    /// Sets this task's own override of one or more of its four review-cycle caps (task: the
    /// review cycle caps become settable at three levels) — deliberately state-agnostic, unlike
    /// <see cref="Revise"/>: it is meant to be set "at any time", including against a task whose
    /// run is live right now, so the daemon can pick it up at the very next cap check. Each of the
    /// four caps is independent: a call naming only one leaves the other three exactly as they
    /// were (absent means "leave alone"), and present-with-null clears that one back to the
    /// project or node level.
    /// </summary>
    public static TaskReviewCapsOverridden OverrideReviewCaps(
        TaskAggregate task,
        Optional<int?> maxComplianceReviewCycles,
        Optional<int?> maxAdversarialReviewCycles,
        Optional<int?> maxFinalFullPassRounds,
        Optional<int?> lifetimeReviewCycleBudget,
        DateTimeOffset overriddenAt,
        Guid overriddenByOwnerId)
    {
        if (!maxComplianceReviewCycles.HasValue && !maxAdversarialReviewCycles.HasValue
            && !maxFinalFullPassRounds.HasValue && !lifetimeReviewCycleBudget.HasValue)
        {
            throw new DomainValidationException(
                "Nothing to change — pass at least one of --max-compliance-review-cycles, "
                + "--max-adversarial-review-cycles, --max-final-full-pass-rounds, or "
                + "--lifetime-review-cycle-budget.");
        }

        ReviewCapValidation.RefuseNegativeCap(maxComplianceReviewCycles, "--max-compliance-review-cycles");
        ReviewCapValidation.RefuseNegativeCap(maxAdversarialReviewCycles, "--max-adversarial-review-cycles");
        ReviewCapValidation.RefuseNegativeCap(maxFinalFullPassRounds, "--max-final-full-pass-rounds");
        ReviewCapValidation.RefuseNonPositiveCap(lifetimeReviewCycleBudget, "--lifetime-review-cycle-budget");

        return new TaskReviewCapsOverridden(
            task.Id, maxComplianceReviewCycles, maxAdversarialReviewCycles, maxFinalFullPassRounds,
            lifetimeReviewCycleBudget, overriddenAt, overriddenByOwnerId);
    }

    /// <summary>
    /// The three cap floors above, asked as questions rather than enforced as refusals — the same
    /// floors, from the same place, so the two readings can never disagree.
    /// <para>
    /// Public for a reason the throwing setters cannot serve: a cap can arrive from OUTSIDE this
    /// install, in the task record on a published issue that <c>h9k task add --from-issue</c>
    /// adopts, and a value that install's own build never validated (a hand-written block) must
    /// degrade to "no override" with the adoption saying so — never wall the whole adoption with a
    /// message quoting a flag the operator never passed, which is exactly what feeding it straight
    /// into <see cref="OverrideSessionCap"/> and <see cref="OverrideReviewCaps"/> used to do
    /// (independent pre-PR review, cycle 1, both lenses; the same failure class
    /// <c>TaskRecordAdoption.VetType</c> already answers for the type field).
    /// </para>
    /// </summary>
    public static bool IsUsableSessionCap(int cap) => ReviewCapValidation.IsPositive(cap);

    /// <inheritdoc cref="IsUsableSessionCap"/>
    public static bool IsUsablePerRunReviewCap(int cap) => ReviewCapValidation.IsAtOrAboveZero(cap);

    /// <inheritdoc cref="IsUsableSessionCap"/>
    public static bool IsUsableLifetimeReviewCycleBudget(int cap) => ReviewCapValidation.IsPositive(cap);

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

        // Per edge, not per dependency: the one this task declared itself stacked on is met at the
        // parent's Delivered, everything else at true closeout (StackedEdgeRules — the same rule
        // TaskDependencyResolver re-applies every sweep, which is why it lives in one place).
        return new TaskAssigned(
            task.Id,
            assignedOwnerId,
            [.. dependencies
                .Where(dependency => StackedEdgeRules.Blocks(task, dependency))
                .Select(dependency => dependency.Id)],
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

    /// <summary>
    /// What actually holds a Blocked task, as a noun phrase after "Blocked on". Two things can,
    /// and a claim refusal that names the wrong one leaves an agent unable to self-correct from the
    /// message (the CLI standard in AGENTS.md): a stacked child whose parent is a pull request on
    /// GitHub has no unmet dependency at all — its hold is what the closeout watcher's sweep last
    /// saw about that pull request (task: a stacked child can stand on a pull request another
    /// install owns).
    /// </summary>
    private static string DescribeBlockedHold(TaskAggregate task) =>
        (task.UnmetDependencies.Count, task.AwaitsRemoteStackedParent) switch
        {
            (0, true) => $"pull request #{task.StackedOnPullRequestNumber}, which it is stacked on and which "
                + $"was last observed {task.RemoteStackedParentState.Describe()}",
            (> 0, true) => "unmet dependencies and the pull request it is stacked on",
            _ => "unmet dependencies",
        };

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

    public static TaskRequeued Requeue(
        TaskAggregate task, RequeueReason reason, DateTimeOffset requeuedAt, bool clearInteractiveMode = false)
    {
        if (task.State != TaskState.Claimed && task.State != TaskState.NeedsHuman)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is {task.State.Value} — only claimed or needs-human tasks requeue.");
        }

        return new TaskRequeued(task.Id, reason, requeuedAt, clearInteractiveMode);
    }

    /// <summary>
    /// h9k task work's claim: the operator's mirror of <see cref="Claim"/>, same
    /// <see cref="TaskClaimed"/> event and lease-generation fencing — but NodeId is the sentinel
    /// <see cref="Guid.Empty"/> rather than a real node's id, which is what
    /// <see cref="TaskAggregate.IsInteractiveClaim"/> reads back. No <c>TaskLease</c> document is
    /// written for this claim (the CLI caller's job, not this decider's): an interactive claim is
    /// held by the human, not a process, so there is nothing here for a heartbeat to renew or an
    /// expiry sweep to reclaim.
    /// <para>
    /// A Blocked task claims here too (task 0ac72cb8-h9k, "claiming or starting a task
    /// interactively across dependency edges warns and asks instead of refuses"), the same
    /// warn-then-override shape <see cref="ClaimDeliberately"/> already has, and only when
    /// <paramref name="dependencyOverrideAcknowledged"/> is true — the human was warned about the
    /// open dependency edges (by the caller, which has the full <see cref="TaskDependency"/>
    /// descriptions this decider never sees) and either confirmed it themselves this time or the
    /// task already carries a covering acknowledgment from an earlier claim
    /// (<paramref name="dependencyOverrideCarriedForward"/>; <see cref="TaskAggregate.UnmetDependenciesAlreadyAcknowledged"/>).
    /// A Blocked task with no acknowledgment still refuses — this is the defensive floor beneath
    /// the caller's own warn-and-ask flow, not a substitute for it.
    /// </para>
    /// </summary>
    public static TaskClaimed ClaimInteractively(
        TaskAggregate task, Guid ownerId, Guid runId, DateTimeOffset claimedAt,
        bool dependencyOverrideAcknowledged = false, bool dependencyOverrideCarriedForward = false)
    {
        if (task.State == TaskState.Blocked)
        {
            if (!dependencyOverrideAcknowledged)
            {
                throw new DomainConflictException(
                    $"Task {task.Id} is Blocked on {DescribeBlockedHold(task)} — h9k task work {task.Id} " +
                    "--acknowledge-unmet-dependencies to claim it anyway, once you have confirmed that is what you want.");
            }
        }
        else if (task.State != TaskState.Queued)
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

        // Always true, never a parameter: every claim this method produces IS the human's own
        // hands-on-the-wheel act by construction — there is no automated caller of
        // ClaimInteractively the way AutoPrReviewEngine calls ClaimDeliberately below (task:
        // interactive mode becomes a recorded property of the task, design ruling R2).
        return new TaskClaimed(
            task.Id, Guid.Empty, ownerId, task.LeaseGeneration + 1, runId, claimedAt,
            dependencyOverrideAcknowledged, dependencyOverrideAcknowledged && dependencyOverrideCarriedForward,
            InteractiveMode: true);
    }

    /// <summary>
    /// h9k task start's claim (task 8a56af78-h9k, "a deliberate human kick-off dispatches a task
    /// on the spot"): a second sibling of <see cref="ClaimInteractively"/>, same sentinel
    /// <see cref="Guid.Empty"/> <c>NodeId</c> and the identical reasoning — a deliberate human act
    /// is outside the automation's budget, so it is ceiling-exempt exactly as an operator's own
    /// attended claim already is (Decisions Log #103) — but this one launches headless rather than
    /// attached to a terminal, so it is kept as its own decider method rather than folded into
    /// <see cref="ClaimInteractively"/>'s name, which specifically means "the operator's own
    /// attached session."
    /// <para>
    /// Shares <see cref="ClaimInteractively"/>'s own Blocked-entry shape: a Blocked task claims
    /// here too, but only when <paramref name="dependencyOverrideAcknowledged"/> is true — the
    /// human was warned about the open dependency edges (by the caller, which has the full
    /// <see cref="TaskDependency"/> descriptions this decider never sees) and either confirmed it
    /// themselves this time or the task already carries a covering acknowledgment from an earlier
    /// claim (<paramref name="dependencyOverrideCarriedForward"/>;
    /// <see cref="TaskAggregate.UnmetDependenciesAlreadyAcknowledged"/>) — task 0ac72cb8-h9k closed
    /// the gap task 8a56af78-h9k deliberately left open ("there is no re-entry branch the way
    /// h9k task work has one"): that reasoning is about re-entering an already-live claim, which
    /// this method still never does, not about honoring a recorded acknowledgment on a fresh claim,
    /// which is all a Blocked entry here ever is. A Blocked task with no acknowledgment still
    /// refuses — this is the defensive floor beneath the caller's own warn-and-ask flow, not a
    /// substitute for it, since this decider has no dependency descriptions to warn with itself.
    /// </para>
    /// <para>
    /// Unlike <see cref="ClaimInteractively"/>, this method has two callers with opposite
    /// answers to "is this the human's own act" (task: interactive mode becomes a recorded
    /// property of the task, design ruling R2): <c>h9k task start</c> passes
    /// <paramref name="interactiveMode"/> true — a human deliberately took the wheel — while
    /// <c>AutoPrReviewEngine</c>'s own automated "now"-speed claim on this identical method
    /// leaves it at its default false, since nobody asked to arbitrate that dispatch's
    /// boundaries by hand.
    /// </para>
    /// </summary>
    public static TaskClaimed ClaimDeliberately(
        TaskAggregate task, Guid ownerId, Guid runId, DateTimeOffset claimedAt, bool dependencyOverrideAcknowledged,
        bool dependencyOverrideCarriedForward = false, bool interactiveMode = false)
    {
        if (task.State == TaskState.Blocked)
        {
            if (!dependencyOverrideAcknowledged)
            {
                throw new DomainConflictException(
                    $"Task {task.Id} is Blocked on {DescribeBlockedHold(task)} — h9k task start {task.Id} " +
                    "--acknowledge-unmet-dependencies to start it anyway, once you have confirmed that is what you want.");
            }
        }
        else if (task.State != TaskState.Queued)
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

        return new TaskClaimed(
            task.Id, Guid.Empty, ownerId, task.LeaseGeneration + 1, runId, claimedAt, dependencyOverrideAcknowledged,
            dependencyOverrideAcknowledged && dependencyOverrideCarriedForward, interactiveMode);
    }

    /// <summary>
    /// h9k task release: the operator gives an interactive claim back to the dispatch queue,
    /// exactly as <see cref="Requeue"/> already does for any other claimed task — refused when
    /// the current claim is a node's (running headless work), which releases through its own
    /// levers (h9k task abandon, or letting the run finish) rather than through this one.
    /// <para>
    /// A release is itself the human's explicit act of returning the task to the machine, so by
    /// default it clears <see cref="TaskAggregate.InteractiveModeEnabled"/> exactly as
    /// <see cref="HandBack"/> does — headless dispatch must not gate phase boundaries for a human
    /// who walked away (design ruling R6, amended 2026-09-05). <paramref name="keepInteractive"/>
    /// is the stated exception: the operator who wants a headless run that still parks at each
    /// boundary asks for it explicitly with <c>--keep-interactive</c>.
    /// </para>
    /// </summary>
    public static TaskRequeued ReleaseInteractiveClaim(
        TaskAggregate task, DateTimeOffset releasedAt, bool keepInteractive = false)
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

        return Requeue(task, RequeueReason.HumanRequested, releasedAt, clearInteractiveMode: !keepInteractive);
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
        // Blocked is admitted alongside Claimed for CloseoutEngine's own one case: a task
        // Apply(TaskReopened) landed Blocked behind a still-open dependency, whose watched run
        // then merged anyway — the follow-up that reopen was meant to dispatch is now moot, and
        // finalizing straight to Done here is what stops a later TaskDependencyCompleted from
        // re-queuing a task whose work already shipped (independent pre-PR review, cycle 3,
        // adversarial lens, on h9k task start).
        // AwaitingAuthor and NeedsHuman are admitted for the pr-review follow-through's own two
        // endings (task: a pr-review task stays open while the pull request's review threads are
        // unresolved): every thread the reviewer opened is resolved, or the pull request merged
        // or closed. The follow-through poll is what observes either, and the task it observes it
        // for is sitting in one of those two states with no lease and no live run — AwaitingAuthor
        // while the author has said nothing, NeedsHuman once they have — so the Claimed-only rule
        // above would leave the one state this feature invented with no way to reach Done at all.
        // Narrowed to a task with the follow-through actually open, so an ordinary NeedsHuman task
        // (an agent's unanswered question, a findings park nobody has walked) keeps the refusal it
        // has always had.
        bool prReviewFollowThrough = task.PrReviewFollowThroughOpen
            && (task.State == TaskState.AwaitingAuthor || task.State == TaskState.NeedsHuman);
        if (task.State != TaskState.Claimed && task.State != TaskState.Blocked && !prReviewFollowThrough)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is {task.State.Value} — only a claimed task, a Blocked one whose " +
                "watched pull request merged anyway, or a pr-review task whose posted review has been " +
                "followed through, completes.");
        }

        return new TaskCompleted(task.Id, runId, pullRequestUrl, completedAt);
    }

    /// <summary>
    /// The posted review is not the ending: park the pr-review task on the pull request and watch
    /// it for the author's answer (task: a pr-review task stays open while the pull request's
    /// review threads are unresolved). What <c>PrReviewEngine.FinalizeAsync</c> appends in place
    /// of <see cref="Complete"/> once a verdict has been delivered on the run.
    /// <para>
    /// A pull request URL is required and a non-pr-review task is refused, because both are what
    /// make the wait meaningful: there is no follow-through without something to poll, and no
    /// other task type has somebody else's review threads to wait on. A pr-review task whose own
    /// reference cannot be read is the one case the caller must handle instead of routing here —
    /// it completes exactly as it always did, because there is genuinely nothing to watch.
    /// </para>
    /// </summary>
    public static PullRequestReviewFollowThroughOpened OpenPrReviewFollowThrough(
        TaskAggregate task, Guid runId, string pullRequestUrl, string? headSha, DateTimeOffset openedAt)
    {
        if (task.Type != TaskType.PrReview)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is a {task.Type.Value} task — following through on a posted review is a "
                + "pr-review task's own ending, and this one has no review of somebody else's pull request "
                + "to be waiting on.");
        }

        if (task.State != TaskState.Claimed)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is {task.State.Value} — only the claim the review was delivered under "
                + "parks on the pull request to wait for its author.");
        }

        if (pullRequestUrl.IsBlank())
        {
            throw new DomainValidationException(
                $"Task {task.Id} has no pull request to wait on, so there is nothing to follow through. "
                + "Complete it instead — a review with no readable pull-request reference has nothing "
                + "left to watch (AGENTS.md: never guess at unobserved facts).");
        }

        return new PullRequestReviewFollowThroughOpened(task.Id, runId, pullRequestUrl, headSha, openedAt);
    }

    /// <summary>
    /// Whether this task is one the closeout watcher's follow-through sweep should poll: a
    /// pr-review task with a posted review still being followed through, sitting in one of the
    /// two states that wait rather than work. Asked from the domain so the sweep's own query and
    /// every guard downstream of it read the same rule.
    /// </summary>
    public static bool AwaitsPrReviewFollowThrough(TaskAggregate task) =>
        task.PrReviewFollowThroughOpen
        && task.Type == TaskType.PrReview
        && (task.State == TaskState.AwaitingAuthor || task.State == TaskState.NeedsHuman);

    /// <summary>
    /// One poll's reading of the watched pull request, recorded as the watermark the next poll
    /// compares against (task: a pr-review task stays open while the pull request's review
    /// threads are unresolved). Refuses a task that is not actually following anything through,
    /// so a stale sweep landing after the task closed out writes nothing.
    /// </summary>
    public static PullRequestReviewFollowThroughObserved ObservePrReviewFollowThrough(
        TaskAggregate task,
        string reviewerLogin,
        IReadOnlyList<PrReviewThreadWatermark> threads,
        bool reReviewRequested,
        string? headSha,
        int? commitCount,
        DateTimeOffset observedAt)
    {
        RefuseUnlessFollowingThrough(task, "record an observation of");
        if (reviewerLogin.IsBlank())
        {
            throw new DomainValidationException(
                $"Task {task.Id}'s follow-through observation names no reviewer login, so it cannot say "
                + "whose review threads it counted. An unreadable login is a failed poll to retry, never "
                + "an observation to record (AGENTS.md: never guess at unobserved facts).");
        }

        return new PullRequestReviewFollowThroughObserved(
            task.Id, reviewerLogin, threads, reReviewRequested, headSha, commitCount, observedAt);
    }

    /// <summary>
    /// The pull request answered: replies in the reviewer's own threads that they did not write
    /// themselves, new commits, a re-review newly requested of them, or any combination — so the
    /// task surfaces as needs-you with a line naming what changed, and only what was observed.
    /// Always appended after the
    /// <see cref="ObservePrReviewFollowThrough"/> that re-baselines the watermark, in the same
    /// transaction, which is what stops the same replies notifying twice.
    /// <para>
    /// A re-review request stands beside the other two rather than merely holding the wait open,
    /// because it is the one thing here that is an explicit ask of the reviewer: an author who
    /// resolves the threads themselves and re-requests review, with no comment and no push, is
    /// asking them back, and leaving the task Waiting there had the author waiting on the reviewer
    /// while the reviewer's board said the opposite (independent pre-PR review, cycle 1,
    /// adversarial lens). Only the transition wakes them — the observation beside this event
    /// records the standing request, so the same ask cannot fire on the next poll.
    /// </para>
    /// </summary>
    public static PullRequestReviewAuthorResponded RecordPrReviewAuthorResponse(
        TaskAggregate task,
        string summary,
        int replyCount,
        int threadsWithReplies,
        int? newCommitCount,
        bool headMoved,
        bool reReviewNewlyRequested,
        string? interactiveSessionAddress,
        DateTimeOffset observedAt)
    {
        RefuseUnlessFollowingThrough(task, "record an author response on");
        if (replyCount <= 0 && !headMoved && !reReviewNewlyRequested)
        {
            throw new DomainValidationException(
                $"Task {task.Id} saw no reply, no moved head and no new re-review request, so there is no "
                + "author response to record. A poll that found nothing new records its observation and says "
                + "nothing else — waking the reviewer for silence is exactly the noise this watch exists to "
                + "avoid.");
        }

        if (summary.IsBlank())
        {
            throw new DomainValidationException(
                $"Task {task.Id}'s author response needs the line every surface shows. Without it the "
                + "board would say needs-you and be unable to say why.");
        }

        return new PullRequestReviewAuthorResponded(
            task.Id, summary, replyCount, threadsWithReplies, newCommitCount, headMoved,
            reReviewNewlyRequested, interactiveSessionAddress, observedAt);
    }

    /// <summary>
    /// The scoped lap's own claim (<c>h9k pr review --since-my-review</c>): a third sibling of
    /// <see cref="ClaimInteractively"/> and <see cref="ClaimDeliberately"/>, same sentinel
    /// <see cref="Guid.Empty"/> node id and the same reasoning — a human's deliberate act is
    /// outside the automation's budget (Decisions Log #103) — entered from the one pair of states
    /// neither of those admits.
    /// <para>
    /// Its own method rather than a widening of <see cref="ClaimInteractively"/>'s state check,
    /// because that check is load-bearing for every other caller: "Queued or an acknowledged
    /// Blocked" is what stops an operator claiming a task the dispatcher already owns, and
    /// admitting two more states there would relax that guard for the whole surface to serve one
    /// command. Here the equivalent guard is <see cref="AwaitsPrReviewFollowThrough"/>, which is
    /// strictly narrower: a pr-review task with a posted review and no live run of any kind.
    /// </para>
    /// </summary>
    public static TaskClaimed ClaimForScopedReviewLap(
        TaskAggregate task, Guid ownerId, Guid runId, DateTimeOffset claimedAt)
    {
        if (!AwaitsPrReviewFollowThrough(task))
        {
            throw new DomainConflictException(
                $"Task {task.Id} is a {task.Type.Value} task in {task.State.Value} with no posted review being "
                + "followed through, so there is nothing since your last review to read. A scoped lap reads the "
                + "thread replies and pushes that arrived after a review THIS platform recorded you posting; "
                + "h9k pr review on that pull request, without --since-my-review, opens an ordinary lap "
                + "instead.");
        }

        if (task.AssignedOwnerId != ownerId)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is assigned to "
                + $"{(task.AssignedOwnerId is { } assignee ? assignee.ToString() : "nobody")}, not to this owner "
                + $"({ownerId}) — an operator claims only their own owner's work.");
        }

        return new TaskClaimed(
            task.Id, Guid.Empty, ownerId, task.LeaseGeneration + 1, runId, claimedAt,
            DependencyOverrideAcknowledged: false, DependencyOverrideCarriedForward: false,
            InteractiveMode: true);
    }

    private static void RefuseUnlessFollowingThrough(TaskAggregate task, string what)
    {
        if (!AwaitsPrReviewFollowThrough(task))
        {
            throw new DomainConflictException(
                $"Task {task.Id} is a {task.Type.Value} task in {task.State.Value} with no posted review "
                + $"being followed through, so there is nothing to {what} it.");
        }
    }

    /// <summary>
    /// Records a GitHub comment mentioning the install's login on this pr-review task's own pull
    /// request (idea 2f079bcd, auto-pr-review's second trigger). Attaches to the task in
    /// whatever state it is already in — a mint, a claim into a fresh follow-up run, and this
    /// observation are three separate events a caller composes together as the situation calls
    /// for, never implied by one another the way a state machine's own transition would be.
    /// </summary>
    public static PullRequestReviewMentionObserved ObservePrReviewMention(
        TaskAggregate task, string pullRequestUrl, string commentId, string commentAuthorLogin,
        string commentBody, string commentUrl, DateTimeOffset commentCreatedAt, DateTimeOffset observedAt)
    {
        if (task.Type != TaskType.PrReview)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is a {task.Type.Value} task — a GitHub mention of the install's login is "
                + "recorded only on the pr-review task reviewing the pull request it was found on.");
        }

        if (pullRequestUrl.IsBlank() || commentId.IsBlank())
        {
            throw new DomainValidationException(
                $"Task {task.Id}'s mention needs both the pull request it was found on and the comment's "
                + "own id — an unreadable mention is nothing to record (AGENTS.md: never guess at "
                + "unobserved facts).");
        }

        return new PullRequestReviewMentionObserved(
            task.Id, pullRequestUrl, commentId, commentAuthorLogin, commentBody, commentUrl, commentCreatedAt,
            observedAt);
    }

    /// <summary>
    /// The mention follow-up's own claim (auto-pr-review's second trigger, idea 2f079bcd): a
    /// fourth sibling of <see cref="ClaimInteractively"/>, <see cref="ClaimDeliberately"/> and
    /// <see cref="ClaimForScopedReviewLap"/>, entered from the identical pair of states
    /// <see cref="ClaimForScopedReviewLap"/> accepts (<see cref="AwaitsPrReviewFollowThrough"/> —
    /// the report already parked, or the task waiting on the pull request) but headless like
    /// <see cref="ClaimDeliberately"/>'s own automatic dispatch: a GitHub mention is the daemon's
    /// own go signal, not a human sitting at a terminal running <c>h9k pr review</c>, so
    /// <c>InteractiveMode</c> is false and there is no assigned-owner identity check — the caller
    /// is always the sweep's own node owner, exactly as an auto-created mint already is.
    /// </summary>
    public static TaskClaimed ClaimForMentionFollowUp(
        TaskAggregate task, Guid ownerId, Guid runId, DateTimeOffset claimedAt)
    {
        RefuseUnlessFollowingThrough(task, "dispatch a mention follow-up on");
        return new TaskClaimed(
            task.Id, Guid.Empty, ownerId, task.LeaseGeneration + 1, runId, claimedAt,
            DependencyOverrideAcknowledged: false, DependencyOverrideCarriedForward: false,
            InteractiveMode: false);
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
        IReadOnlyList<string>? knownPendingReviewRequestLogins = null,
        string? pullRequestHeadSha = null,
        string? stackReplayUpstreamCommit = null,
        string? stackReplayOntoCommit = null,
        IReadOnlyList<ChangesRequestedReview>? changesRequestedReviews = null,
        DateTimeOffset? checksPendingSince = null)
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

        // A stacked replay's whole job is one `git rebase --onto <onto> <upstream>`, and it needs
        // both arguments as observed commits. Without the upstream it cannot drop the parent's
        // commits and would replay them onto a base that already holds them; without the onto it
        // would have to name a ref instead, landing wherever that ref had drifted to by the time
        // the session ran — on the one follow-up no reviewer ever reads. Refused rather than
        // degraded into a different operation wearing this kind's name.
        if (kind == FollowUpKind.StackReplay
            && (stackReplayUpstreamCommit.IsBlank() || stackReplayOntoCommit.IsBlank()))
        {
            throw new DomainValidationException(
                $"A stacked replay of task {task.Id} needs both commits it replays between: the one its "
                + "branch was last built on (the upstream `git rebase --onto` drops the parent's commits "
                + "at) and the one it lands on. Without either there is no deterministic replay to "
                + "dispatch, only a plain rebase wearing its name.");
        }

        // A changes-requested lap's whole job is answering a named human review: without the
        // review itself the fix session has nothing to read but the reopen's one-line reason, and
        // the disagreement park it may reach has no review to name or link. Refused rather than
        // degraded into an ordinary thread lap wearing this kind's name, for the same reason a
        // replay missing either of its commits is.
        if (kind == FollowUpKind.ReviewRequestedChanges && changesRequestedReviews is not { Count: > 0 })
        {
            throw new DomainValidationException(
                $"A changes-requested fix lap for task {task.Id} needs the review it is answering — the "
                + "reviewer, the review's link, and its findings. Without them the fix session has no "
                + "findings to read and a disagreement park has no review to name, which is the whole "
                + "shape of this lap.");
        }

        return new TaskReopened(
            task.Id, previousRunId, branch, reason, reopenedAt, reopenedByOwnerId, kind, automatic,
            obstructionKey, obstructionSummary,
            knownHumanReviewThreadIds, knownPendingReviewRequestLogins, pullRequestHeadSha,
            stackReplayUpstreamCommit, stackReplayOntoCommit, changesRequestedReviews,
            checksPendingSince);
    }

    /// <summary>
    /// The honest default <c>h9k task retry</c> records when the operator gives no
    /// <c>--reason</c> of their own (PLAN.md §16 #25) — a fact about how the retry was invoked,
    /// never a human's own priority instruction. <see cref="Hall9k.Connectors.Prompts.WorkPromptBuilder.AppendOperatorGuidanceSection"/>
    /// compares against this literal so a bare retry's boilerplate is never re-presented to the
    /// dispatched session as something a human told it to prioritize (independent pre-PR review,
    /// cycle 1, conformance lens).
    /// </summary>
    public const string DefaultRetryReason = "Retry requested via h9k task retry.";

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
    /// second request while the first is still pending could race a write against itself, and the
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
                + "flight could race a write against itself; wait for it to resolve, or check "
                + $"h9k task show {task.Id}.");
        }

        if (operation == JiraWriteOperation.Create)
        {
            // The decider's cheap half of the dedup gate (backlog: mirroring the GitHub read-back
            // gate): a task already carrying an item refuses here before Jira is ever asked. The
            // executor's own physical dedup — searching for a task marker before it creates a
            // card — is what catches the harder case, a crash between Jira creating the card and
            // this event ever landing.
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
    /// pending rather than ending it (see <see cref="JiraWriteFailed"/>'s own doc comment) — a
    /// rejected credential is an expected, handled state, and the identical payload succeeds on a
    /// later attempt once the connection is fixed, so nothing here forgets it.
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

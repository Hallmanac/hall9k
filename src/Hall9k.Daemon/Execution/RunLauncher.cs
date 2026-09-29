using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Prompts;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon.Closeout;
using Hall9k.Daemon.Dispatch;
using Hall9k.Daemon.ProjectHomes;
using Hall9k.Daemon.Review;
using Hall9k.Connectors.Worktrees;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Features.Learning.Queries;
using Hall9k.Domain.Features.AutoPrReview;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.PrReviewPreflight;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Documents;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Features.Tasks.Rendering;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;
using Marten.Linq.MatchesSql;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// Turns a fresh claim into a live agent: worktree → RunDispatched → spawn →
/// RunProcessStarted → monitor. Failures at any step fail the run and task honestly.
/// A task carrying a pull-request URL is checked against the provider first: already
/// merged means close out, never redispatch.
/// </summary>
public sealed class RunLauncher(
    IDocumentStore store,
    IWorktreeManager worktrees,
    IExecutor executor,
    RunSupervisor supervisor,
    BlockerContextAssembler blockerContext,
    IPullRequestInspector inspector,
    CloseoutEngine closeout,
    PullRequestOpener pullRequests,
    ProcessRunner processRunner,
    IOptions<DaemonOptions> options,
    ILogger<RunLauncher> logger)
{
    /// <summary>
    /// The ordinary shape, for every dispatch where the calling node IS the dispatching node —
    /// every caller except auto-pr-review's "now" speed, which needs the overload below.
    /// </summary>
    public Task LaunchAsync(
        Guid taskId, Guid runId, Guid nodeId, Guid ownerId, int leaseGeneration, CancellationToken cancellationToken)
        => LaunchAsync(taskId, runId, nodeId, ownerId, leaseGeneration, dispatchingNodeId: null, cancellationToken);

    /// <summary>
    /// <paramref name="dispatchingNodeId"/> names the physical daemon actually making this call,
    /// for the one case it can differ from <paramref name="nodeId"/>: auto-pr-review's "now"
    /// speed passes the ceiling-exempt <see cref="Guid.Empty"/> sentinel as <paramref name="nodeId"/>
    /// but still needs <see cref="Hall9k.Domain.Features.Run.Events.RunDispatched.DispatchingNodeId"/>
    /// to name this node, so <see cref="RunSupervisor"/>'s sentinel-run adoption can tell this
    /// node's own sentinel runs apart from another node's sharing the same database. Null resolves
    /// to <paramref name="nodeId"/> itself, which is already the dispatching node for every ordinary
    /// (non-sentinel) dispatch — the overload above is that ordinary shape.
    /// </summary>
    public async Task LaunchAsync(
        Guid taskId, Guid runId, Guid nodeId, Guid ownerId, int leaseGeneration, Guid? dispatchingNodeId,
        CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        TaskDetails? task = await session.LoadAsync<TaskDetails>(taskId, cancellationToken);
        ProjectDetails? project = task is null
            ? null
            : await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);

        if (task is null || project is null)
        {
            logger.LogError("Cannot launch run {RunId}: task or project missing", runId);
            return;
        }

        // The generation fence, checked before any worktree checkout or spawn rather than
        // only on the already-merged path below (Copilot review, PR #30): a reclaim during
        // dispatch — a requeued lease's expiry sweep and a startup adoption both landing in
        // the same window, the origin incident GenerationFence documents — must stop this
        // stale generation here, or it checks out a worktree and spawns a second live agent
        // for a task a fresh generation already owns. refuseAbandonedTask: true closes the
        // identical gap for a task abandoned in the window between the dispatch claim
        // committing and this check (independent pre-PR review, cycle 1, conformance lens):
        // this is the one call site that actually spawns a fresh agent session, so identity
        // alone (CurrentRunId still naming this run — abandon never clears it) is not enough.
        // No run stream exists yet at this point, so a rejection here has nothing to retire —
        // unlike every other fence caller in this file's neighbours, this one simply declines
        // to ever create the run.
        if (!await GenerationFence.AllowsAsync(
            session, logger, taskId, runId, leaseGeneration, "to launch", cancellationToken,
            refuseAbandonedTask: true))
        {
            return;
        }

        try
        {
            if (task.PullRequestUrl.IsNotBlank()
                && await TryCloseOutMergedPullRequestAsync(
                    task, project, taskId, runId, nodeId, ownerId, leaseGeneration, cancellationToken))
            {
                return;
            }

            if (task.PullRequestUrl.IsBlank()
                && await TryResumeAtPullRequestOpenAsync(
                    task, taskId, runId, nodeId, ownerId, leaseGeneration, cancellationToken))
            {
                return;
            }

            // A pr-review task never has a branch or a PR of its own to resume — every
            // dispatch (including a retry) fetches the reviewed pull request fresh instead,
            // since nothing is ever committed into its read-only worktree to preserve
            // between attempts. Checked before the follow-up/retry logic below, which is
            // written entirely in terms of this task's own branch and PR.
            bool isPrReview = task.Type == TaskType.PrReview;
            PullRequestFacts? prReviewFacts = isPrReview
                ? await FetchOpenPullRequestFactsAsync(task, project, processRunner, cancellationToken)
                : null;
            if (isPrReview && prReviewFacts is null)
            {
                await RecordLaunchFailureAsync(
                    taskId, runId, leaseGeneration,
                    $"{task.ExternalReference} is no longer open — a pr-review task reviews an open pull "
                    + "request, so there is nothing to dispatch against.", cancellationToken);
                return;
            }

            // A safe verdict for a pre-flight dispatched to gate a mention follow-up's own
            // checkout (idea 2f079bcd, decision 2) releases the task through its own requeue
            // reason rather than the ordinary one (RunSupervisor.CompletePreflightAsync), so this
            // claim answers the mentioning comment instead of running the full review the rest of
            // this isPrReview branch dispatches (independent pre-PR review, cycle 1, both lenses
            // — a bare PrReviewPreflightSafe requeue here let the ordinary dispatch loop claim the
            // task straight into the full-review branch below, and the mentioning comment was
            // never answered). Delegates entirely: LaunchPrReviewMentionFollowUpAsync re-reads the
            // pull request, re-runs the identical foreign-repository and pre-flight gates the rest
            // of this branch runs below, and cuts its own checkout.
            if (isPrReview && task.PendingMentionFollowUpAfterPreflight)
            {
                PullRequestMentionComment comment = new(
                    task.LatestMentionCommentId ?? string.Empty, task.LatestMentionAuthorLogin ?? string.Empty,
                    task.LatestMentionBody ?? string.Empty, task.LatestMentionUrl ?? string.Empty,
                    task.LatestMentionCreatedAt ?? DateTimeOffset.UtcNow, task.LatestMentionCommentDatabaseId);
                Guid? priorReviewRunId = task.RunIds.Count > 0 ? task.RunIds[0] : null;
                await LaunchPrReviewMentionFollowUpAsync(
                    taskId, runId, ownerId, leaseGeneration, dispatchingNodeId ?? nodeId, comment, priorReviewRunId,
                    cancellationToken);
                return;
            }

            // The worktree checkout below fetches from the PROJECT's own origin — it has no
            // notion of any other remote — so a pull request adopted from a different
            // repository (--from-pr accepts any owner/repo#N or URL) would otherwise either
            // silently check out whatever PR shares that number in the project's own repo, or
            // fail an unhelpful "couldn't find remote ref" if none does. Refused here, before
            // any fetch or worktree is touched, rather than guessed at (AGENTS.md, "never
            // guess at unobserved facts"): best-effort against project.RepositoryUrl, falling
            // back to the same ambient `gh repo view` observation TaskPublishCommand already
            // uses for the identical "what repository is this project really" question — and
            // silent (proceeds) only when neither source can name one, exactly as that
            // existing guard does.
            if (isPrReview && prReviewFacts is not null)
            {
                Uri? projectRepositoryUrl = project.RepositoryUrl
                    ?? await new GitHubWorkItemProvider(processRunner).TryObserveRepositoryHostAsync(
                        project.RepositoryPath, cancellationToken);
                if (OwnerRepoFrom(projectRepositoryUrl) is { } projectRepository
                    && !string.Equals(projectRepository, prReviewFacts.Repository, StringComparison.OrdinalIgnoreCase))
                {
                    await RecordLaunchFailureAsync(
                        taskId, runId, leaseGeneration,
                        $"{task.ExternalReference} lives in {prReviewFacts.Repository}, not "
                        + $"{project.Name}'s own repository ({projectRepository}). A pr-review task's "
                        + "worktree is always cut from the project it was adopted against, so reviewing "
                        + $"a pull request from another repository needs a project registered against "
                        + $"{prReviewFacts.Repository} instead.", cancellationToken);
                    return;
                }
            }

            // The pull-request review pre-flight (idea 6be68ee2, finding 1, phase one): every
            // pr-review dispatch is gated here, before any worktree is cut, on a safe verdict for
            // this exact head oid. Not safe yet (no verdict at all, or one for a since-moved head)
            // dispatches a fresh pre-flight and returns; unsafe parks the task as needs-you and
            // returns; either way nothing below this point runs for this dispatch.
            if (isPrReview && prReviewFacts is not null
                && !await EnsurePrReviewPreflightSafeAsync(
                    session, task, project, prReviewFacts, dispatchingNodeId ?? nodeId, runId, leaseGeneration,
                    isMentionFollowUp: false, cancellationToken))
            {
                return;
            }

            // A reopened task carries the branch of its existing PR: the follow-up run
            // resumes that branch instead of cutting a fresh one off the base (log #20).
            (string Branch, string PullRequestUrl)? followUp =
                !isPrReview && task.FollowUpBranch.IsNotBlank() && task.PullRequestUrl.IsNotBlank()
                    ? (task.FollowUpBranch, task.PullRequestUrl)
                    : null;

            // A pr-review run never resumes a prior attempt's worktree the way
            // CheckoutFreshOrRetryAsync's retry path does (nothing is ever committed into
            // one to preserve), so a retried or reopened-elsewhere pr-review task cuts a
            // brand-new checkout every time — and no sweep ever reaches the old ones
            // afterward: a pr-review run carries no PullRequestNumber, so CloseoutEngine's
            // merge closeout never watches it, and TaskDecider.Reopen refuses the type
            // outright (adversarial review, cycle 1). Reclaiming this task's own previous
            // pr-review worktrees right before cutting the new one is the only point left
            // that can still reach them.
            if (isPrReview)
            {
                await CleanUpPreviousPrReviewWorktreesAsync(taskId, project, cancellationToken);
            }

            // The branch a fresh cut starts from, resolved once here (task: a stacked pull-request
            // edge exists as an explicit opt-in dependency). A run that RESUMES this task's
            // existing branch inherits the base the branch already sits on instead, below, once the
            // checkout has said which of the two happened — the resolver reads the PARENT's current
            // state, which is the right question only when a branch is being cut
            // (StackedBaseResolver.ResumedBaseAsync). A pr-review run is excluded: its checkout is
            // a detached, branch-less read of a foreign pull request whose own base is already
            // recorded separately as PrReviewBaseRefName.
            StackedBase stackedBase = isPrReview
                ? new StackedBase(project.BaseBranch, null, "a pr-review run has no base of its own to stack")
                : await StackedBaseResolver.ResolveAsync(session, task, project, cancellationToken);
            if (stackedBase.IsStacked)
            {
                logger.LogInformation(
                    "Task {TaskId}: run {RunId} is {Reason}", taskId, runId, stackedBase.Reason);
            }

            Worktree worktree;
            bool resumesPreviousWork;
            RunStartedCleanAfterBranchGone? startedClean;
            if (isPrReview)
            {
                try
                {
                    worktree = await worktrees.CreatePrReviewCheckoutAsync(
                        new PrReviewWorktreeRequest(
                            project.RepositoryPath, prReviewFacts!.Number, taskId, runId, prReviewFacts.HeadRefOid),
                        cancellationToken);
                }
                catch (PullRequestHeadMovedException exception)
                {
                    // The safe verdict this dispatch just confirmed was judged against a head this
                    // checkout's own fetch no longer observes (idea 6be68ee2, finding 1, phase
                    // two) — a fresh pre-flight is dispatched against the head actually observed
                    // here, exactly as a dispatch that found no verdict at all would, rather than
                    // proceeding with a checkout the verdict was never about.
                    logger.LogWarning(
                        "Task {TaskId}: {Message} — dispatching a fresh pre-flight instead of proceeding",
                        taskId, exception.Message);
                    await DispatchPrReviewPreflightAsync(
                        task, project, prReviewFacts! with { HeadRefOid = exception.ObservedHeadOid },
                        dispatchingNodeId ?? nodeId, runId, leaseGeneration, isMentionFollowUp: false,
                        cancellationToken, headOidFromGitFetch: true);
                    return;
                }

                resumesPreviousWork = false;
                startedClean = null;
            }
            else if (followUp is { } resume)
            {
                worktree = await worktrees.CheckoutExistingAsync(
                    new FollowUpWorktreeRequest(project.RepositoryPath, resume.Branch, taskId, runId),
                    cancellationToken);
                resumesPreviousWork = true;
                startedClean = null;
            }
            else
            {
                (worktree, resumesPreviousWork, startedClean) = await CheckoutFreshOrRetryAsync(
                    task, project, stackedBase.BaseBranch, taskId, runId, cancellationToken);
            }

            await RenderKnowledgeDocumentsIntoAsync(session, project, worktree.Path, runId, cancellationToken);
            // The lesson section every prompt this method composes carries (idea d805fd8b, piece
            // 5). Read once here, beside the render that writes the same store's lessons into the
            // worktree as a file, and handed to whichever prompt branch below actually runs: the
            // section is identical for a fresh build, a follow-up, a failing-checks lap and a
            // changes-requested lap, and a per-branch read would be four chances to disagree.
            InjectedLessons lessons = await LoadRecordedLessonsAsync(
                session, project, runId, cancellationToken);

            Guid sessionId = DomainId.New();
            ExecutorMode mode = ExecutorMode.Subscription;
            // Resolved once, here, and carried to both the spawn and the record: the model
            // the run is dispatched on and the model it actually runs on are the same fact
            // (Decisions Log #33), so they can never disagree. A pr-review task's primary
            // session IS a review lens (the adversarial one — PrReviewEngine dispatches the
            // conformance lens second), so it resolves the review role, never build.
            AgentRole primaryRole = isPrReview ? AgentRole.Review : AgentRole.Build;
            AgentModel model = options.Value.ResolveModel(primaryRole, task.Model, project.Model);
            // Effort is not recorded on the run the way the model is, so it is resolved here for the
            // spawn alone, over the same role.
            AgentEffort effort = options.Value.ResolveEffort(primaryRole, task.Effort, project.Effort);

            // Resolved once, here, and frozen on RunDispatched for this run's whole lifetime
            // (task: the review pipeline's stage composition becomes configuration recorded per
            // run) — unlike the review-cycle caps, which ReviewEngine re-resolves live every
            // cycle, a composition change is structural (which tracks exist, whether the
            // mandatory final pass runs) and must not reshape a run already in flight; see
            // ReviewStageComposition's own doc for why.
            //
            // A pr-review task never reads this chain (class sweep on independent pre-PR review,
            // cycle 1, adversarial lens's PrReviewEngine finding): TaskDecider.Add/Revise already
            // refuse a task-level override for this type, but a project- or node-level one is not
            // task-type-aware and would otherwise still flow through here and land on
            // RunDispatched — the same misrepresentation the refusal exists to prevent, just
            // reached through a level that refusal cannot reach. PrReviewEngine's own primary
            // session is always the adversarial lens and its DispatchConformanceAsync always
            // dispatches the conformance lens second, unconditionally, so FullPipeline (both
            // lenses) is the only value that is ever actually true of a pr-review run — recording
            // anything else would repeat the exact "h9k task show states a pipeline shape the run
            // never honors" defect for a project- or node-wide override instead of a task one.
            // A stacked replay is mechanical by construction (FollowUpKind.StackReplay's own doc):
            // git replaying commits that already passed review onto a moved base is not new
            // intent, so no reviewer reads it — the composition is forced to None here, the same
            // way a pr-review run's is forced to FullPipeline, rather than left to the task/
            // project/node chain that has no idea which follow-up kind this is. ReviewStageComposition
            // .None still runs the gates and still rebases before settling, which is exactly the
            // "runs the gates, triggers no review cycle" this follow-up owes.
            //
            // Deliberately NOT routed through ReviewStageCompositionValidation's acknowledgment
            // gate: that gate exists so a HUMAN cannot silently waive Decisions Log #92's
            // review guarantee. There is no guarantee to waive here — nothing entered this branch
            // for a reviewer to have an opinion about — so there is no consequence to attest to.
            bool isStackReplay = followUp is not null && task.FollowUpKind == FollowUpKind.StackReplay;
            ReviewStageComposition reviewStageComposition = isPrReview
                ? ReviewStageComposition.FullPipeline
                : isStackReplay
                    ? ReviewStageComposition.None
                    : ReviewStageCompositionResolver.Resolve(
                        task.ReviewStageComposition, project.ReviewStageComposition, options.Value.ReviewStageComposition,
                        task.Type);

            // Resolved once, here, exactly like the worktree above: this run's directory is
            // under the task's own directory when the project has a home (backlog 49), and
            // every consumer reads the recorded value from here on rather than rederiving it.
            //
            // The task's directory, though, is read from disk rather than freshly computed from
            // the task's live objective (adversarial review, cycle 1): the doorbell-woken render
            // sweep owns renaming that directory when a revision changes its slug, and it runs
            // on its own schedule, not synchronously with dispatch. Trusting the freshly computed
            // name here would, on a revise-then-redispatch landing ahead of that sweep, create the
            // not-yet-renamed directory itself — stranding the true, already-populated one under
            // its old name as an orphan the next reconciliation pass only marks, never merges.
            // Resolving against whatever directory already exists for this task keeps the run
            // pointed at the one directory that is actually there; the eventual sweep still
            // renames it, runs/ and all, exactly as it always has.
            //
            // A task being redispatched (a follow-up onto a reopened Done task, backlog 51) can
            // have its directory sitting under tasks/_archive/ rather than tasks/ right up until
            // the render sweep's own next pass moves it back — TaskReopened lands on the task
            // stream well ahead of any guarantee that the sweep has already caught up with it.
            // Searching the archive root too, and placing the new run directly under whichever
            // directory is actually found, keeps the run's files beside the task's real
            // task.md/workspace/ wherever they currently sit, rather than resolving a runs/
            // directory under a tasks/ path the sweep has not created yet.
            string? existingTaskDirectory = project.HomeDirectory.HasValue
                ? HomeEntryWriter.FindExistingDirectory(
                    ProjectHomePaths.TasksDirectory(project.HomeDirectory.Value), task.Id,
                    alternateRoots: [ProjectHomePaths.ArchivedTasksDirectory(project.HomeDirectory.Value)])
                : null;
            string runDirectory = existingTaskDirectory is not null
                ? RunPaths.ResolveDirectoryUnderTaskDirectory(existingTaskDirectory, runId)
                : RunPaths.ResolveDirectory(project.HomeDirectory, TaskDocumentRenderer.DirectoryName(task), runId);

            // Which personas this pull request is reviewed through (idea b9b09779, piece 1),
            // resolved before the session name and the prompt below, which both come out of it.
            // The personas are the ASSIGNEE's, not this node's owner's: a review is a job handed
            // to a member, and the lens it is read through belongs to whoever it was handed to.
            // An unassigned pr-review task falls back to the owner this run is dispatched under,
            // which on a single-owner install is the same person. An owner record that cannot be
            // read at all plans exactly as an owner who declared nothing does.
            // isForkHead (security review idea 6be68ee2, process-injection finding 1) is read off
            // this dispatch's own PullRequestFacts, fetched fresh above: a pull request's head
            // repository can never change once opened (only its base can move, on a retarget),
            // so this is the one, permanent read and never re-checked on a later dispatch.
            // Whether the Security persona is appended to this plan at all (idea 6be68ee2, phase
            // two), and whether every path this pull request changed matched this project's own
            // non-executable-path set — both resolved once, here, and folded into the plan below,
            // so the run's own recorded plan and every session's dispatch agree with what h9k
            // project show would print for this project right now.
            bool securityReviewEnabled = !isPrReview
                || (await SecurityReviewSetting.ResolveAsync(session, project.Id, cancellationToken)).IsOn;
            bool securityReviewDocsOnlySkip = isPrReview && securityReviewEnabled
                && await EveryChangedPathIsNonExecutableAsync(
                    worktree.Path, prReviewFacts!.BaseRefName.IsNotBlank() ? prReviewFacts.BaseRefName : project.BaseBranch,
                    project.EffectiveNonExecutablePaths, cancellationToken);

            ReviewPersonaPlan? personaPlan = isPrReview
                ? ReviewPersonaRegistry.Plan(
                    (await session.LoadAsync<OwnerDetails>(task.AssignedOwnerId ?? ownerId, cancellationToken))
                        ?.ReviewPersonas,
                    isForkHead: prReviewFacts!.IsCrossRepository, securityReviewEnabled: securityReviewEnabled,
                    everyChangedPathIsNonExecutable: securityReviewDocsOnlySkip)
                : null;

            // Then, for whichever of those personas can stand the product up, what this run will
            // actually do about it (idea b9b09779, piece 3): the project's own drive setting and
            // whether there is a run skill to drive with. Resolved here, once, and recorded with
            // the plan below, so every session of this review is told the same thing and the
            // report describes the review that ran rather than the settings as they stand later.
            // Re-planned rather than patched because the plan is a pure value and its own
            // filtering is what keeps a decision for a persona that did not run out of it.
            if (personaPlan is not null)
            {
                personaPlan = ReviewPersonaRegistry.Plan(
                    personaPlan.Requested,
                    await ReviewDriveResolver.ResolveAllAsync(
                        personaPlan.Ran, session, project, cancellationToken),
                    isForkHead: prReviewFacts!.IsCrossRepository, securityReviewEnabled: securityReviewEnabled,
                    everyChangedPathIsNonExecutable: securityReviewDocsOnlySkip);
            }

            // The primary session is ordinarily the engineer's adversarial lens — Security's own
            // session never leads a plan today, since ReviewPersonaRegistry.Plan always appends
            // it after whatever the assignee declared — but a future persona-ordering change must
            // not silently spawn this slot on the ordinary Review chain instead of this persona's
            // own floor (idea 6be68ee2, phase two, the courier precedent). Extracted to
            // ResolvePrimarySessionModel so this floor is unit-testable directly against a
            // synthetic Security-led plan, rather than only through a shape today's registry can
            // never actually produce (independent pre-PR review, cycle 1, conformance lens,
            // medium).
            if (isPrReview)
            {
                (model, effort) = ResolvePrimarySessionModel(
                    personaPlan!.Sessions[0].Persona, task.Model, project.Model, task.Effort, project.Effort,
                    options.Value, model, effort);
            }

            // The primary session's own name (task: every dispatched agent session launches
            // under a human-readable id-and-role name) — decided here, once, from the same
            // three-way split the prompt selection below re-derives for its own purpose, because
            // AgentRole.Build alone cannot tell a rebase follow-up, a failing-checks follow-up,
            // and an ordinary dispatch apart on later reads (RunDetails.SessionName's own doc).
            string sessionRole = isPrReview
                ? personaPlan!.Sessions[0].RoleName
                : followUp is not null
                    ? task.FollowUpKind == FollowUpKind.FailingChecks
                        ? SessionRoleName.Checks
                        : task.FollowUpKind == FollowUpKind.Rebase
                            ? SessionRoleName.Rebase
                            : task.FollowUpKind == FollowUpKind.StackReplay
                                ? SessionRoleName.StackReplay
                                : SessionRoleName.Build
                    : SessionRoleName.Build;
            string sessionName = SessionRoleName.For(DomainId.Short(taskId), sessionRole);

            // Where this branch already sits, for a run that resumed it rather than cutting it —
            // both facts read off the run before this one, because resuming moves neither of them
            // (StackedBaseResolver.ResumedBaseAsync's own doc). Keyed on resumesPreviousWork, not on
            // this being a follow-up (conformance review, cycle 4): a retry that resumes
            // task.RetryBranch resumes the branch just as a follow-up does —
            // CheckoutFreshOrRetryAsync's own retry arm calls the same CheckoutExistingAsync — and
            // that path reports no StartPointCommit at all, because a resumed checkout performs no
            // fresh cut to observe one from.
            StackedBaseResolver.ResumedBase? resumedBase = resumesPreviousWork
                ? await StackedBaseResolver.ResumedBaseAsync(session, task, project, runId, cancellationToken)
                : null;
            if (resumedBase?.Refused is { } refusedCarryForward)
            {
                logger.LogWarning("Task {TaskId}: run {RunId} — {Refused}", taskId, runId, refusedCarryForward);
            }

            // The base this run records and every prompt below names: the resolver's answer for a
            // fresh cut, the resumed branch's own recorded base otherwise (adversarial review,
            // cycle 4 — re-resolving here reads the parent's CURRENT state, so a parent that closed
            // out between the reopen and this launch would record an un-replayed stacked child as
            // unstacked and disarm its retarget, its replay and the merge-bar guard for good).
            string runBaseBranch = resumedBase?.BaseBranch ?? stackedBase.BaseBranch;

            // resumedBase.BaseBranch is already sanitized by StackedBaseResolver.ResumedBaseAsync,
            // and a fresh cut's stackedBase.BaseBranch is checked again by
            // GitWorktreeManager.CreateAsync before it becomes a git argument there — but
            // runBaseBranch also flows onward into RunDispatched.BaseBranch, into every prompt
            // this method builds below, and (for a stacked replay) into
            // StackReplayOntoResolver's own fetch, none of which sit behind either of those two
            // checks. Refused here, once, at the point this run's own combined answer exists,
            // rather than trusted because some upstream resolver already looked at one of its two
            // inputs (security review idea 6be68ee2, process-injection finding 2).
            if (!GitArgumentValidation.IsLegalBranchName(runBaseBranch, out string? runBaseBranchRefusalReason))
            {
                await RecordLaunchFailureAsync(
                    taskId, runId, leaseGeneration,
                    $"This run's own base branch '{GitArgumentValidation.Printable(runBaseBranch)}' is not a "
                    + $"legal branch name ({runBaseBranchRefusalReason}) — refusing to dispatch a session "
                    + "against it.",
                    cancellationToken);
                return;
            }

            if (resumedBase is not null && resumedBase.BaseBranch != stackedBase.BaseBranch)
            {
                logger.LogInformation(
                    "Task {TaskId}: run {RunId} resumes branch {Branch}, which sits on {RecordedBase} — carried "
                    + "forward from the previous run rather than re-resolved ({Reason})",
                    taskId, runId, worktree.Branch, resumedBase.BaseBranch, stackedBase.Reason);
            }

            // This branch's fork point, observed at the moment it was true (RunDispatched.BaseCommit's
            // own doc): the start point a fresh cut resolved, the commit a replay is dispatched to
            // land on, or whatever the run before this one recorded for a branch it resumed. Blank
            // when none of the three could be observed, which is what stops a later replay from
            // inventing a boundary. Resolved once here rather than inline on the event, because
            // every prompt this method builds below needs the identical value: a stacked session's
            // recompose, self-review range, rebase replay and fixup-fold all key off the recorded
            // commit rather than origin/<parent> (independent pre-PR review, cycles 1 and 2,
            // adversarial lens).
            //
            // task.StackReplayOntoCommit is a DISPATCH-TIME prediction, written once by whatever
            // dispatched this follow-up (CloseoutEngine or StackedParentWatch) and never touched
            // again — including across a retry. The prediction is exactly right for that first
            // dispatch, but a retry can land long after it, with the base having moved in the
            // meantime (origin incident, 2026-09-15, task 450b9d84/PR #382: the base took a real
            // Decisions Log number for another entry between the failed lap and its retry, and the
            // retry rebuilt its prompt against the stale recorded commit, landing short of it and
            // failing the same numbering guard a second time). RetryPending is this task's own
            // standing "a retry is still unconsumed" flag (TaskDetails.RetryPending's own doc) —
            // true for every dispatch from the moment a retry lands until the task ends, which is
            // deliberately broader than "only this one relaunch": it costs nothing to resolve the
            // base's own current tip fresh instead of trusting a prediction this task has already
            // shown can go stale. FollowUpKind.Rebase carries no equivalent hazard: its own onto
            // target is always `origin/<base>`, a ref BuildRebase's own prompt fetches fresh at
            // session run time, never a commit frozen at dispatch time.
            StackReplayOntoResolver.Resolution stackReplayOnto = default;
            if (isStackReplay)
            {
                // Serialized under the project's own repository lock (Decisions Log #4: "Daemon
                // serializes git ops per-repo, mutex not retry loops") only when a retry actually
                // needs the fetch below — an ordinary first dispatch returns its recorded commit
                // without touching git at all, so it has nothing to serialize. Without this lock, a
                // retried replay's fetch of origin/<base> can race a closeout sweep's or another
                // run's own fetch into the same shared repository, fail with a lock error, and fall
                // back to the stale recorded commit — exactly the incident this resolver exists to
                // close, surfaced only as a warning log (independent pre-PR review, cycle 1, both
                // lenses).
                await using IAsyncDisposable? repositoryLock = task.RetryPending
                    ? await worktrees.AcquireRepositoryLockAsync(project.RepositoryPath, cancellationToken)
                    : null;
                stackReplayOnto = await StackReplayOntoResolver.ResolveAsync(
                    processRunner, worktree.Path, runBaseBranch, task.StackReplayOntoCommit ?? string.Empty,
                    task.RetryPending, cancellationToken);
            }
            if (isStackReplay && task.RetryPending && !stackReplayOnto.ResolvedFromCurrentBaseTip)
            {
                logger.LogWarning(
                    "Task {TaskId}: run {RunId} retries a stacked replay but could not read {Base}'s current "
                    + "tip in {Worktree} — falling back to the recorded onto commit {Recorded}",
                    taskId, runId, runBaseBranch, worktree.Path, task.StackReplayOntoCommit);
            }

            string baseCommit = isStackReplay
                ? stackReplayOnto.Commit
                : resumedBase?.ForkPointCommit ?? worktree.StartPointCommit;

            // Of those three, exactly one can name a commit this branch never landed on, and it is
            // the inherited one: a StackReplay's own record is a dispatch-time PREDICTION — the
            // commit the replay was TOLD to land on, written above before the session has rebased
            // anything — and its prompt sanctions `git rebase --abort` on a conflict it cannot
            // honestly resolve, which leaves the branch where it was with the prediction recorded
            // as though it were the fork point (adversarial review, cycle 6). Every later consumer
            // of the record then asserts it as observed fact: a Rebase follow-up is told
            // `git rebase --onto origin/<parent> <that commit>`, whose replay range still holds the
            // parent's own commits, and a review lap scopes `git diff <that commit>...HEAD`, whose
            // merge base collapses to the project's base and folds the parent's delta into what a
            // reviewer grades as this branch's work. So the inherited value is checked against the
            // branch before this run re-asserts it, the same containment check — and the same
            // three-way reading of --is-ancestor — StackedParentWatch already applies to it.
            //
            // A fresh cut's start point needs no check (the cut just observed it) and a replay's
            // own prediction must not have one (the branch is not meant to contain it yet — that
            // is what the replay is for). Only a stacked run pays for the call at all, because
            // blank-versus-recorded changes nothing an unstacked run reads (RunDetails.StackedForkPoint).
            if (!isStackReplay
                && resumedBase is not null
                && baseCommit.IsNotBlank()
                && runBaseBranch != project.BaseBranch
                && await BranchContainsCommitAsync(
                    worktree.Path, worktree.Branch, baseCommit, cancellationToken) == false)
            {
                // Blank, not a substitute: nothing else on record names where this branch actually
                // forked from, and the prompts already have an honest path for an unobserved
                // boundary — a stacked rebase says so and disputes rather than replaying from a
                // guess (AgentPromptBuilder.AppendStackedRebaseRules' own null arm).
                logger.LogWarning(
                    "Task {TaskId}: run {RunId} resumes branch {Branch}, which does NOT contain the fork point "
                    + "{ForkPoint} the previous run recorded — recording no fork point rather than a commit this "
                    + "branch never landed on (ordinarily a stacked replay that aborted its rebase)",
                    taskId, runId, worktree.Branch, baseCommit);
                baseCommit = string.Empty;
            }

            // The follow-up's own opening Discovery cycle scope seed (task: a lap reviews only what
            // it changed): task.FollowUpPullRequestHeadSha already carries the kind-aware gate —
            // CloseoutEngine only ever records one for an automatic ReviewFeedback or FailingChecks
            // reopen, leaving it null for a Rebase reopen, a manual h9k pr resolve, and a fresh
            // dispatch alike — so nothing here needs to re-check FollowUpKind.
            session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, taskId, nodeId, ownerId, leaseGeneration, sessionId,
                worktree.Path, worktree.Branch, mode, DateTimeOffset.UtcNow,
                IsFollowUp: followUp is not null, Model: model, RunDirectory: runDirectory,
                PrReviewBaseRefName: prReviewFacts?.BaseRefName, SessionName: sessionName,
                ReviewStageComposition: reviewStageComposition,
                DispatchingNodeId: dispatchingNodeId ?? nodeId,
                OpeningReviewSinceSha: followUp is not null ? task.FollowUpPullRequestHeadSha : null,
                // Recorded as the project's own base branch's NAME only when it differs — blank
                // means "the project's own", which is what keeps every unstacked run's stream and
                // every stream written before this field byte-identical in meaning (the field's
                // own doc). A stacked replay is the one follow-up whose base moves: closeout has
                // already retargeted the pull request onto the project's base by the time it
                // reopens, and that retarget cleared the previous run's own record, so the base
                // carried forward for it is the project's base too.
                BaseBranch: runBaseBranch == project.BaseBranch ? string.Empty : runBaseBranch,
                // Resolved above, once, so the record and every prompt below name the same commit.
                BaseCommit: baseCommit,
                // Carried forward from the resumed branch's own previous run, exactly as BaseBranch
                // and BaseCommit are just above (StackedBaseResolver.ResumedBase's own doc) — null
                // for a fresh cut, which is every ordinary run.
                OpenedAgainstBaseBranch: resumedBase?.OpenedAgainstBaseBranch));
            // Appended in the dispatch's own commit so the run's record of what it set out to
            // review can never be missing from a run that is already dispatched (idea b9b09779,
            // piece 1). Read back by PrReviewEngine, by the findings report, and by h9k task show,
            // none of which re-derive it from the registry: a persona registered between this
            // dispatch and that read must not change what this run is said to have done.
            if (personaPlan is not null)
            {
                session.Events.Append(runId, new PrReviewPersonasSelected(
                    runId, personaPlan.Requested, personaPlan.Ran, personaPlan.Skipped,
                    personaPlan.FellBackToEngineer, DateTimeOffset.UtcNow, personaPlan.DriveDecisions,
                    personaPlan.ForkSkipped, personaPlan.ForkSkipReason,
                    personaPlan.DocsOnlySkipped, personaPlan.DocsOnlySkipReason));
            }

            // Appended right behind the dispatch, in the same commit, so the run record can never
            // exist saying "resumed" while the fact that it did not is still in flight
            // (#234). Null for every run that resumed what it meant to and every
            // run that never meant to resume anything.
            if (startedClean is not null)
            {
                session.Events.Append(runId, startedClean);
            }

            await session.SaveChangesAsync(cancellationToken);

            // The reopen's kind picks the follow-up prompt; Unknown (reopens recorded
            // before the vocabulary existed) keeps the historic review-feedback meaning.
            // The commit style resolves project-over-platform (Decisions Log #26).
            CommitStyle commitStyle = CommitStyle.Resolve(project.CommitStyle, options.Value.DefaultCommitStyle);
            // The owner's standing voice preference, read once so every prompt this method can
            // assemble below names the same skill (#193). Inside the try, like every
            // other read this dispatch makes, so a transient failure on it is recorded as a launch
            // failure rather than thrown past the run this method just opened a stream for. Null
            // when the owner record is not readable at all, which renders every seam exactly as it
            // renders for an owner who never named one.
            VoiceSkillName? voiceSkill =
                (await session.LoadAsync<OwnerDetails>(ownerId, cancellationToken))?.VoiceSkill;
            string prompt;
            if (isPrReview)
            {
                // The persona plan's first session, dispatched as this run's ordinary primary
                // session — PrReviewEngine takes over from here once it completes, dispatching
                // every remaining session the plan names and never a build/fix session of any
                // kind. For an assignee who declared no persona that first session is the
                // engineer's adversarial lens, exactly as it has always been.
                string baseBranch = prReviewFacts!.BaseRefName.IsNotBlank() ? prReviewFacts.BaseRefName : project.BaseBranch;
                ReviewPersonaSession primarySession = personaPlan!.Sessions[0];
                ReviewDriveDecision primaryDrive = personaPlan.DriveFor(primarySession.Persona);
                prompt = primarySession.BuildPrompt(new ReviewPersonaPromptRequest(
                    task, project, worktree.Branch, baseBranch, options.Value.VerifyGateTimeout, primaryDrive,
                    primaryDrive.Drives ? ProjectRunSkillReader.Read(project) : null));

                // This mint itself came from a GitHub mention (idea 2f079bcd, decision 2 and 3):
                // the primary session's own ordinary verdict is not enough here, so it is also
                // asked to write mention-answer.md, which ComposeReportAndParkAsync reads and
                // appends to the findings report as a "You were asked" section — the identical
                // shape a mention that instead attaches to an already-reviewed pull request gets
                // from PrReviewEngine.DriveMentionFollowUpAsync's own bounded lap.
                //
                // Read off the ObservedReviewMention row this task was actually MINTED from
                // (Outcome == TaskCreated or TaskCreatedParked — a parked mint's own mention row
                // (independent pre-PR review, cycle 1, conformance lens) carries the identical
                // marker under the outcome the membership gate actually settled it with) — the same
                // stable marker PrReviewEngine's own park line uses — rather than task.LatestMention*,
                // which a second mention landing on this same task before its first ("first"/"normal"
                // speed) dispatch can move to a comment this addendum never answered (independent
                // pre-PR review, cycle 1, both lenses). Falls back to task.LatestMention* only when
                // that row is not there yet: at "now" speed this dispatch happens before
                // ProcessMentionAsync's own session.Store(observed) has committed, so a query-based
                // read finds nothing on that path, but nothing else could have raced the fields in
                // that window either.
                ObservedReviewMention? mintingMention = await session.Query<ObservedReviewMention>()
                    .Where(mention => mention.TaskId == taskId)
                    .Where(mention => mention.MatchesSql(
                        "d.data ->> 'outcome' IN (?, ?)",
                        ReviewMentionOutcome.TaskCreated.Value, ReviewMentionOutcome.TaskCreatedParked.Value))
                    .FirstOrDefaultAsync(cancellationToken);
                string? mentionAuthorLogin = mintingMention?.CommentAuthorLogin ?? task.LatestMentionAuthorLogin;
                string? mentionBody = mintingMention?.CommentBody ?? task.LatestMentionBody;
                string? mentionUrl = mintingMention?.CommentUrl ?? task.LatestMentionUrl;
                DateTimeOffset? mentionCreatedAt = mintingMention?.CommentCreatedAt ?? task.LatestMentionCreatedAt;
                if (mentionAuthorLogin is not null && mentionCreatedAt is { } resolvedMentionCreatedAt)
                {
                    prompt += "\n\n" + MentionFollowUpPromptBuilder.BuildMintAddendum(
                        mentionAuthorLogin, resolvedMentionCreatedAt, mentionBody ?? string.Empty,
                        mentionUrl, runDirectory, voiceSkill);
                }
            }
            else if (followUp is { } review)
            {
                // A follow-up resumes work the original session already did with its blockers'
                // context in hand; re-routing it now would pay for a second synthesis to tell
                // the agent what it was told the first time. Its job is the review feedback.
                //
                // interactiveMilestoneAddress stays null here too, for the identical reason the
                // fresh-dispatch branch below states: this is a brand-new RunDispatched (the
                // StartStream call above runs unconditionally, follow-up or not), so no
                // h9k task register-session call could possibly have landed on it yet (task:
                // agents on an interactive-mode task report outbound) — a follow-up's own build-role
                // milestones (OutboundMilestone.Build) log a skip on every production path today,
                // exactly like a fresh build's (independent pre-PR review, cycle 1, conformance lens).
                //
                // baseCommit is this run's own recorded fork point, resolved above: on a stacked
                // child every instruction that rewrites this branch's history — the rebase replay,
                // the narrative style's fixup-fold — has to key off that commit rather than
                // origin/<parent>, which a force-pushed parent moves out from under it
                // (independent pre-PR review, cycle 2, adversarial lens). A replay is the one
                // follow-up that names its own boundary and onto-commit explicitly instead.
                prompt = task.FollowUpKind == FollowUpKind.FailingChecks
                    ? AgentPromptBuilder.BuildFixChecks(
                        task, project, worktree.Branch, review.PullRequestUrl, commitStyle,
                        interactiveMilestoneAddress: null, baseBranch: runBaseBranch,
                        baseCommit: baseCommit, commandTimeout: options.Value.VerifyGateTimeout,
                        voiceSkill: voiceSkill, lessons: lessons)
                    : task.FollowUpKind == FollowUpKind.Rebase
                        // voiceSkill on both of these too: each ends in the rebase verification
                        // rule, whose gate-fix instruction asks an append-style project for an
                        // authored commit message (follow-up review finding, PR #376).
                        ? AgentPromptBuilder.BuildRebase(
                            task, project, worktree.Branch, review.PullRequestUrl, commitStyle,
                            interactiveMilestoneAddress: null, baseBranch: runBaseBranch,
                            baseCommit: baseCommit, commandTimeout: options.Value.VerifyGateTimeout,
                            voiceSkill: voiceSkill, lessons: lessons)
                        : isStackReplay
                            ? AgentPromptBuilder.BuildStackReplay(
                                task, project, worktree.Branch, review.PullRequestUrl, commitStyle,
                                runBaseBranch,
                                task.StackReplayUpstreamCommit ?? string.Empty,
                                baseCommit,
                                commandTimeout: options.Value.VerifyGateTimeout,
                                voiceSkill: voiceSkill,
                                ontoCommitResolvedFromCurrentBaseTip: stackReplayOnto.ResolvedFromCurrentBaseTip,
                                recordedOntoCommit: task.StackReplayOntoCommit,
                                lessons: lessons)
                            // A human's changes-requested review gets its own prompt rather than
                            // the thread one (task: a changes-requested pull-request review from a
                            // human becomes a fix lap): the findings are handed over, and a
                            // disagreement is drafted for the implementer instead of posted.
                            : task.FollowUpKind == FollowUpKind.ReviewRequestedChanges
                                ? AgentPromptBuilder.BuildReviewRequestedChanges(
                                    task, project, worktree.Branch, review.PullRequestUrl, commitStyle,
                                    interactiveMilestoneAddress: null, baseBranch: runBaseBranch,
                                    baseCommit: baseCommit, commandTimeout: options.Value.VerifyGateTimeout,
                                    voiceSkill: voiceSkill, lessons: lessons)
                                : AgentPromptBuilder.BuildFollowUp(
                                    task, project, worktree.Branch, review.PullRequestUrl, commitStyle,
                                    interactiveMilestoneAddress: null, baseBranch: runBaseBranch,
                                    baseCommit: baseCommit, commandTimeout: options.Value.VerifyGateTimeout,
                                    voiceSkill: voiceSkill, lessons: lessons);
            }
            else
            {
                // Context routing (Decisions Log #36). The run stream exists by now, so the
                // synthesis pass has somewhere to record itself, and the build session has not
                // spawned yet — the whole point is that the dependent starts already knowing.
                // A spike never reads this (see the comment on the branch below): assembling it
                // anyway would spawn — and pay for — a full synthesis session whose entire output
                // BuildSpike then has no parameter to receive (independent pre-PR review, cycle 1,
                // both lenses).
                string? handoffs = task.Type == TaskType.Spike
                    ? null
                    : await blockerContext.AssembleAsync(
                        runId, runDirectory, task, project, worktree.Path, mode, cancellationToken);
                // interactiveMilestoneAddress stays null: this is a brand-new RunDispatched, so no
                // h9k task register-session call could possibly have landed on it yet (task:
                // agents on an interactive-mode task report outbound) — a genuinely fresh headless
                // build under interactive mode always starts with nobody registered to address.
                // TaskStartCommand's own headless-start dispatch and the follow-up branch above are
                // the other build-role callers in the identical position, so a build session's own
                // outbound milestones (OutboundMilestone.Build) log a skip on every production path
                // today; only the review and fix roles dispatched later on this same run, once a
                // human's own h9k task work claim has registered against it, can actually address one
                // (independent pre-PR review, cycle 1, adversarial lens; AGENTS.md and
                // docs/scope.md say so plainly).
                // baseCommit is this cut's own observed start point — the identical value recorded on
                // RunDispatched.BaseCommit above, read from the same local, and the only stable fork
                // point a stacked session's recompose can reset to (independent pre-PR review,
                // cycle 1, adversarial lens: `git merge-base origin/<parent> HEAD` collapses below it
                // the moment the parent is force-pushed, and the recompose's mixed reset would then
                // rewrite the parent's commits as the child's own history).
                // A spike's own build prompt (task: a spike is a run, not a walk) — its kind and
                // exit criterion drive the whole prompt, and it never joins the ordinary
                // blocker-context/voice-skill/resume machinery above: a spike is never a
                // follow-up and never resumes previous work (RunDispatched always cuts it a
                // fresh branch — see the worktree checkout above).
                prompt = task.Type == TaskType.Spike
                    ? AgentPromptBuilder.BuildSpike(task, commandTimeout: options.Value.VerifyGateTimeout)
                    : AgentPromptBuilder.Build(
                        task, project, worktree.Branch, worktree.Path, resumesPreviousWork, handoffs,
                        baseBranch: runBaseBranch, baseCommit: baseCommit,
                        commandTimeout: options.Value.VerifyGateTimeout, voiceSkill: voiceSkill,
                        lessons: lessons);
            }

            // isPrReview and the followUp branches above both compose through AgentPromptBuilder's
            // own review/follow-up methods, which splice PromptBuilderKey.Agent; only the fresh-
            // dispatch else branch composes through AgentPromptBuilder.Build, a thin forward to
            // WorkPromptBuilder.Build, which splices PromptBuilderKey.Work instead — so the key
            // logged here has to track which branch actually ran rather than naming Agent
            // unconditionally (verify pass, cycle 2, conformance lens).
            PromptBuilderKey composedWithAddendumKey = isPrReview || followUp is not null
                ? PromptBuilderKey.Agent
                : PromptBuilderKey.Work;
            LogIfOverCapAddendum(runId, project, composedWithAddendumKey);

            // Re-checked here, immediately before the actual spawn, rather than trusting the
            // fence read at the top of this method alone (Copilot review, PR #334, a suppressed
            // finding on that earlier check): the worktree checkout, blocker-context assembly,
            // and prompt building above can each take real wall-clock time, and an abandon
            // landing anywhere in that window still finds CurrentRunId naming this run (abandon
            // never clears it) and would otherwise reach SpawnAsync anyway. This does not close
            // the race outright — nothing can, short of making a live OS process spawn itself
            // transactional — but it shrinks the window from "however long checkout and prompt
            // assembly take" down to the gap between this read and the spawn call, and a
            // rejection here retires the run stream StartStream already opened above rather than
            // spawning into a task nobody is coming back to.
            //
            // The same window is exactly where h9k run kill (task: a run can be killed without
            // killing its task) can end this run's own context-synthesis session, and TaskFailed
            // never clears CurrentRunId either, so the identity fence below alone still says yes
            // to a run already recorded Killed. Checked as its own branch first, rather than
            // folded into the rejection below (which retires the run with RunSuperseded): a run
            // already carrying a terminal record must never have a second terminal event
            // appended over it (independent pre-PR review, cycle 1, conformance lens).
            RunDetails? currentRun = await session.LoadAsync<RunDetails>(runId, cancellationToken);
            if (currentRun is { State.IsTerminal: true })
            {
                logger.LogInformation(
                    "Run {RunId}: already {State} by the time its pre-spawn fence was checked - not spawning",
                    runId, currentRun.State.Value);
                return;
            }

            if (!await GenerationFence.AllowsAsync(
                session, logger, taskId, runId, leaseGeneration, "to spawn", cancellationToken,
                refuseAbandonedTask: true))
            {
                await using IDocumentSession retireSession = store.LightweightSession();
                retireSession.Events.Append(runId, new RunSuperseded(runId, leaseGeneration, DateTimeOffset.UtcNow));
                await retireSession.SaveChangesAsync(cancellationToken);
                return;
            }

            // The primary pr-review session's own QA exception (security review idea 6be68ee2,
            // process-injection finding 1): only when this run's assignee holds no other persona
            // ahead of QA in the fixed order, so QA's own session is the run's primary one — the
            // ordinary case is the engineer's adversarial lens instead, which never earns this.
            // By construction this never co-occurs with a fork head, since ReviewPersonaRegistry
            // .Plan skips QA outright there.
            IReadOnlyList<VerifyCommand>? primaryQaGateCommands = isPrReview
                && personaPlan!.Sessions[0].Persona == ReviewPersona.Qa
                ? QaGateCommandsResolver.Resolve(project)
                : null;

            SpawnedAgent agent = await executor.SpawnAsync(
                new AgentSpawnRequest(
                    runId, sessionId, worktree.Path, runDirectory, prompt, mode, model, effort,
                    // Hardcoded false for a pr-review primary session, never project.SkipPermissions
                    // (Brian's ruling 2026-09-27: no pr-review session ever runs with permissions
                    // skipped, member or not) — the project's own setting is consulted only for an
                    // ordinary build session, which this same call site also spawns.
                    SkipPermissions: isPrReview ? false : project.SkipPermissions,
                    UntrustedWorkingDirectory: isPrReview,
                    // Every follow-up kind, not only the review-feedback one (task: a
                    // review-feedback follow-up never answers a human reviewer in the owner's
                    // name on its own): a follow-up is exactly the session that works an open
                    // pull request's threads, and a CI-fix lap wandering into a person's thread
                    // is as much the thing being prevented as a thread lap doing it on purpose.
                    GuardsReviewThreadReplies: followUp is not null,
                    // TaskConstraints' first consumer (task: a spike is a run, not a walk):
                    // passed straight through to the agent launch as its own hard turn limit —
                    // null (no declared budget) leaves this session exactly as unbounded as
                    // every other task's own build session already is.
                    MaxTurns: task.Constraints?.MaxTurns)
                {
                    TaskId = task.Id,
                    SessionName = sessionName,
                    UsesReviewPermissions = isPrReview,
                    QaGateCommands = primaryQaGateCommands,
                },
                cancellationToken);

            await using IDocumentSession startSession = store.LightweightSession();
            startSession.Events.Append(runId, new RunProcessStarted(runId, agent.ProcessId, agent.StartedAt));
            startSession.Store(new RunActivity { Id = runId, LastActivityAt = DateTimeOffset.UtcNow, StreamBytesRead = 0 });
            await startSession.SaveChangesAsync(cancellationToken);

            supervisor.StartMonitoring(runId, runDirectory, taskId, agent.ProcessId, agent.StartedAt, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Launch failed for run {RunId}", runId);
            await RecordLaunchFailureAsync(taskId, runId, leaseGeneration, exception.Message, cancellationToken);
        }
    }

    /// <summary>
    /// The bounded follow-up lap's own launch (idea 2f079bcd, auto-pr-review's second trigger,
    /// decision 2): a fresh, single-session run under an EXISTING pr-review task, answering one
    /// GitHub comment that mentioned the install's login after the original review already
    /// completed. Deliberately its own method rather than a mode threaded through
    /// <see cref="LaunchAsync"/> — that method's <c>isPrReview</c> branch always dispatches the
    /// adversarial lens as the run's primary session, and PrReviewEngine takes over from there to
    /// dispatch the conformance lens second; a mention follow-up runs neither, so bending that
    /// 400-line method around a second prompt for a case it was never shaped for would risk the
    /// ordinary dispatch it already carries. Reuses only what is genuinely shared: the PR-facts
    /// read, the checkout, and the process spawn — the same primitives-only reuse
    /// <c>PrReviewEngine</c>'s own class doc describes for its relationship to <c>ReviewEngine</c>.
    /// <para>
    /// The caller (<c>AutoPrReviewEngine</c>) has already appended
    /// <see cref="Handlers.TaskDecider.ClaimForMentionFollowUp"/> before calling this — the task is
    /// Claimed under a fresh generation by the time this runs, exactly as every other launch here
    /// assumes.
    /// </para>
    /// </summary>
    public async Task LaunchPrReviewMentionFollowUpAsync(
        Guid taskId, Guid runId, Guid ownerId, int leaseGeneration, Guid dispatchingNodeId,
        PullRequestMentionComment comment, Guid? priorReviewRunId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        TaskDetails? task = await session.LoadAsync<TaskDetails>(taskId, cancellationToken);
        ProjectDetails? project = task is null
            ? null
            : await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
        if (task is null || project is null)
        {
            logger.LogError("Cannot launch mention follow-up run {RunId}: task or project missing", runId);
            return;
        }

        // refuseAbandonedTask: true for the identical reason LaunchAsync's own fence carries it
        // (Copilot review, PR #334): AutoPrReviewEngine commits ClaimForMentionFollowUp before
        // calling this method, so an abandon landing in that same window still finds
        // CurrentRunId naming this run and, without this, would still spawn a fresh agent
        // session against work a human already walked away from. No run stream exists yet at
        // this point either, so a rejection here has nothing of its own to retire.
        if (!await GenerationFence.AllowsAsync(
            session, logger, taskId, runId, leaseGeneration, "to launch a mention follow-up", cancellationToken,
            refuseAbandonedTask: true))
        {
            return;
        }

        try
        {
            PullRequestFacts? facts = await FetchOpenPullRequestFactsAsync(task, project, processRunner, cancellationToken);
            if (facts is null)
            {
                await RecordLaunchFailureAsync(
                    taskId, runId, leaseGeneration,
                    $"{task.ExternalReference} is no longer open — nothing to read a mention follow-up against.",
                    cancellationToken);
                return;
            }

            // The identical foreign-repository guard LaunchAsync's own isPrReview branch runs
            // before every checkout — worth re-checking on every dispatch, not only the task's
            // first, since a project's own effective repository can change after the task was
            // minted (h9k project set --repo re-points the project at a different local checkout,
            // whose own git remote resolves to a different GitHub repository).
            Uri? projectRepositoryUrl = project.RepositoryUrl
                ?? await new GitHubWorkItemProvider(processRunner).TryObserveRepositoryHostAsync(
                    project.RepositoryPath, cancellationToken);
            if (OwnerRepoFrom(projectRepositoryUrl) is { } projectRepository
                && !string.Equals(projectRepository, facts.Repository, StringComparison.OrdinalIgnoreCase))
            {
                await RecordLaunchFailureAsync(
                    taskId, runId, leaseGeneration,
                    $"{task.ExternalReference} lives in {facts.Repository}, not {project.Name}'s own repository "
                    + $"({projectRepository}). A pr-review task's worktree is always cut from the project it "
                    + $"was adopted against, so reviewing a pull request from another repository needs a "
                    + $"project registered against {facts.Repository} instead.", cancellationToken);
                return;
            }

            // The identical pre-flight gate LaunchAsync's own isPrReview branch requires (idea
            // 6be68ee2, finding 1, phase one — the acceptance criterion is explicit that BOTH
            // daemon checkout sites require a safe verdict for the current head oid): this method
            // cuts a fresh checkout too, and never passes through that branch, so it needs its own
            // call to the identical gate rather than inheriting one it never runs through.
            if (!await EnsurePrReviewPreflightSafeAsync(
                session, task, project, facts, dispatchingNodeId, runId, leaseGeneration, isMentionFollowUp: true,
                cancellationToken))
            {
                return;
            }

            await CleanUpPreviousPrReviewWorktreesAsync(taskId, project, cancellationToken);
            Worktree worktree;
            try
            {
                worktree = await worktrees.CreatePrReviewCheckoutAsync(
                    new PrReviewWorktreeRequest(project.RepositoryPath, facts.Number, taskId, runId, facts.HeadRefOid),
                    cancellationToken);
            }
            catch (PullRequestHeadMovedException exception)
            {
                logger.LogWarning(
                    "Task {TaskId}: {Message} — dispatching a fresh pre-flight instead of proceeding",
                    taskId, exception.Message);
                await DispatchPrReviewPreflightAsync(
                    task, project, facts with { HeadRefOid = exception.ObservedHeadOid }, dispatchingNodeId, runId,
                    leaseGeneration, isMentionFollowUp: true, cancellationToken, headOidFromGitFetch: true);
                return;
            }

            await RenderKnowledgeDocumentsIntoAsync(session, project, worktree.Path, runId, cancellationToken);

            Guid sessionId = DomainId.New();
            AgentModel model = options.Value.ResolveModel(AgentRole.Review, task.Model, project.Model);
            AgentEffort effort = options.Value.ResolveEffort(AgentRole.Review, task.Effort, project.Effort);
            string sessionName = SessionRoleName.For(DomainId.Short(taskId), SessionRoleName.PrReviewMentionFollowUp);

            string? existingTaskDirectory = project.HomeDirectory.HasValue
                ? HomeEntryWriter.FindExistingDirectory(
                    ProjectHomePaths.TasksDirectory(project.HomeDirectory.Value), taskId,
                    alternateRoots: [ProjectHomePaths.ArchivedTasksDirectory(project.HomeDirectory.Value)])
                : null;
            string runDirectory = existingTaskDirectory is not null
                ? RunPaths.ResolveDirectoryUnderTaskDirectory(existingTaskDirectory, runId)
                : RunPaths.ResolveDirectory(project.HomeDirectory, TaskDocumentRenderer.DirectoryName(task), runId);

            session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, taskId, Guid.Empty, ownerId, leaseGeneration, sessionId,
                worktree.Path, worktree.Branch, ExecutorMode.Subscription, DateTimeOffset.UtcNow,
                Model: model, RunDirectory: runDirectory, PrReviewBaseRefName: facts.BaseRefName,
                SessionName: sessionName, ReviewStageComposition: ReviewStageComposition.FullPipeline,
                DispatchingNodeId: dispatchingNodeId, PrReviewMentionCommentId: comment.CommentId,
                // A pr-review run of any kind has no base of its own to record — the same reading
                // isPrReview's own StackedBase carries in the ordinary dispatch above — and the
                // detached checkout's own start point is its BaseCommit, the identical value a
                // fresh (non-resumed) unstacked checkout records there.
                BaseBranch: string.Empty, BaseCommit: worktree.StartPointCommit));
            await session.SaveChangesAsync(cancellationToken);

            string baseBranch = facts.BaseRefName.IsNotBlank() ? facts.BaseRefName : project.BaseBranch;
            string? priorReport = priorReviewRunId is { } priorRunId
                ? await ReadPriorReviewReportAsync(priorRunId, cancellationToken)
                : null;
            string prompt = MentionFollowUpPromptBuilder.Build(
                facts.Repository, facts.Number, worktree.Path, baseBranch: baseBranch, comment: comment,
                priorReport: priorReport, project: project,
                // The drafted reply this session produces is written first-person as the owner, so
                // the seam names their own voice skill when they have one (#193).
                voiceSkill: (await session.LoadAsync<OwnerDetails>(ownerId, cancellationToken))?.VoiceSkill);

            LogIfOverCapAddendum(runId, project, PromptBuilderKey.MentionFollowUp);

            // Re-checked here, immediately before the actual spawn, rather than trusting the
            // fence read at the top of this method alone (independent pre-PR review, cycle 1,
            // adversarial lens — LaunchAsync's own sibling pre-spawn check, above, closes the
            // identical gap): the GitHub fetch, the worktree checkout, and the prompt build above
            // can each take real wall-clock time, and an abandon landing anywhere in that window
            // still finds CurrentRunId naming this run (abandon never clears it) and would
            // otherwise reach SpawnAsync anyway. A rejection here retires the run stream
            // StartStream already opened above rather than spawning into a task nobody is coming
            // back to.
            if (!await GenerationFence.AllowsAsync(
                session, logger, taskId, runId, leaseGeneration, "to spawn a mention follow-up", cancellationToken,
                refuseAbandonedTask: true))
            {
                await using IDocumentSession retireSession = store.LightweightSession();
                retireSession.Events.Append(runId, new RunSuperseded(runId, leaseGeneration, DateTimeOffset.UtcNow));
                await retireSession.SaveChangesAsync(cancellationToken);
                return;
            }

            // SkipPermissions is hardcoded false, never project.SkipPermissions, and
            // UsesReviewPermissions is unconditionally true: a mention follow-up is one of the
            // three real spawn sites this security review moved off
            // --dangerously-skip-permissions (Brian's ruling 2026-09-27, no pr-review session
            // ever runs with permissions skipped) — the project's own setting is never consulted
            // here.
            SpawnedAgent agent = await executor.SpawnAsync(
                new AgentSpawnRequest(
                    runId, sessionId, worktree.Path, runDirectory, prompt, ExecutorMode.Subscription, model,
                    effort, SkipPermissions: false, UntrustedWorkingDirectory: true,
                    // A hard turn limit, the same shape CourierEngine passes CourierMaxTurns with:
                    // a follow-up reads the prior report, the thread, and parts of the diff, writes
                    // an addendum, and never posts, so it needs bounded turns rather than the
                    // unbounded budget an ordinary build session gets (task 7ae690f5). A run that
                    // hits the limit ends visibly: RunSupervisor.CompleteRunAsync reads the
                    // terminal result's own "error_max_turns" subtype against this run's
                    // PrReviewMentionCommentId and fails the task outright, rather than spending
                    // the ordinary error-result retry on a resume that would carry no turn cap of
                    // its own — never a silent hang, and never an unbounded second attempt.
                    MaxTurns: options.Value.PrReviewMentionFollowUpMaxTurns)
                {
                    TaskId = taskId,
                    SessionName = sessionName,
                    UsesReviewPermissions = true,
                },
                cancellationToken);

            await using IDocumentSession startSession = store.LightweightSession();
            startSession.Events.Append(runId, new RunProcessStarted(runId, agent.ProcessId, agent.StartedAt));
            startSession.Store(new RunActivity { Id = runId, LastActivityAt = DateTimeOffset.UtcNow, StreamBytesRead = 0 });
            await startSession.SaveChangesAsync(cancellationToken);

            supervisor.StartMonitoring(runId, runDirectory, taskId, agent.ProcessId, agent.StartedAt, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Mention follow-up launch failed for run {RunId}", runId);
            await RecordLaunchFailureAsync(taskId, runId, leaseGeneration, exception.Message, cancellationToken);
        }
    }

    /// <summary>
    /// The original review's own merged findings report, read off disk for the follow-up prompt to
    /// cite against — best-effort, since a missing or unreadable report is not a reason to refuse
    /// answering a tagged comment: the follow-up prompt says plainly when none was found.
    /// </summary>
    private async Task<string?> ReadPriorReviewReportAsync(Guid priorReviewRunId, CancellationToken cancellationToken)
    {
        try
        {
            await using IQuerySession query = store.QuerySession();
            RunDetails? priorRun = await query.LoadAsync<RunDetails>(priorReviewRunId, cancellationToken);
            if (priorRun is null)
            {
                return null;
            }

            string path = RunPaths.ReviewFindingsFile(RunPaths.ResolveCurrentDirectory(priorRun.RunDirectory), 1);
            return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not read the prior review report for run {RunId}", priorReviewRunId);
            return null;
        }
    }

    /// <summary>
    /// A task reaching dispatch with a pull-request URL is a requeue or reopen — and the
    /// PR may have merged while the task sat queued. Ask the provider before spawning:
    /// merged work closes out (the task completes with its PR, the lease releases, the
    /// workspace is cleaned) and is never rebuilt. Origin incident (2026-08-18): after
    /// PR #11 merged, the storm-killed generation 5's lease expiry requeued the task and
    /// generation 6 spawned a fresh agent to rebuild the feature already on main.
    /// Inspection failure (the network is often still down right after a wake) falls
    /// back to a normal dispatch rather than blocking the task. True means "do not
    /// dispatch": either this run closed the task out, or the PR is merged but this run's
    /// generation is stale, in which case the live generation owns the task and this run
    /// must not fall through to a fresh dispatch either.
    /// <para>
    /// Applies the same repository-match guard <see cref="PullRequestUrls.IsSafePullRequestUrl"/>
    /// enforces everywhere else a task's own <c>PullRequestUrl</c> reaches <c>gh</c>. This is now
    /// belt-and-suspenders rather than a closed exploit path on its own: <c>TaskResolveCommand.ExecuteAsync</c>'s
    /// task-stream write (<c>SafeTaskStreamPullRequestUrl</c>) used to record a mismatched
    /// <c>--pr</c> onto the task stream verbatim, unguarded, whenever a run stream already existed —
    /// reasoning only about the missing-run sweep's own candidate shape — and a task later reopened
    /// through <c>h9k pr resolve</c> (which resumes the real, already-pushed branch off
    /// <c>RunDetails</c>, never the recorded URL, so the guard on <c>Reopen</c> itself stops nothing
    /// here) would carry that unguarded URL into this very check on its next dispatch, where without
    /// this guard <c>gh pr view &lt;number&gt;</c> below would resolve a foreign URL's number inside
    /// the project's own repository (routed defect fix, independent pre-PR review, cycle 1, medium:
    /// that write site is now guarded unconditionally, so this check no longer has a known live path
    /// feeding it an unsafe URL — but it stays, since nothing about this method's own contract
    /// depends on that write site staying guarded). A pr-review task's own <c>PullRequestUrl</c> can
    /// never carry a live URL into this dispatch-time recheck (Reopen refuses the type outright, so a
    /// pr-review task's Done state has no lever back to this method), so that half of the check is
    /// belt-and-suspenders here rather than a closed exploit path the way the repository check is.
    /// </para>
    /// </summary>
    private async Task<bool> TryCloseOutMergedPullRequestAsync(
        TaskDetails task, ProjectDetails project, Guid taskId, Guid runId, Guid nodeId, Guid ownerId,
        int leaseGeneration, CancellationToken cancellationToken)
    {
        string pullRequestUrl = task.PullRequestUrl!;
        int pullRequestNumber = PullRequestUrls.ParseNumber(pullRequestUrl);
        if (pullRequestNumber <= 0 || task.Type == TaskType.PrReview)
        {
            return false;
        }

        Uri? projectRepositoryUrl = project.RepositoryUrl
            ?? await new GitHubWorkItemProvider(processRunner).TryObserveRepositoryHostAsync(
                project.RepositoryPath, cancellationToken);
        if (!PullRequestUrls.IsSafePullRequestUrl(pullRequestUrl, projectRepositoryUrl))
        {
            return false;
        }

        PullRequestSnapshot snapshot;
        try
        {
            snapshot = await inspector.InspectAsync(
                project.RepositoryPath, pullRequestUrl, pullRequestNumber, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception,
                "Could not check pull request {Url} before dispatching run {RunId}; dispatching normally",
                pullRequestUrl, runId);
            return false;
        }

        if (!snapshot.IsMerged)
        {
            return false;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();
        if (!await GenerationFence.AllowsAsync(
            session, logger, taskId, runId, leaseGeneration, nameof(TaskCompleted), cancellationToken))
        {
            // The PR is merged, but this run is no longer its task's current generation —
            // the live generation owns closing this out (or already has). Reporting this as
            // "not merged" would send the caller to CheckoutFreshOrRetryAsync and spawn a
            // fresh agent to rebuild work already on main: the exact origin incident above.
            // A stale generation must never write task state, but it must also never fall
            // through to a dispatch it has no right to make.
            logger.LogInformation(
                "Task {TaskId}: pull request {Url} already merged but run {RunId} is a stale generation; skipping dispatch without closing out",
                taskId, pullRequestUrl, runId);
            return true;
        }

        (TaskAggregate Task, long Version)? fenced = await GenerationFence.LoadFencedAsync(session, taskId, cancellationToken);
        if (fenced is not { } current
            || current.Task.State != TaskState.Claimed || current.Task.CurrentRunId != runId)
        {
            return false;
        }

        session.Events.Append(
            taskId, expectedVersion: current.Version + 1, TaskDecider.Complete(current.Task, runId, pullRequestUrl, now));
        session.Delete<TaskLease>(taskId);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            // A claim landed between the version above and this commit — the live
            // generation now owns the task, so this run must stop exactly like the
            // !AllowsAsync branch above: true means "do not dispatch", never fall
            // through to CheckoutFreshOrRetryAsync and spawn a second agent.
            logger.LogInformation(
                "Task {TaskId}: lost the generation race closing out pull request {Url} — a newer claim committed first; skipping dispatch",
                taskId, pullRequestUrl);
            return true;
        }

        logger.LogInformation(
            "Task {TaskId}: pull request {Url} already merged — closed out instead of dispatching run {RunId}",
            taskId, pullRequestUrl, runId);

        await CleanUpMergedWorkspaceAsync(task, project.RepositoryPath, taskId, nodeId, cancellationToken);

        // TaskCompleted above only moves the task to Done — this run's stream was never
        // started (there was never anything to spawn), so nothing has yet recorded the merge
        // itself, unblocked dependents, told the linked card, or produced a handoff. Origin
        // incident (2026-08-28 needs-you cleanup): the first cut of this method stopped at
        // TaskCompleted, and every task closed out this way came back needs-you within
        // minutes — Delivered's "no run record is watching it" arm, because nothing ever
        // would. CloseoutEngine.ReconstructAndCompleteAsync reconstructs a minimal run record
        // for runId (it was never dispatched, so there is nothing but the id and the pull
        // request to reconstruct from) and then runs the same closeout every merged run gets.
        await closeout.ReconstructAndCompleteAsync(
            session, current.Task, project, runId, nodeId, ownerId, snapshot.MergedAt, now, cancellationToken);
        return true;
    }

    /// <summary>
    /// True means "do not dispatch a build or review session": the failed run this task's own
    /// retry resumes never reached the tree itself, only the pull-request open at the very end of
    /// it (task: a run that failed only at pull-request opening resumes at that step on retry) —
    /// the branch was already pushed and its review, if any, already settled, so the only thing
    /// left owed is retrying <c>gh pr create</c> against the identical tip. Re-running the whole
    /// build and review pipeline over a tree nothing has touched since would cost real tokens to
    /// re-derive a verdict already on record.
    /// <para>
    /// Every condition this checks is load-bearing, not merely defensive: the failure must be
    /// recorded as <see cref="RunDetails.FailedDuringPullRequestOpen"/> on the specific run
    /// <see cref="TaskDetails.FailedRunId"/> names (a gate, review, or build failure never sets it,
    /// so an ordinary retry falls straight through to the ordinary path below), and the branch's
    /// tip must still match what that run pushed
    /// (<see cref="TaskDetails.LastPushedBranchTip"/>) on BOTH origin and the local ref in the
    /// reused worktree — read live, here, rather than trusted from the earlier record, because
    /// anything could have moved this branch since (a human, a follow-up dispatched some other
    /// way, a second retry racing this one, an interactive claim landing on the retained
    /// worktree). Origin alone is not enough: <see cref="PullRequestOpener"/> pushes whatever the
    /// local ref currently is, and its force-with-lease guard only refuses a tip outside the
    /// allow-list, it never confirms the local ref hasn't moved past the recorded one. A tip that
    /// moved, on either side, is not this method's problem to solve: it falls back to the ordinary
    /// dispatch, which resumes the branch through the same <see cref="CheckoutFreshOrRetryAsync"/>
    /// path any other retry does and lets a fresh build session decide what to make of whatever is
    /// there now.
    /// </para>
    /// <para>
    /// The run this method starts reuses the failed run's own <see cref="RunDetails.WorktreePath"/>
    /// and <see cref="RunDetails.RunDirectory"/> rather than cutting anything fresh: no worktree
    /// checkout runs, and reusing the run directory is what lets
    /// <see cref="PullRequestOpener.CreateArgumentsAsync"/> still find the original build session's
    /// <c>pr-summary.md</c> and closing stream — a pull request opened this way carries the exact
    /// body it would have carried had <c>gh pr create</c> simply succeeded the first time, not a
    /// skeleton fallback for a summary that still exists on disk.
    /// </para>
    /// </summary>
    private async Task<bool> TryResumeAtPullRequestOpenAsync(
        TaskDetails task, Guid taskId, Guid runId, Guid nodeId, Guid ownerId,
        int leaseGeneration, CancellationToken cancellationToken)
    {
        if (task.Type == TaskType.PrReview
            || task.RetryBranch.IsBlank()
            || task.FailedRunId is not { } failedRunId
            || task.LastPushedBranch != task.RetryBranch
            || task.LastPushedBranchTip is not { } recordedTip)
        {
            return false;
        }

        // A pending operator reason — an `h9k task retry --reason` or an `h9k task handback`
        // note not yet consumed by a build session — is an instruction for the build itself
        // (WorkPromptBuilder.AppendOperatorGuidanceSection and its handback-causeless sibling
        // above it in that file are the only things that ever render it). This shortcut
        // dispatches no build session at all, only PullRequestOpener, so a real reason here
        // would silently go unread and the same `gh pr create` failure would repeat on every
        // later retry for as long as the tip stays put (independent pre-PR review, cycle 1,
        // adversarial lens). The default filler text `h9k task retry` writes with no `--reason`
        // asserts nothing to prioritize, so it does not disqualify the shortcut.
        if (task.RetryPending
            && task.RetryReason.IsNotBlank()
            && (task.RetryReasonIsHandback || task.RetryReason != TaskDecider.DefaultRetryReason))
        {
            logger.LogInformation(
                "Task {TaskId}: a pending operator reason is waiting for a build session to read — "
                + "dispatching a full build and review pipeline instead of resuming directly at "
                + "pull-request-open",
                taskId);
            return false;
        }

        await using IDocumentSession session = store.LightweightSession();
        RunDetails? failedRun = await session.LoadAsync<RunDetails>(failedRunId, cancellationToken);
        if (failedRun is null)
        {
            return false;
        }

        if (!failedRun.FailedDuringPullRequestOpen
            || failedRun.Branch != task.RetryBranch
            || failedRun.WorktreePath.IsBlank())
        {
            return false;
        }

        // Refused before Directory.Exists or RunDirectory is touched at all (security review idea
        // 6be68ee2, process-injection finding 2): failedRun replicated onto this node the same as
        // any other run record, and a run this node never actually dispatched can name a
        // WorktreePath that happens to exist here for reasons that have nothing to do with this
        // task — this node's own unrelated directory of the same name, or a value a malicious
        // teammate crafted to land on one. Without this check, Directory.Exists below would treat
        // that coincidence as "this run's worktree is right here", run git inside it, and
        // PullRequestOpener would push from it and read pr-summary.md there. Checked only once the
        // cheap eligibility checks above already passed (independent pre-PR review, cycle 1,
        // adversarial lens): an ordinary cross-node retry, whose last failed run simply never
        // failed at pull-request-open at all, used to trip this warning too, even though the
        // shortcut could never have applied to it either way — moving the check here keeps the
        // protection while dropping that false warning.
        if (failedRun.NodeId != nodeId)
        {
            logger.LogWarning(
                "Task {TaskId}: the failed run {FailedRunId} this task's RetryBranch would resume belongs to "
                + "node {ForeignNodeId}, not this node {NodeId} — refusing to treat its recorded worktree path "
                + "as local, and dispatching a full build and review pipeline instead",
                taskId, failedRunId, failedRun.NodeId, nodeId);
            return false;
        }

        if (!Directory.Exists(failedRun.WorktreePath))
        {
            return false;
        }

        string? originTip = await ReadRemoteBranchTipAsync(failedRun.WorktreePath, task.RetryBranch, cancellationToken);
        if (originTip != recordedTip)
        {
            logger.LogInformation(
                "Task {TaskId}: branch {Branch}'s tip on origin no longer matches {RecordedTip}, the tip run "
                + "{FailedRunId} pushed before failing at pull-request-open — dispatching a full build and "
                + "review pipeline instead of resuming directly at the open",
                taskId, task.RetryBranch, recordedTip, failedRunId);
            return false;
        }

        // Origin matching the recorded tip is only half the story: PullRequestOpener.OpenAsync
        // pushes whatever this worktree's LOCAL branch ref currently is, not the recorded tip
        // itself — its force-with-lease guard only refuses a tip that ISN'T the recorded one
        // in the allow-list, it never confirms the local ref hasn't moved past it. Between this
        // run's own failure and this retry, the same worktree can pick up commits no gate or
        // review ever saw — an interactive claim (h9k task work) landing on the retained
        // worktree, or a full-build follow-up whose lease expired before it finished and got
        // requeued without clearing FailedRunId/RetryBranch. Reading the local ref here, rather
        // than trusting the failed run's own recorded tip, is what actually closes that gap
        // (independent pre-PR review, cycle 1, adversarial lens).
        string? localTip = await ReadLocalBranchTipAsync(failedRun.WorktreePath, task.RetryBranch, cancellationToken);
        if (localTip != recordedTip)
        {
            logger.LogInformation(
                "Task {TaskId}: branch {Branch}'s local tip in {Worktree} no longer matches {RecordedTip}, the "
                + "tip run {FailedRunId} pushed before failing at pull-request-open — dispatching a full build "
                + "and review pipeline instead of resuming directly at the open",
                taskId, task.RetryBranch, failedRun.WorktreePath, recordedTip, failedRunId);
            return false;
        }

        session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
            runId, taskId, nodeId, ownerId, leaseGeneration, DomainId.New(),
            failedRun.WorktreePath, task.RetryBranch, ExecutorMode.Subscription, DateTimeOffset.UtcNow,
            IsFollowUp: false, Model: failedRun.Model, RunDirectory: failedRun.RunDirectory,
            SessionName: SessionRoleName.For(DomainId.Short(taskId), SessionRoleName.Build),
            // The failed run's OWN composition, not None: this run dispatches no reviewer of its
            // own, but the pull request it opens and h9k task show both report the composition
            // the branch's settled review actually ran under (independent pre-PR review, cycle 3,
            // both lenses — see ResumedReviewSettlement's own doc).
            ReviewStageComposition: failedRun.ReviewStageComposition,
            DispatchingNodeId: nodeId,
            BaseBranch: failedRun.BaseBranch,
            BaseCommit: failedRun.BaseCommit,
            OpenedAgainstBaseBranch: failedRun.OpenedAgainstBaseBranch,
            ResumedReviewSettlement: new ResumedReviewSettlement(
                failedRun.ReviewSettlement, failedRun.ReviewResidualsFixed, failedRun.ReviewResidualsRouted,
                failedRun.ReviewResidualsRoutingFailed, failedRun.ReviewResidualsRideAlong,
                failedRun.ReviewRideAlongFindings, failedRun.ReviewResidualsUnfixed, failedRun.ReviewUnfixedFindings,
                failedRun.InputTokens, failedRun.CacheReadInputTokens, failedRun.CacheCreationInputTokens,
                failedRun.OutputTokens)));
        await session.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Task {TaskId}: run {RunId} resumes at pull-request-open only — branch {Branch}'s tip on origin "
            + "still matches what run {FailedRunId} pushed before failing there, so no build or review session "
            + "dispatches; the run re-attempts gh pr create against the same tip",
            taskId, runId, task.RetryBranch, failedRunId);

        await pullRequests.OpenAsync(runId, taskId, cancellationToken);
        return true;
    }

    /// <summary>
    /// This project's bounded lesson section for the prompt about to be composed (idea d805fd8b,
    /// piece 5; backlog 55).
    /// <para>
    /// Best-effort in exactly the way <see cref="RenderKnowledgeDocumentsIntoAsync"/> is, and for
    /// the same reason: the run's point is the work, and a session whose prompt is missing its
    /// lessons is a worse session rather than a failed dispatch. A store or config-file problem is
    /// logged once and the run goes on with <see cref="InjectedLessons.None"/>, which composes no
    /// section at all, never a section that claims the project has nothing recorded, which would
    /// be a claim nobody here is in a position to make.
    /// </para>
    /// </summary>
    private async Task<InjectedLessons> LoadRecordedLessonsAsync(
        IQuerySession query, ProjectDetails project, Guid runId, CancellationToken cancellationToken)
    {
        try
        {
            return await LessonPromptFeed.LoadAsync(query, project.Id, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception,
                "Run {RunId}: could not read {Project}'s recorded lessons, so this session's prompt "
                + "carries no lesson section; h9k learn list still shows them", runId, project.Name);
            return InjectedLessons.None;
        }
    }

    /// <summary>
    /// Writes this project's <c>decisions.md</c> and <c>lessons.md</c> into the worktree this run
    /// is about to be spawned into (idea d805fd8b, piece 2), so a session reads the platform's own
    /// record of what binds from the checkout it is working in rather than from a hand-edited
    /// markdown file in the repository.
    /// <para>
    /// The ignore comes first and is a precondition, not a courtesy. A generated file that git
    /// reports as untracked is work a session is told to commit before it finishes (the platform's
    /// own left-behind check), and committing a projection into authored history is exactly what
    /// this feature exists to stop, so a checkout whose exclude list cannot be resolved gets no
    /// files at all and the reason is logged.
    /// </para>
    /// <para>
    /// Best-effort past that, like every other per-project step on this path: the run's whole
    /// point is the work, and a session that has to read <c>h9k decide list</c> instead of a file
    /// is a worse session rather than a failed dispatch.
    /// </para>
    /// </summary>
    private async Task RenderKnowledgeDocumentsIntoAsync(
        IQuerySession query, ProjectDetails project, string worktreePath, Guid runId,
        CancellationToken cancellationToken)
    {
        try
        {
            // The ignore is settled before anything is rendered, not after: a checkout this
            // cannot be established for gets no files, so rendering first would be two store
            // queries spent on documents there was never going to be anywhere to put.
            if (!await KnowledgeDocuments.EnsureIgnoredAsync(worktreePath, cancellationToken))
            {
                logger.LogWarning(
                    "Run {RunId}: could not resolve {Worktree}'s repository exclude list, so the decisions "
                    + "and lessons projections were not written there — they would read as untracked work",
                    runId, worktreePath);
                return;
            }

            RenderedKnowledgeDocuments documents = await KnowledgeDocuments.RenderAsync(
                query, project.Id, cancellationToken);
            KnowledgeDocumentWriteResult result = KnowledgeDocuments.WriteInto(worktreePath, documents);
            foreach (string skipped in result.SkippedForeignFiles)
            {
                logger.LogWarning(
                    "Run {RunId}: '{File}' is not this platform's own render and was left alone; this "
                    + "session reads that file rather than {Project}'s recorded decisions or lessons",
                    runId, skipped, project.Name);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception,
                "Run {RunId}: could not write the decisions and lessons projections into {Worktree}; "
                + "the session runs without them", runId, worktreePath);
        }
    }

    /// <summary>
    /// Whether every path this pull request's checkout changed against <paramref name="baseRef"/>
    /// matched <see cref="SecurityReviewNonExecutablePathRules.Resolve"/>'s own rule set (idea
    /// 6be68ee2, phase two) — never <c>project.EffectiveNonExecutablePaths</c> directly: that set
    /// answers whether a diff can break the build and test gates (Decisions Log #252), a project
    /// may add to it freely, and reusing it here let a gate-skipping addition (<c>.github/</c>,
    /// <c>*.yml</c>) silently skip the one review meant to catch a CI or release workflow change,
    /// and let its own compiled defaults (<c>.claude/skills/</c>, <c>.claude/commands/</c>, the
    /// bare <c>*.md</c> rule) wave through agent-executed content this persona exists to read
    /// (independent pre-PR review, cycle 1, both lenses).
    /// <para>
    /// <c>git diff --name-status</c>, not <c>--name-only</c>: a rename or copy is classified by
    /// both its old and new path (<see cref="WorktreeGitStatus.ParseNameStatus"/>), the same
    /// rename-safety <c>VerificationRunner</c>'s own build/test skip already has — a file moved
    /// out of a buildable tree and into <c>docs/</c> used to read as docs-only from the new path
    /// alone (independent pre-PR review, cycle 1, adversarial lens, high).
    /// </para>
    /// False for any diff this could not read, the safe direction: a pull request whose changed
    /// paths are unobservable is never guessed as content-only, so the Security persona still runs
    /// against it rather than being silently skipped on a fact nobody actually observed.
    /// </summary>
    private async Task<bool> EveryChangedPathIsNonExecutableAsync(
        string worktreePath, string baseRef, IReadOnlyList<string> nonExecutablePathRules,
        CancellationToken cancellationToken)
    {
        try
        {
            ProcessResult diff = await processRunner(
                "git", ["diff", "--name-status", "-z", $"origin/{baseRef}...HEAD"], worktreePath, cancellationToken);
            if (diff.ExitCode != 0)
            {
                return false;
            }

            IReadOnlyList<string> changedPaths = WorktreeGitStatus.ParseNameStatus(diff.StandardOutput);
            return changedPaths.Count > 0
                && NonExecutablePathClassifier.Classify(
                    changedPaths, SecurityReviewNonExecutablePathRules.Resolve(nonExecutablePathRules)).AllMatched;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception, "Could not classify changed paths in {Worktree} against {BaseRef}", worktreePath, baseRef);
            return false;
        }
    }

    /// <summary>
    /// Origin's current tip for <paramref name="branch"/>, read fresh rather than trusted from any
    /// earlier record — the live half of <see cref="TryResumeAtPullRequestOpenAsync"/>'s own two-part
    /// check. Null for every shape that is not "the ref exists and named exactly one commit": no
    /// matching ref, a read failure, or an unreachable remote all collapse to the same answer here,
    /// because every one of them fails <see cref="TryResumeAtPullRequestOpenAsync"/>'s own tip
    /// comparison and falls back to the ordinary dispatch — the safe direction when origin cannot be
    /// read is dispatching the full pipeline, never assuming the tip still matches.
    /// </summary>
    private async Task<string?> ReadRemoteBranchTipAsync(
        string worktreePath, string branch, CancellationToken cancellationToken)
    {
        try
        {
            ProcessResult probe = await processRunner(
                "git", ["ls-remote", "--exit-code", "origin", $"refs/heads/{branch}"], worktreePath, cancellationToken);
            return probe.ExitCode == 0
                ? probe.StandardOutput.Split('\t', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
                : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception, "Could not read branch {Branch}'s tip on origin in {Worktree}", branch, worktreePath);
            return null;
        }
    }

    /// <summary>
    /// <paramref name="branch"/>'s own local ref, read fresh in <paramref name="worktreePath"/> —
    /// the other half of <see cref="TryResumeAtPullRequestOpenAsync"/>'s tip check, alongside
    /// <see cref="ReadRemoteBranchTipAsync"/>. Reading the ref by name rather than the worktree's
    /// own <c>HEAD</c> is deliberate: a worktree's branch and its checked-out commit can only
    /// diverge if something has switched it to a different ref entirely, and refs are shared
    /// across every worktree in this repository, so this answers "what does the branch itself
    /// point at right now" regardless of which worktree happens to have it checked out. Null,
    /// exactly like the remote read, collapses every failure shape (missing ref, read failure) to
    /// the same answer, and the caller's tip comparison already treats null as a mismatch.
    /// </summary>
    private async Task<string?> ReadLocalBranchTipAsync(
        string worktreePath, string branch, CancellationToken cancellationToken)
    {
        try
        {
            ProcessResult probe = await processRunner(
                "git", ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"], worktreePath, cancellationToken);
            return probe.ExitCode == 0 ? probe.StandardOutput.Trim() : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception, "Could not read branch {Branch}'s local tip in {Worktree}", branch, worktreePath);
            return null;
        }
    }

    /// <summary>
    /// The launch-time twin of CloseoutEngine.CompleteCloseoutAsync's cleanup: the dead
    /// generation's retained worktree still has the merged branch checked out, so remove
    /// this node's worktrees for the task first, then delete the branch everywhere it
    /// lingers. Best-effort — the merge already closed the story.
    /// </summary>
    private async Task CleanUpMergedWorkspaceAsync(
        TaskDetails task, string repositoryPath, Guid taskId, Guid nodeId, CancellationToken cancellationToken)
    {
        IReadOnlyList<RunDetails> previousRuns;
        await using (IQuerySession query = store.QuerySession())
        {
            previousRuns = await query.Query<RunDetails>()
                .Where(r => r.TaskId == taskId && r.NodeId == nodeId)
                .ToListAsync(cancellationToken);
        }

        foreach (RunDetails previous in previousRuns.Where(r => r.WorktreePath.IsNotBlank() && Directory.Exists(r.WorktreePath)))
        {
            // A local launch left standing in that earlier run's checkout comes down before the
            // checkout does (LocalLaunchProcesses' own doc).
            LocalLaunchProcesses.EndAll(previous.LocalLaunch);

            try
            {
                await worktrees.RemoveAsync(repositoryPath, previous.WorktreePath, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Worktree removal failed for {Path} (safe to prune later)", previous.WorktreePath);
            }
        }

        string? branch = task.FollowUpBranch.IsNotBlank()
            ? task.FollowUpBranch
            : task.RetryBranch.IsNotBlank()
                ? task.RetryBranch
                : previousRuns.OrderByDescending(r => r.DispatchedAt).FirstOrDefault()?.Branch;
        if (branch.IsBlank())
        {
            return;
        }

        // The same question closeout asks before it deletes anything from origin, for the same
        // reason and through the same seam (Decisions Log #186): a raw remote
        // deletion of a merged branch closes every open pull request stacked on it. GitHub has
        // usually deleted this branch itself long before a later run reaches this cleanup, in which
        // case the push would merely fail harmlessly — but "usually" is not the guarantee, and this
        // arm is the sibling of the one the decision was written for, not an exception to it.
        RemoteBranchDeletionOwner remoteDeletion =
            await closeout.RemoteBranchDeletionOwnerAsync(repositoryPath, cancellationToken);

        try
        {
            await worktrees.DeleteBranchEverywhereAsync(repositoryPath, branch, remoteDeletion, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Branch cleanup failed for {Branch} (safe to delete by hand)", branch);
        }
    }

    /// <summary>
    /// Reclaims this task's own previous pr-review worktrees, and the tracking ref each was
    /// fetched against, right before cutting a fresh checkout (adversarial review, cycle 1):
    /// a pr-review run never resumes a prior attempt's worktree the way an ordinary retry
    /// does, so every retry or requeue cuts a brand-new one, and no closeout sweep can ever
    /// reach the old ones afterward — a pr-review run carries no PullRequestNumber, so
    /// CloseoutEngine's merge closeout never watches it, and a pr-review task refuses to
    /// reopen at all. This is the only point left in the run's whole lifecycle that still can.
    /// Scoped to the task alone, never the dispatching node (independent pre-PR review, cycle
    /// 1, both lenses): auto-pr-review's now speed launches a pr-review task's first run under
    /// the ceiling-exempt Guid.Empty sentinel, and a later retry or requeue reclaims the task
    /// under this daemon's own real node id — a node-scoped query would never see that
    /// sentinel-dispatched predecessor and leak its worktree and tracking ref permanently.
    /// The one-live-task-per-pull-request rule means every run this finds is already terminal
    /// by the time a fresh one is about to launch, whichever node dispatched it.
    /// Best-effort throughout: a failed reclaim here costs nothing this dispatch needs.
    /// </summary>
    private async Task CleanUpPreviousPrReviewWorktreesAsync(
        Guid taskId, ProjectDetails project, CancellationToken cancellationToken)
    {
        IReadOnlyList<RunDetails> previousRuns;
        await using (IQuerySession query = store.QuerySession())
        {
            previousRuns = await query.Query<RunDetails>()
                .Where(r => r.TaskId == taskId)
                .ToListAsync(cancellationToken);
        }

        foreach (RunDetails previous in previousRuns)
        {
            if (previous.WorktreePath.IsNotBlank() && Directory.Exists(previous.WorktreePath))
            {
                // Same as the merged-workspace cleanup above: a launch standing in that earlier
                // pr-review checkout comes down before the checkout does.
                LocalLaunchProcesses.EndAll(previous.LocalLaunch);

                try
                {
                    await worktrees.RemoveAsync(project.RepositoryPath, previous.WorktreePath, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "Worktree removal failed for {Path} (safe to prune later)", previous.WorktreePath);
                }
            }

            // Checked for every previous run, not only one whose worktree directory still exists
            // (cycle-1 adversarial finding): the ref's lifetime is independent of the directory's —
            // FinalizeAsync already removes the worktree and only then attempts this same deletion
            // best-effort, so a run whose directory is already gone is exactly the run most likely
            // to still be carrying a ref that earlier attempt failed to clear. Deleting an
            // already-gone ref is a harmless no-op error, caught the same as any other failure here.
            if (PullRequestNumberFromPrReviewBranch(previous.Branch) is { } pullRequestNumber)
            {
                try
                {
                    await worktrees.DeletePrReviewTrackingRefAsync(project.RepositoryPath, pullRequestNumber, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(
                        exception, "Pr-review tracking ref cleanup failed for pull request #{Number} (safe to delete by hand)",
                        pullRequestNumber);
                }
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="branch"/> actually contains <paramref name="commit"/> — true, false,
    /// or null for "git could not answer", which is deliberately not the same as false (AGENTS.md's
    /// never-guess rule, and the identical three-way reading of <c>--is-ancestor</c>
    /// <c>StackedParentWatch.ObserveAsync</c> takes: git documents exit 0 as contained, 1 as not,
    /// and anything else as a failure to answer). Null and true are both treated as "keep the
    /// record" by the one caller: a failed git call is not evidence a branch is missing a commit,
    /// and discarding an honest fork point over a transient would cost this branch its boundary
    /// permanently — nothing can re-derive one later.
    /// <para>
    /// Asks the branch ref rather than <c>HEAD</c>: the claim being checked is about the branch, and
    /// a retained worktree can be sitting on a detached HEAD left by an earlier session's own
    /// interrupted rebase.
    /// </para>
    /// </summary>
    private async Task<bool?> BranchContainsCommitAsync(
        string worktreePath, string branch, string commit, CancellationToken cancellationToken)
    {
        try
        {
            ProcessResult contained = await processRunner(
                "git",
                ["merge-base", "--is-ancestor", commit, $"refs/heads/{branch}"],
                worktreePath,
                cancellationToken);
            return contained.ExitCode switch
            {
                0 => true,
                1 => false,
                _ => null,
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Could not read whether branch {Branch} in {Worktree} contains {Commit}; leaving the recorded "
                + "fork point as it was",
                branch, worktreePath, commit);
            return null;
        }
    }

    /// <summary>The pull request number out of a pr-review run's own <c>pr/&lt;n&gt;</c> branch name.</summary>
    private static int? PullRequestNumberFromPrReviewBranch(string branch) =>
        branch.StartsWith("pr/", StringComparison.Ordinal)
        && int.TryParse(branch.AsSpan(3), out int number)
            ? number
            : null;

    /// <summary>
    /// A live read of the pull request a pr-review task targets, taken fresh at every
    /// dispatch (never the task's adoption-time snapshot — a PR's base can move, and only a
    /// live read tells whether it is still open to review at all). A read failure (gh
    /// unavailable, the repository unreachable) is let through to <see cref="LaunchAsync"/>'s
    /// own catch, which fails the run with gh's own words rather than a guessed "not open".
    /// Null return means the read succeeded and the pull request is genuinely not open — a
    /// blank <see cref="TaskDetails.ExternalReference"/> is a different fact (this pr-review task
    /// carries no reference at all, which every CLI door that can create one already refuses) and
    /// throws rather than returning null, so it reaches the same catch with its own honest
    /// message instead of being reported as "is no longer open" (adversarial review, cycle 3).
    /// </summary>
    private static async Task<PullRequestFacts?> FetchOpenPullRequestFactsAsync(
        TaskDetails task, ProjectDetails project, ProcessRunner processRunner, CancellationToken cancellationToken)
    {
        if (task.ExternalReference.IsBlank())
        {
            throw new DomainBusinessRuleException(
                "This pr-review task carries no external reference to a pull request, so there is "
                + "nothing to dispatch against — every door that creates a pr-review task should have "
                + "refused it without one.");
        }

        PullRequestFacts facts = await new GitHubPullRequestProvider(processRunner).FetchFactsAsync(
            ExternalReference.Parse(task.ExternalReference).Reference, project.RepositoryPath, cancellationToken);
        return facts.State.Equals("OPEN", StringComparison.OrdinalIgnoreCase) ? facts : null;
    }

    /// <summary>
    /// The pull-request review pre-flight's own gate (idea 6be68ee2, finding 1, phase one): true
    /// only when a safe verdict is on record for this task's CURRENT head oid, in which case the
    /// caller proceeds to cut the worktree exactly as before. False means this dispatch is done —
    /// either a fresh pre-flight was just dispatched (no usable verdict for this exact head, or the
    /// latest row is unsafe or abandoned), or one is already dispatched and not yet complete (its
    /// own monitor, or a restart's adoption sweep, completes or abandons it later). Shared by
    /// <see cref="LaunchAsync"/>'s own <c>isPrReview</c> branch and
    /// <see cref="LaunchPrReviewMentionFollowUpAsync"/>: both cut a fresh checkout, and neither
    /// may do so without this gate (the acceptance criterion is explicit that both sites require
    /// it). <paramref name="isMentionFollowUp"/> is threaded straight through to
    /// <see cref="DispatchPrReviewPreflightAsync"/> so a fresh dispatch this gate triggers is
    /// tagged the same way the caller's own checkout would have been.
    /// </summary>
    private async Task<bool> EnsurePrReviewPreflightSafeAsync(
        IDocumentSession session, TaskDetails task, ProjectDetails project, PullRequestFacts facts, Guid nodeId,
        Guid runId, int leaseGeneration, bool isMentionFollowUp, CancellationToken cancellationToken)
    {
        PrReviewPreflightDetails? latest = await session.Query<PrReviewPreflightDetails>()
            .Where(preflight => preflight.TaskId == task.Id)
            .OrderByDescending(preflight => preflight.DispatchedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (latest is { CompletedAt: not null } completed && completed.HeadRefOid == facts.HeadRefOid)
        {
            // Every completed row is a genuine verdict: a budget exhaustion, a launch failure, a
            // timeout, or a dead process at restart never reaches PrReviewPreflightCompleted at
            // all (RunSupervisor.AbandonPreflightAsync leaves the row incomplete instead), so
            // there is no third "completed but not really" case to fall through here.
            if (completed.Safe)
            {
                return true;
            }

            // An unsafe verdict is never cached forever: ParkPrReviewPreflight lands the task on
            // Published, never Queued, so the ordinary dispatch loop can never reclaim it on its
            // own — the only way this gate runs again against the identical head is a human's own
            // h9k task assign. Re-parking off the stale row here, rather than judging fresh, left
            // that deliberate override unable to ever change the outcome (independent pre-PR
            // review, cycle 1, adversarial lens) and contradicted PrReviewPreflightUnsafe's own
            // doc, which promises this next dispatch's own pre-flight "either records a new, safe
            // verdict or parks again" — never a silent re-park off data the session never judged
            // this claim against.
            await DispatchPrReviewPreflightAsync(
                task, project, facts, nodeId, runId, leaseGeneration, isMentionFollowUp, cancellationToken);
            return false;
        }
        else if (latest is { CompletedAt: null, AbandonedAt: null } inFlight && inFlight.HeadRefOid == facts.HeadRefOid)
        {
            // Already dispatched for this exact head and not abandoned — its own monitor (or,
            // after a restart, the adoption sweep) completes or abandons it and releases this
            // task's lease. An abandoned row (AbandonedAt set) falls through to a fresh dispatch
            // below instead: it never will complete, and treating it as still in flight left this
            // task claimed-and-requeued forever with no pre-flight ever running again (independent
            // pre-PR review, cycle 1, both lenses).
            return false;
        }

        await DispatchPrReviewPreflightAsync(
            task, project, facts, nodeId, runId, leaseGeneration, isMentionFollowUp, cancellationToken);
        return false;
    }

    /// <summary>
    /// Reads a pull request's own changed-file list and diff hunks through <c>gh</c> and spawns
    /// the pre-flight session against them (idea 6be68ee2, finding 1, phase one) — the courier's
    /// own run-with-no-task precedent (working directory <see cref="RunPaths.GlobalDirectory"/>,
    /// <c>UntrustedWorkingDirectory</c> true, a turn cap from <see cref="DaemonOptions"/>, its own
    /// stream) rather than the ordinary <c>RunDispatched</c>/<see cref="RunAggregate"/> shape:
    /// <see cref="RunDispatched.WorktreePath"/> is required, and
    /// <see cref="CleanUpPreviousPrReviewWorktreesAsync"/> removes any recorded path — a run whose
    /// worktree is its own run directory is a trap for that sweep. Never awaited on to finish:
    /// <see cref="RunSupervisor.StartPreflightMonitoring"/> is fired and this method returns, so
    /// the dispatch loop that called <see cref="LaunchAsync"/> is never stalled behind this
    /// session (the acceptance criterion's own "without ever blocking the dispatch loop").
    /// </summary>
    private async Task DispatchPrReviewPreflightAsync(
        TaskDetails task, ProjectDetails project, PullRequestFacts facts, Guid nodeId, Guid runId,
        int leaseGeneration, bool isMentionFollowUp, CancellationToken cancellationToken,
        bool headOidFromGitFetch = false)
    {
        Guid preflightRunId = DomainId.New();
        string runDirectory = RunPaths.GlobalDirectory(preflightRunId);
        Directory.CreateDirectory(runDirectory);

        string reference = ExternalReference.Parse(task.ExternalReference).Reference;
        GitHubPullRequestProvider ghProvider = new(processRunner);

        // The head oid this verdict is about to be recorded against (facts.HeadRefOid) is read by
        // gh pr view earlier and separately from the gh pr diff calls below — two independent
        // observations that can disagree if the head moved in between. Cycle 1's own fix here
        // (06714e1de) reread the head once after the diff fetch and adopted the newer oid, but kept
        // the diff already fetched under the older one, so a single push landing in that window
        // still recorded a safe verdict against content this pre-flight never actually read
        // (independent pre-PR review, cycle 3, adversarial lens). Rereading alone can never make
        // facts.HeadRefOid describe the diff in hand — only rereading the diff itself does, so a
        // disagreement here discards the diff and refetches it under the newer oid instead of
        // silently swapping facts underneath unchanged content. Bounded, rather than looped forever,
        // for the pathological case of a head that keeps moving every time this checks it. Skipped
        // entirely when headOidFromGitFetch is true (both PullRequestHeadMovedException catches pass
        // it): that oid already comes from a direct git fetch of refs/pull/<n>/head, strictly fresher
        // than a second gh pr view could ever confirm, so asking gh again there would only replace a
        // more-authoritative reading with a less fresh one.
        const int maxHeadRereadAttempts = 3;
        IReadOnlyList<string> changedFiles;
        string diff;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                changedFiles = await ghProvider.FetchChangedFileNamesAsync(reference, runDirectory, cancellationToken);
                diff = await ghProvider.FetchDiffAsync(reference, runDirectory, cancellationToken);
            }
            catch (DomainException exception)
            {
                // GitHub's own diff endpoint refuses a pull request over its file-count ceiling
                // outright (documented at 300 files) — with nothing to judge, this pre-flight cannot
                // reach a verdict at all, so the task parks as needs-you exactly as an unparseable
                // session verdict already does, rather than failing the launch permanently on a pull
                // request that can never shrink back under the ceiling on its own (independent pre-PR
                // review, cycle 1, adversarial lens: a dependency bump, a generated-code refresh, or a
                // rename sweep touching more than 300 files could otherwise never be reviewed again).
                if (GitHubDiffSizeLimitClassifier.IsDiffTooLarge(exception.Message))
                {
                    await ParkUnreadableDiffPreflightAsync(
                        task.Id, facts,
                        $"gh could not read this pull request's diff, so the pre-flight has nothing to judge: "
                        + $"{exception.Message}",
                        cancellationToken);
                    return;
                }

                await RecordLaunchFailureAsync(task.Id, runId, leaseGeneration, exception.Message, cancellationToken);
                return;
            }

            if (headOidFromGitFetch)
            {
                break;
            }

            PullRequestFacts recheckedFacts;
            try
            {
                recheckedFacts = await ghProvider.FetchFactsAsync(reference, runDirectory, cancellationToken);
            }
            catch (DomainException exception)
            {
                await RecordLaunchFailureAsync(task.Id, runId, leaseGeneration, exception.Message, cancellationToken);
                return;
            }

            if (recheckedFacts.HeadRefOid == facts.HeadRefOid)
            {
                break;
            }

            if (attempt >= maxHeadRereadAttempts)
            {
                await RecordLaunchFailureAsync(
                    task.Id, runId, leaseGeneration,
                    $"{task.ExternalReference}'s head kept moving while the pre-flight was reading its diff "
                    + $"(last seen {facts.HeadRefOid}, then {recheckedFacts.HeadRefOid}) — giving up after "
                    + $"{maxHeadRereadAttempts} attempts rather than recording a verdict for a diff this "
                    + "pre-flight never actually read against the head it judged.",
                    cancellationToken);
                return;
            }

            logger.LogWarning(
                "Task {TaskId}: the pull request's head read as {ExpectedHeadOid} earlier but "
                + "{ObservedHeadOid} just now, after the diff fetch — refetching the diff against the head "
                + "actually observed instead of recording a verdict for stale content (attempt {Attempt})",
                task.Id, facts.HeadRefOid, recheckedFacts.HeadRefOid, attempt);
            facts = recheckedFacts;
        }

        IReadOnlyList<string> surfaces = PrReviewPreflightSurfaceMatcher.Match(changedFiles);
        string matchedHunks = PrReviewPreflightDiffExtractor.ExtractMatchedHunks(diff, surfaces);
        string prompt = PrReviewPreflightPromptBuilder.Build(reference, facts.Url, changedFiles, surfaces, matchedHunks);

        AgentModel model = options.Value.ResolveSecurityPreflightModel(project.Model);
        AgentEffort effort = options.Value.ResolveEffort(AgentRole.SecurityPreflight, taskEffort: null, project.Effort);

        await using (IDocumentSession dispatchSession = store.LightweightSession())
        {
            dispatchSession.Events.StartStream(preflightRunId, new PrReviewPreflightDispatched(
                preflightRunId, task.Id, nodeId, model, facts.HeadRefOid, surfaces, DateTimeOffset.UtcNow,
                isMentionFollowUp));
            await dispatchSession.SaveChangesAsync(cancellationToken);
        }

        SpawnedAgent agent;
        try
        {
            agent = await executor.SpawnAsync(
                new AgentSpawnRequest(
                    preflightRunId, preflightRunId, runDirectory, runDirectory, prompt, ExecutorMode.Subscription,
                    model, effort, SkipPermissions: false,
                    UntrustedWorkingDirectory: true,
                    MaxTurns: options.Value.SecurityPreflightMaxTurns)
                {
                    TaskId = task.Id,
                    SessionName = SessionRoleName.For(DomainId.Short(task.Id), SessionRoleName.SecurityPreflight),
                    UsesReviewPermissions = true,
                },
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                exception, "Task {TaskId}: the pull-request review pre-flight could not be spawned", task.Id);
            // The identical orphan-row gap AbandonPreflightAsync's own doc closes for a timeout or
            // a budget exhaustion (independent pre-PR review, cycle 1, both lenses): without this,
            // PrReviewPreflightDispatched just above already committed, so the row sits incomplete
            // and un-abandoned forever, and EnsurePrReviewPreflightSafeAsync reads it as still in
            // flight on every later h9k task retry.
            await using (IDocumentSession abandonSession = store.LightweightSession())
            {
                abandonSession.Events.Append(
                    preflightRunId, new PrReviewPreflightAbandoned(preflightRunId, DateTimeOffset.UtcNow));
                await abandonSession.SaveChangesAsync(cancellationToken);
            }

            await RecordLaunchFailureAsync(task.Id, runId, leaseGeneration, exception.Message, cancellationToken);
            return;
        }

        await using (IDocumentSession startSession = store.LightweightSession())
        {
            startSession.Events.Append(
                preflightRunId, new PrReviewPreflightProcessStarted(preflightRunId, agent.ProcessId, agent.StartedAt));
            await startSession.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation(
            "Task {TaskId}: pull-request review pre-flight dispatched (pid {ProcessId}, model {Model}, "
            + "{SurfaceCount} matched surface(s) of {ChangedCount} changed file(s))",
            task.Id, agent.ProcessId, model.Value, surfaces.Count, changedFiles.Count);

        supervisor.StartPreflightMonitoring(preflightRunId, task.Id, agent.ProcessId, agent.StartedAt, cancellationToken);
    }

    /// <summary>
    /// Parks a pr-review task as needs-you when gh itself refused to hand the pre-flight anything
    /// to judge (<see cref="GitHubDiffSizeLimitClassifier"/>) — no pre-flight session ever ran, so
    /// nothing is appended to a <see cref="PrReviewPreflightDetails"/> stream here; the task's own
    /// stream carries the whole story via the identical <see cref="PrReviewPreflightParked"/>
    /// event a genuine unsafe verdict uses, so <c>AttentionComposer</c> renders one park card
    /// either way.
    /// </summary>
    private async Task ParkUnreadableDiffPreflightAsync(
        Guid taskId, PullRequestFacts facts, string reason, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        TaskAggregate? task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken);
        if (task is null || task.State != TaskState.Claimed)
        {
            return;
        }

        session.Events.Append(taskId, TaskDecider.ParkPrReviewPreflight(
            task, surfaces: [], facts.HeadRefOid, "unreadable", reason, DateTimeOffset.UtcNow));
        session.Delete<TaskLease>(taskId);
        await session.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "Task {TaskId}: parked as needs-you — the pull-request review pre-flight could not read this pull "
            + "request's diff ({Reason})", taskId, reason);
    }

    /// <summary>
    /// Best-effort <c>owner/repo</c> out of a project's registered or observed repository URL,
    /// for the pr-review foreign-repository guard above. Unlike
    /// <c>GitHubWorkItemProvider</c>'s own <c>RepositoryFrom</c>, this never throws: a URL that
    /// does not parse as a github.com owner/repo means nothing to compare against, not a reason
    /// to fail the launch over a check that is a courtesy rather than a hard requirement.
    /// </summary>
    // Internal rather than private: AutoPrReviewEngine needs the identical owner/repo parse to
    // resolve which repository gh's own review-requested search runs against, and two copies of
    // this rule are two rules that drift.
    internal static string? OwnerRepoFrom(Uri? repositoryUrl) =>
        repositoryUrl is not null
        && repositoryUrl.AbsolutePath.Trim('/').Split('/') is [{ Length: > 0 } owner, { Length: > 0 } repository, ..]
            ? $"{owner}/{TrimGitSuffix(repository)}"
            : null;

    private static string TrimGitSuffix(string repository) =>
        repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? repository[..^4]
            : repository;

    /// <summary>
    /// The primary session's model and effort: the Security persona's own floor
    /// (<c>DaemonOptions.ResolveSecurityReviewModel</c>/<c>ResolveSecurityReviewEffort</c>) when
    /// the plan's first session is that persona's, <paramref name="fallbackModel"/> and
    /// <paramref name="fallbackEffort"/> — already resolved for the ordinary Review or Build role
    /// — otherwise. Extracted as a pure function, rather than inlined at the one call site, so
    /// this floor is unit-testable directly against a synthetic Security-led plan even though
    /// today's <c>ReviewPersonaRegistry.Plan</c> can never actually produce one (the courier
    /// precedent this persona otherwise shares must never silently regress unnoticed).
    /// </summary>
    // Internal rather than private: unit-tested directly against every ReviewPersona, including
    // the Security-led shape the registry itself cannot produce today.
    internal static (AgentModel Model, AgentEffort Effort) ResolvePrimarySessionModel(
        ReviewPersona firstSessionPersona, AgentModel? taskModel, AgentModel? projectModel, AgentEffort? taskEffort,
        AgentEffort? projectEffort, DaemonOptions options, AgentModel fallbackModel, AgentEffort fallbackEffort) =>
        firstSessionPersona == ReviewPersona.Security
            ? (options.ResolveSecurityReviewModel(taskModel, projectModel),
                options.ResolveSecurityReviewEffort(taskEffort, projectEffort))
            : (fallbackModel, fallbackEffort);

    /// <summary>
    /// A retried task resumes its failed run's branch through the same checkout path
    /// follow-up runs use — the retained worktree, or a fresh worktree on the surviving
    /// branch. When the branch is gone everywhere, the retry starts clean from the base
    /// branch instead of failing the run (Decisions Log #25). The flag reports which
    /// path won, so the prompt tells a resuming agent to review the previous attempt's
    /// work — possibly uncommitted in the retained worktree — before starting over.
    /// <para>
    /// A branch gone from both sides starts clean whoever's it was, this node's own earlier
    /// attempt or another node's (#234, narrowing Decisions Log #224). What #224
    /// actually protects is a foreign node's WORK, and a branch that reached neither origin nor
    /// this repository never brought any here to abandon — the failure it was written against
    /// (2026-09-19, task a56cf16e, runs 01a0baf1 and 01a0baf3, after a forced take from a Mac run
    /// that had built eleven minutes without pushing) was two retries dying at launch over a
    /// branch nothing on this node could ever have found. The loud failure stays for every OTHER
    /// <see cref="WorktreeException"/> on a foreign-node resume — a <c>worktree add</c> that
    /// failed, an unreadable repository — because those are a machine that could not do the job,
    /// where a fresh cut really would discard work still sitting there.
    /// </para>
    /// </summary>
    /// <param name="baseBranch">
    /// The resolved base this run sits on — the project's own for every ordinary task, a stacked
    /// child's parent branch instead (<see cref="StackedBaseResolver"/>). A retry resuming its own
    /// previous branch never consults it: that branch was already cut from whatever base the earlier
    /// attempt resolved, and the retry is continuing that work rather than re-basing it.
    /// </param>
    /// <returns>
    /// The checkout, whether it resumed previous work, and — only when a resume was meant and the
    /// branch was gone — the event recording that this run started clean instead, for the caller
    /// to append alongside <see cref="RunDispatched"/>.
    /// </returns>
    private async Task<(Worktree Worktree, bool ResumesPreviousWork, RunStartedCleanAfterBranchGone? StartedClean)>
        CheckoutFreshOrRetryAsync(
            TaskDetails task, ProjectDetails project, string baseBranch, Guid taskId, Guid runId,
            CancellationToken cancellationToken)
    {
        RunStartedCleanAfterBranchGone? startedClean = null;
        if (task.RetryBranch.IsNotBlank())
        {
            try
            {
                return (await worktrees.CheckoutExistingAsync(
                    new FollowUpWorktreeRequest(project.RepositoryPath, task.RetryBranch, taskId, runId),
                    cancellationToken), true, null);
            }
            // Only the branch-is-gone-everywhere failure falls through to a fresh cut on a
            // foreign-node resume; every other worktree failure keeps propagating there, through
            // this method's caller into LaunchAsync's own catch, and fails the run by name.
            catch (WorktreeException exception)
                when (exception is BranchGoneException || !task.RetryBranchResumesForeignNode)
            {
                logger.LogInformation(
                    "Retry of task {TaskId} cannot resume branch {Branch} ({Reason}); starting clean from {BaseBranch}",
                    taskId, task.RetryBranch, exception.Message, baseBranch);
                // Recorded only for the failure that actually observed the branch on neither side.
                // The ordinary retry's OTHER fallback — log #25's, a machine that could not do the
                // job on a branch of this node's own — reaches here too, and there the branch and
                // its commits are still in the repository: saying "gone" about it would be the
                // audit trail guessing at a gap nobody observed (AGENTS.md's never-guess rule;
                // both review lenses, cycle 1). That path stays exactly as it was, a fresh cut
                // and this log line.
                if (exception is BranchGoneException gone)
                {
                    startedClean = new RunStartedCleanAfterBranchGone(
                        runId, gone.Branch, baseBranch, exception.Message,
                        task.RetryBranchResumesForeignNode, DateTimeOffset.UtcNow);
                }
            }
        }

        return (await worktrees.CreateAsync(
            new WorktreeRequest(
                project.RepositoryPath, baseBranch, taskId, runId, task.Objective,
                project.BranchNameTemplate, task.ExternalReference),
            cancellationToken), false, startedClean);
    }

    /// <summary>
    /// The "log names an over-cap addendum whenever it appends one" half of criterion 3 (idea
    /// b9b09779, piece 6): <see cref="AgentPromptBuilder"/> and <see cref="MentionFollowUpPromptBuilder"/>
    /// are static and logger-free by design (PLAN.md's own entry for this feature), so the one line
    /// this logs at this shared dispatch point, once a run's prompt has already been composed, is
    /// what satisfies that clause for every session THIS launch path dispatches — rather than
    /// threading an <see cref="ILogger"/> through every one of those builders' own <c>Build*</c>
    /// methods for a fact this call site can already read straight off the project. It is not the
    /// only dispatcher that splices one of these addenda: <c>ReviewEngine</c>, <c>PrReviewEngine</c>,
    /// and <c>CardPublicationEngine</c> each compose and dispatch their own prompts through
    /// <see cref="AgentPromptBuilder"/> directly, never through <see cref="RunLauncher"/>, so none of
    /// them ever reaches this log line. For those, the criterion is satisfied purely by the in-prompt
    /// heading annotation every splice already carries — the only "log" a pure, logger-free static
    /// builder has (independent pre-PR review, cycle 1, conformance lens, low: this comment's own
    /// "every daemon-dispatched session" previously overclaimed daemon-wide coverage this one line
    /// does not actually have).
    /// </summary>
    private void LogIfOverCapAddendum(Guid runId, ProjectDetails project, PromptBuilderKey builder)
    {
        if (ProjectPromptAddendaLoader.TryLoad(project, builder) is { OverCap: true })
        {
            logger.LogInformation(
                "Run {RunId}: composed with project {ProjectId}'s own {Builder} prompt addendum, set over "
                + "this project's usual length cap",
                runId, project.Id, builder.Value);
        }
    }

    private async Task RecordLaunchFailureAsync(
        Guid taskId, Guid runId, int leaseGeneration, string reason, CancellationToken cancellationToken)
    {
        try
        {
            await using IDocumentSession session = store.LightweightSession();
            if (await session.Events.FetchStreamStateAsync(runId, cancellationToken) is not null)
            {
                session.Events.Append(runId, new RunFailed(runId, reason, DateTimeOffset.UtcNow));
            }

            // LoadFencedAsync's read must happen before the AllowsAsync identity check below
            // — not after — so a reclaim landing between the two is caught by AllowsAsync's
            // fresh read rather than baked into `current.Task` as an already-stale ownership
            // fact that AllowsAsync never gets asked about (adversarial review, cycle 2).
            (TaskAggregate Task, long Version)? fenced =
                await GenerationFence.LoadFencedAsync(session, taskId, cancellationToken);
            if (!await GenerationFence.AllowsAsync(
                session, logger, taskId, runId, leaseGeneration, nameof(TaskFailed), cancellationToken))
            {
                await session.SaveChangesAsync(cancellationToken);
                return;
            }

            if (fenced is { } current && TaskDecider.CanFail(current.Task))
            {
                session.Events.Append(taskId, expectedVersion: current.Version + 1, TaskDecider.Fail(
                    current.Task, runId, $"Launch failed: {reason}", DateTimeOffset.UtcNow));
            }

            session.Delete<TaskLease>(taskId);
            try
            {
                await session.SaveChangesAsync(cancellationToken);
            }
            catch (EventStreamUnexpectedMaxEventIdException)
            {
                // A claim landed between LoadFencedAsync and this commit; the live
                // generation owns the task now and this stale failure must not land.
                logger.LogInformation(
                    "Task {TaskId}: lost the generation race recording a launch failure — a newer claim committed first",
                    taskId);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to record launch failure for run {RunId}", runId);
        }
    }
}

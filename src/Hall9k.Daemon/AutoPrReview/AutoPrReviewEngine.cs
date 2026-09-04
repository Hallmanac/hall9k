using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Text;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Extensions;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Marten.Linq.MatchesSql;

namespace Hall9k.Daemon.AutoPrReview;

/// <summary>
/// One sweep's tally, the <see cref="Hall9k.Daemon.Closeout.CloseoutSweepResult"/> shape applied
/// to this feature: <see cref="ProjectsFailed"/> feeds the monitor's own backoff decision exactly
/// as that sibling's <c>Failures</c> does, keyed the identical way — a project this sweep could
/// not reach at all (no repository resolved, gh itself unreachable) counts, one project's own
/// unrelated trouble (a single unreadable pull request) does not widen the interval for every
/// other opted-in project's own healthy reads.
/// </summary>
public sealed record AutoPrReviewSweepResult(
    int ProjectsInspected, int ProjectsFailed, int TasksCreated, int AssignmentsRecalled);

/// <summary>
/// The auto-pr-review core (idea e5e98a33, PLAN.md §16 decision #34's amendment): for every
/// project opted in with <c>h9k project set --auto-pr-review</c>, asks GitHub which open pull
/// requests in that project's repo currently request this install's own login — read back from
/// GitHub every sweep, never a configured or cached name — and for each one with no live task
/// already watching it, mints, publishes, and starts a pr-review task exactly as
/// <c>h9k task add --from-pr</c> would, at the project's chosen speed. The reviewer assignment on
/// GitHub is the go signal; there is no scheduling code here beyond the three general dispatch
/// levers this feature deliberately builds nothing new on top of: the ordinary claim rotation, the
/// queue-first marker (Decisions Log #127), and the ceiling-exempt claim <c>h9k task start</c>
/// already uses (Decisions Log #103, #125).
/// <para>
/// The same sweep also watches every non-terminal task it previously auto-created
/// (<see cref="TaskListItem.AutoPrReviewAssigneeLogin"/> non-null) for the mirror image: a
/// reviewer request GitHub no longer reports. Before the run ever dispatches (Published or
/// Queued), that concludes the task honestly — the go signal recalled by the same authority that
/// gave it. Once the run is Claimed or parked, it is recorded as an observation only; the work,
/// and any findings already produced, are never discarded for a reviewer reshuffle.
/// </para>
/// </summary>
public sealed class AutoPrReviewEngine(
    IDocumentStore store,
    NodeContext node,
    RunLauncher launcher,
    ProcessRunner processRunner,
    ILogger<AutoPrReviewEngine> logger)
{
    private static readonly string[] TerminalStates =
        [TaskState.Done.Value, TaskState.Abandoned.Value];

    /// <summary>
    /// At most one ceiling-exempt launch per sweep (Decisions Log #64's own origin OOM,
    /// independent pre-PR review cycle 1, conformance and adversarial lenses both): the consent
    /// text a human agrees to at <c>h9k project set --auto-pr-review now</c>
    /// (<c>ProjectSetCommand.AutoPrReviewConsequence</c>) promises "an extra concurrent agent
    /// session", singular, and a single sweep iterating every currently-requested pull request
    /// across every opted-in project with no cap at all could otherwise start as many ceiling-exempt
    /// sessions as there are open requests in one tick. A candidate beyond the cap is not dropped —
    /// it is minted, published and assigned exactly as a <c>First</c>-speed task is, so it still
    /// takes the next free ordinary dispatch slot rather than waiting a full poll interval for
    /// nothing to happen.
    /// </summary>
    private const int MaxImmediateLaunchesPerSweep = 1;

    private readonly GitHubReviewAssignments reviewAssignments = new(processRunner);

    private int _immediateLaunchesThisSweep;

    public async Task<AutoPrReviewSweepResult> PollOnceAsync(CancellationToken cancellationToken)
    {
        // Sequential ticks only (AutoPrReviewMonitor awaits one PollOnceAsync before starting the
        // next), so a plain field is safe here without any locking.
        _immediateLaunchesThisSweep = 0;

        IReadOnlyList<ProjectDetails> optedIn;
        await using (IQuerySession query = store.QuerySession())
        {
            // Fetched in full and filtered here rather than through MatchesSql: AutoPrReviewSpeed
            // is a value object behind a JsonConverter like every other one in this codebase, and
            // the project count on any real install is small enough that a client-side filter
            // costs nothing an index would meaningfully save.
            optedIn = [.. (await query.Query<ProjectDetails>().ToListAsync(cancellationToken))
                .Where(project => project.AutoPrReview != AutoPrReviewSpeed.Off)];
        }

        int inspected = 0;
        int failed = 0;
        int created = 0;
        int recalled = 0;
        foreach (ProjectDetails project in optedIn)
        {
            inspected++;
            try
            {
                (int projectCreated, int projectRecalled) = await SweepProjectAsync(project, cancellationToken);
                created += projectCreated;
                recalled += projectRecalled;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed++;
                logger.LogWarning(
                    exception, "Auto-pr-review sweep failed for project {Project}; will retry next tick", project.Name);
            }
        }

        return new AutoPrReviewSweepResult(inspected, failed, created, recalled);
    }

    private async Task<(int Created, int Recalled)> SweepProjectAsync(ProjectDetails project, CancellationToken cancellationToken)
    {
        Uri? repositoryUrl = project.RepositoryUrl
            ?? await new GitHubWorkItemProvider(processRunner).TryObserveRepositoryHostAsync(project.RepositoryPath, cancellationToken);
        string repository = RunLauncher.OwnerRepoFrom(repositoryUrl)
            ?? throw new InvalidOperationException(
                $"Could not resolve which GitHub repository {project.Name} is ({project.RepositoryPath}) — "
                + "neither the project's own recorded URL nor gh repo view named one.");

        string login = await reviewAssignments.CurrentLoginAsync(project.RepositoryPath, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Could not read gh's authenticated login from {project.RepositoryPath} — gh may not be "
                + "installed or signed in on this node.");

        IReadOnlyList<ReviewRequestedPullRequest> currentlyRequested =
            await reviewAssignments.ListReviewRequestedAsync(repository, login, project.RepositoryPath, cancellationToken);

        int created = await CreateNewlyAssignedAsync(project, repository, login, currentlyRequested, cancellationToken);
        int recalled = await ConcludeWithdrawnAsync(project, repository, login, currentlyRequested, cancellationToken);
        return (created, recalled);
    }

    /// <summary>
    /// One task per currently review-requested pull request this install has no live task
    /// watching yet — the mirror of <c>TaskAddCommand.RefuseSecondAdoptionAsync</c>'s own dedup
    /// query, since a manually-adopted <c>--from-pr</c> task and an auto-created one share the
    /// identical one-per-item rule (PLAN.md §3.1a): a Done pr-review does not block a fresh
    /// adoption (a completed review does not hold its pull request hostage), so a re-request
    /// after an earlier auto-created review closed mints a fresh task, honestly noted as a
    /// re-review rather than silently indistinguishable from the first one.
    /// <para>
    /// The dedup check runs on the canonical reference the import itself returns
    /// (<see cref="ImportedWorkItem.Reference"/>), never on a reference guessed from
    /// <paramref name="repository"/> before importing — the same discipline
    /// <c>TaskAddCommand.AdoptAsync</c> already follows, and for the identical reason:
    /// <paramref name="repository"/> is parsed from this project's own recorded repository URL,
    /// which is under no obligation to match GitHub's own canonical casing, while every task this
    /// engine has ever created stores the import's own canonical form. A dedup check keyed on a
    /// mismatched guess would never find its own prior task and re-mint a duplicate every sweep —
    /// exactly the failure the one-live-task-per-pull-request rule exists to prevent.
    /// </para>
    /// </summary>
    private async Task<int> CreateNewlyAssignedAsync(
        ProjectDetails project, string repository, string login,
        IReadOnlyList<ReviewRequestedPullRequest> currentlyRequested, CancellationToken cancellationToken)
    {
        int created = 0;
        foreach (ReviewRequestedPullRequest candidate in currentlyRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using IDocumentSession session = store.LightweightSession();
            try
            {
                created += await CreateOneAsync(session, project, repository, candidate, login, cancellationToken);
            }
            catch (DomainException exception)
            {
                // A race with GitHub itself (closed or merged between the search and the
                // import) or a genuinely unreadable pull request: logged and skipped rather
                // than failing the whole project's sweep, since every other candidate this
                // project offered is unrelated to this one's own trouble.
                logger.LogWarning(
                    exception, "Auto-pr-review could not adopt {Repository}#{Number}; skipping this poll",
                    repository, candidate.Number);
            }
        }

        return created;
    }

    private async Task<int> CreateOneAsync(
        IDocumentSession session, ProjectDetails project, string repository, ReviewRequestedPullRequest candidate,
        string login, CancellationToken cancellationToken)
    {
        // A cheap fast path in front of the gh pr view subprocess the import below always pays
        // (independent pre-PR review, cycle 1, conformance lens, low): the overwhelmingly common
        // case on every sweep after the first is "a live task already covers this pull request",
        // and a case-insensitive match against the reference guessed from repository — never
        // gh's own canonical casing, per the discipline the canonical dedup check below still
        // enforces — catches it without ever shelling out. A guess that finds nothing here is not
        // trusted as "nothing exists": it is only a fast path in front of the canonical check,
        // never a replacement for it, so the import and the exact-match dedup still run
        // regardless of what this finds.
        string guessedReference = $"{WorkItemProvider.GitHubPullRequest.Value}:{repository}#{candidate.Number}";
        bool likelyAlreadyCovered = await session.Query<TaskListItem>()
            .Where(task => task.MatchesSql("lower(d.data ->> 'externalReference') = lower(?)", guessedReference))
            .Where(task => task.MatchesSql("d.data ->> 'state' <> ?", TaskState.Abandoned.Value))
            .Where(task => task.MatchesSql(
                "NOT (d.data ->> 'type' = ? AND d.data ->> 'state' = ?)",
                TaskType.PrReview.Value, TaskState.Done.Value))
            .AnyAsync(cancellationToken);
        if (likelyAlreadyCovered)
        {
            return 0;
        }

        // processRunner threaded through explicitly (independent pre-PR review, cycle 1,
        // adversarial lens): ImporterAsync's own default construction ignores whatever runner it
        // is handed unless asked, which silently shells to the real gh underneath this engine's
        // own injected ProcessRunner and left this whole mint path unreachable by a scripted test.
        WorkItemImporter importer = await WorkItemConnections.ImporterAsync(session, cancellationToken, processRunner: processRunner);
        ImportedWorkItem imported = await importer.ImportAsync(
            new WorkItemImportRequest(WorkItemProvider.GitHubPullRequest, $"{repository}#{candidate.Number}", project.RepositoryPath),
            cancellationToken);

        string canonical = imported.Reference.ToString();
        TaskListItem? existing = await session.Query<TaskListItem>()
            .Where(task => task.ExternalReference == canonical)
            .Where(task => task.MatchesSql("d.data ->> 'state' <> ?", TaskState.Abandoned.Value))
            .Where(task => task.MatchesSql(
                "NOT (d.data ->> 'type' = ? AND d.data ->> 'state' = ?)",
                TaskType.PrReview.Value, TaskState.Done.Value))
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return 0;
        }

        // Both terminal states, not Done alone (independent pre-PR review, cycle 1, both
        // lenses): an operator who abandons an auto-created review the standing request never
        // cleared must have that stick, exactly like a Done one does, or the very next sweep
        // re-mints it — h9k task abandon cannot decline an auto-created review at all while the
        // request stands otherwise. IsGenuineReRequestAsync below still lets a real re-request
        // through either way; only the same-standing-request case is what this guards.
        // WasAutoPrReviewCreated, not AutoPrReviewAssigneeLogin (independent pre-PR review, cycle
        // 2, adversarial lens): that field is transient and goes null on any recall, even a
        // "work continues" one recorded while this same task's run kept going to Done — reusing
        // it here would silently drop the re-review note for a task recalled mid-run once a real
        // re-request later arrives, because by then AutoPrReviewAssigneeLogin already reads null
        // for reasons unrelated to provenance. WasAutoPrReviewCreated is set once and never
        // cleared, so this only ever finds a task auto-pr-review itself minted — never a task a
        // human created by hand with h9k task add --from-pr, whose own abandonment or closeout
        // says nothing about whether this feature already covered any request at all (independent
        // pre-PR review, cycle 1, adversarial lens: the note below states "auto-created" and this
        // query is what has to make that true rather than assumed).
        TaskListItem? previousReview = await session.Query<TaskListItem>()
            .Where(task => task.ExternalReference == canonical)
            .Where(task => task.WasAutoPrReviewCreated)
            .Where(task => task.MatchesSql(
                "d.data ->> 'type' = ? AND d.data ->> 'state' IN (?, ?)",
                TaskType.PrReview.Value, TaskState.Done.Value, TaskState.Abandoned.Value))
            .OrderByDescending(task => task.AddedAt)
            .FirstOrDefaultAsync(cancellationToken);

        ReviewRequestActor actor = await reviewAssignments.FindMostRecentRequestActorAsync(
            OwnerFrom(repository), NameFrom(repository), candidate.Number, login,
            ReviewTimelineEventKind.Requested, project.RepositoryPath, cancellationToken);

        if (previousReview is not null
            && !await IsGenuineReRequestAsync(session, previousReview, actor.RequestedAt, cancellationToken))
        {
            // Still the same standing request an earlier auto-created review already covered —
            // not a re-review, just a reviewer request GitHub never cleared (walk-pr-review-findings
            // can end with nothing posted, or review resolve can close the task without a GitHub
            // review ever being submitted). Minting again here on every later sweep is the
            // infinite re-mint loop both review lenses found (independent pre-PR review, cycle 1).
            logger.LogDebug(
                "Auto-pr-review skipped {Repository}#{Number}: task {TaskId} already reviewed this same "
                + "standing request", repository, candidate.Number, DomainId.Short(previousReview.Id));
            return 0;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string objective = RelayedText.WithoutClosingKeywords(RelayedText.OneLine(imported.Title)).Trim() is { Length: > 0 } seed
            ? seed
            : $"Review pull request {imported.Reference.Key}";

        string provenance = actor.Login is { } assigner
            ? $"GitHub reviewer assignment observed: {assigner} requested {login} as a reviewer"
              + (actor.RequestedAt is { } at ? $" at {at:yyyy-MM-dd HH:mm:ss}Z" : string.Empty) + "."
            : $"GitHub reviewer assignment observed: {login} was requested as a reviewer (the actor who "
              + "requested it could not be read from GitHub's own timeline).";
        // Never previousReview.AddedAt (independent pre-PR review, cycle 1, both lenses):
        // TaskListItem.AddedAt is written once, at TaskAdded, and no Apply ever moves it — it is
        // the earlier task's creation time, not the date it closed or was abandoned, and stating
        // it as the closing date is exactly the plausible-but-unobserved fill-in AGENTS.md's
        // never-guess-at-unobserved-facts rule forbids. TaskListItem carries no close/abandon
        // timestamp to report instead, so the honest fix is to say what is actually observed —
        // the task id and its outcome — and leave the date out rather than mislabel one.
        // Never "an earlier request" (independent pre-PR review, cycle 4, both lenses):
        // control reaches here either because IsGenuineReRequestAsync compared timestamps and
        // found this one postdates the one previousReview was minted from (genuinely a later
        // request), or because previousReview's own stream predates
        // PullRequestReviewAssignmentObserved.RequestedAt and there was nothing to compare
        // against at all (the method's own documented conservative fallback) — a case this note
        // cannot tell apart from the first without re-reading the stream itself, so it states
        // only what is true either way: that an earlier auto-created task existed for this pull
        // request, not that the request it existed for was necessarily a distinct, earlier one.
        // Never "already reviewed this pull request" either (independent pre-PR review, cycle 6,
        // adversarial lens): the most common way an auto-created task reaches Abandoned is
        // ConcludeOneAsync's own pre-dispatch recall, where the task never ran and reviewed
        // nothing, and a Done task can equally be one h9k task resolve closed on a human's
        // attestation with no findings report ever produced. TaskListItem carries no field that
        // says whether a review actually happened, only that the task existed and how it ended —
        // stating "reviewed" would be the same plausible-but-unobserved fill-in the AddedAt
        // comment above already refuses to make.
        string? reReviewNote = previousReview is not null
            ? previousReview.State == TaskState.Abandoned
                ? $"This is a re-review: an earlier auto-created task ({DomainId.Short(previousReview.Id)}) "
                  + "existed for this pull request and was abandoned."
                : $"This is a re-review: task {DomainId.Short(previousReview.Id)} existed for this "
                  + "pull request and closed Done."
            : null;
        string additionalContext = reReviewNote is null ? provenance : $"{provenance}\n{reReviewNote}";
        // Exactly as h9k task add --from-pr does (independent pre-PR review, cycle 1, conformance
        // lens: the two adoption paths had drifted apart, and an auto-created review carried
        // strictly less to check the diff against than an identical hand-created one).
        string? linkedContext = await LinkedWorkItemImport.TryImportContextAsync(
            session, project, imported, cancellationToken, processRunner: processRunner);
        string composedAdditional = linkedContext.IsNotBlank()
            ? $"{linkedContext}\n\n{additionalContext}"
            : additionalContext;
        string agentContext = WorkItemContext.Compose(imported, composedAdditional);

        string[] criteria =
        [
            "The findings report is walked with the owner (walk-pr-review-findings) and every finding is directed.",
        ];

        Guid taskId = DomainId.New();
        TaskAdded added = TaskDecider.Add(
            taskId, project.Id, objective, criteria, TaskType.PrReview, agentContext, constraints: null,
            imported.Reference, now, node.OwnerId, model: null, blockedBy: null, sourceIdeaId: null, epicId: null);

        PullRequestReviewAssignmentObserved observed = new(
            taskId, imported.Url?.ToString() ?? candidate.Url, login, actor.Login, now, actor.RequestedAt);

        TaskAggregate task = new();
        task.Apply(added);
        task.Apply(observed);

        List<object> events = [added, observed];

        TaskPublished published = TaskDecider.Publish(
            task, TaskDependencyGraph.Empty, now, node.OwnerId, project.BacklogPolicy);
        task.Apply(published);
        events.Add(published);

        TaskAssigned assigned = TaskDecider.Assign(task, node.OwnerId, dependencies: [], now, node.OwnerId);
        task.Apply(assigned);
        events.Add(assigned);

        // A Now-speed candidate beyond this sweep's own immediate-launch cap is not silently
        // downgraded: it still takes the queue-first marker First speed uses, so it takes the
        // next free ordinary dispatch slot rather than sitting until the next poll interval.
        bool launchImmediately = project.AutoPrReview == AutoPrReviewSpeed.Now
            && ++_immediateLaunchesThisSweep <= MaxImmediateLaunchesPerSweep;

        Guid? deliberateRunId = null;
        int? deliberateLeaseGeneration = null;
        if (project.AutoPrReview == AutoPrReviewSpeed.First
            || (project.AutoPrReview == AutoPrReviewSpeed.Now && !launchImmediately))
        {
            if (project.AutoPrReview == AutoPrReviewSpeed.Now)
            {
                logger.LogInformation(
                    "Auto-pr-review deferred {Repository}#{Number} to the ordinary queue-first slot — "
                    + "this sweep already used its one immediate ceiling-exempt launch", repository, candidate.Number);
            }

            TaskRevised revised = TaskDecider.Revise(
                task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
                Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None,
                now, node.OwnerId, Optional<Guid?>.None, Optional<bool>.Of(true));
            task.Apply(revised);
            events.Add(revised);
        }
        else if (launchImmediately)
        {
            deliberateRunId = DomainId.New();
            TaskClaimed claimed = TaskDecider.ClaimDeliberately(
                task, node.OwnerId, deliberateRunId.Value, now, dependencyOverrideAcknowledged: false);
            task.Apply(claimed);
            events.Add(claimed);
            deliberateLeaseGeneration = claimed.LeaseGeneration;
        }

        long claimedVersion = events.Count;
        session.Events.StartStream<TaskAggregate>(taskId, [.. events]);
        await session.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Auto-created pr-review task {TaskId} for {Repository}#{Number}, assigned to {Login} at "
            + "{Speed} speed", taskId, repository, candidate.Number, login, project.AutoPrReview.Value);

        if (deliberateRunId is { } runId && deliberateLeaseGeneration is { } generation)
        {
            try
            {
                // The ceiling-exempt sentinel node id, exactly as h9k task start's own claim uses:
                // launched through this daemon's own run-launching mechanism (RunLauncher already
                // knows how to dispatch a pr-review task, per Decisions Log #99 — nothing about
                // that is rebuilt here) rather than waiting for the ordinary claim sweep, which
                // would never pick this run up anyway since NodeLoad never counts a
                // Guid.Empty-claimed run. dispatchingNodeId names this physical daemon so
                // RunSupervisor's own sentinel-run adoption can tell this node's runs apart from
                // another node's sharing the same database.
                await launcher.LaunchAsync(
                    taskId, runId, Guid.Empty, node.OwnerId, generation,
                    dispatchingNodeId: node.NodeId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // RunLauncher.LaunchAsync's own catch handles every ordinary launch failure by
                // failing the task through RecordLaunchFailureAsync, but deliberately excludes
                // cancellation so a daemon shutdown mid-launch propagates rather than being
                // recorded as an agent failure — uncaught here, that would leave this task
                // permanently Claimed with no run stream and nothing that ever recovers it
                // (independent pre-PR review, cycle 1, adversarial lens), the same gap h9k task
                // start's own FailDeliberateClaimAsync closes for the identical deliberate-claim
                // shape.
                await FailDeliberateLaunchAsync(
                    taskId, runId, claimedVersion,
                    "the daemon stopped while launching this auto-created review", CancellationToken.None);
                throw;
            }
        }

        return 1;
    }

    /// <summary>
    /// The compensation <c>TaskStartCommand.FailDeliberateClaimAsync</c> runs for the identical
    /// deliberate-claim shape: a fenced check that nothing else already moved this stream past the
    /// claim this call is compensating for, then an honest <see cref="TaskFailed"/> rather than a
    /// permanently stranded Claimed task with no run and no lease.
    /// </summary>
    private async Task FailDeliberateLaunchAsync(
        Guid taskId, Guid runId, long claimedVersion, string reason, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        StreamState? fence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken);
        if (fence is null || fence.Version != claimedVersion)
        {
            return;
        }

        TaskAggregate? current = await session.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, version: fence.Version, token: cancellationToken);
        if (current is null || !TaskDecider.CanFail(current))
        {
            return;
        }

        session.Events.Append(taskId, expectedVersion: fence.Version + 1,
            TaskDecider.Fail(current, runId, $"Auto-pr-review's immediate launch failed: {reason}", DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Whether <paramref name="currentRequestedAt"/> — the currently-requested candidate's own
    /// most recent <c>ReviewRequestedEvent</c> timestamp — postdates the request
    /// <paramref name="previousReview"/> was minted from, the one fact that tells a genuine
    /// re-request (Alice requests again after every finding was directed) apart from the same
    /// standing request GitHub never cleared (independent pre-PR review, cycle 1, both lenses).
    /// Conservative wherever the evidence is missing: no currently-observed timestamp at all
    /// means there is nothing to prove this is a fresh request, so it is treated as the same
    /// standing one rather than risk the infinite re-mint loop this check exists to close; no
    /// baseline recorded on the previous task (a stream predating <see cref="PullRequestReviewAssignmentObserved.RequestedAt"/>)
    /// means there is nothing to compare against, so any currently-observed timestamp counts as
    /// fresher rather than permanently blocking re-review on tasks this field predates.
    /// <para>
    /// Internal (rather than private) so the dedup-timestamp comparison is directly testable
    /// (test: AutoPrReviewEngine dedup coverage) without also depending on
    /// <c>WorkItemConnections.ImporterAsync</c>'s un-injectable real-<c>gh</c> construction.
    /// </para>
    /// </summary>
    internal static async Task<bool> IsGenuineReRequestAsync(
        IDocumentSession session, TaskListItem previousReview, DateTimeOffset? currentRequestedAt,
        CancellationToken cancellationToken)
    {
        if (currentRequestedAt is not { } current)
        {
            return false;
        }

        PullRequestReviewAssignmentObserved? previousObserved = await MostRecentObservedAsync(
            session, previousReview.Id, cancellationToken);

        return previousObserved?.RequestedAt is not { } previous || current > previous;
    }

    /// <summary>
    /// The most recent <see cref="PullRequestReviewAssignmentObserved"/> this task's own stream
    /// carries — the request (URL and timestamp both) the task was actually minted from, read
    /// fresh from the stream rather than cached on the projection, the same source
    /// <see cref="IsGenuineReRequestAsync"/> already reads on the mint side.
    /// </summary>
    private static async Task<PullRequestReviewAssignmentObserved?> MostRecentObservedAsync(
        IDocumentSession session, Guid taskId, CancellationToken cancellationToken)
    {
        IReadOnlyList<IEvent> stream = await session.Events.FetchStreamAsync(taskId, token: cancellationToken);
        return stream
            .Select(recorded => recorded.Data)
            .OfType<PullRequestReviewAssignmentObserved>()
            .LastOrDefault();
    }

    /// <summary>
    /// Every non-terminal task this feature previously auto-created whose pull request no longer
    /// review-requests this login, per the current sweep's own read — the comparison point is
    /// <see cref="TaskListItem.AutoPrReviewAssigneeLogin"/> itself (set only by this feature), so a
    /// task a human minted by hand with <c>h9k task add --from-pr</c> is never touched here, and a
    /// task already recalled once does not fire again on every later poll.
    /// </summary>
    private async Task<int> ConcludeWithdrawnAsync(
        ProjectDetails project, string repository, string login,
        IReadOnlyList<ReviewRequestedPullRequest> currentlyRequested, CancellationToken cancellationToken)
    {
        HashSet<int> stillRequested = [.. currentlyRequested.Select(pr => pr.Number)];

        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<TaskListItem> watched = await query.Query<TaskListItem>()
            .Where(task => task.ProjectId == project.Id)
            .Where(task => task.AutoPrReviewAssigneeLogin != null)
            .Where(task => task.MatchesSql("d.data ->> 'type' = ?", TaskType.PrReview.Value))
            .Where(task => task.MatchesSql("d.data ->> 'state' NOT IN (?, ?)", TerminalStates[0], TerminalStates[1]))
            .ToListAsync(cancellationToken);

        int recalled = 0;
        foreach (TaskListItem watchedTask in watched)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (watchedTask.ExternalReference is not { } reference
                || ExternalReference.Parse(reference) is not { Provider: var provider, Key: { Length: > 0 } key }
                || provider != WorkItemProvider.GitHubPullRequest
                || !int.TryParse(key, out int number)
                || stillRequested.Contains(number))
            {
                continue;
            }

            try
            {
                if (await ConcludeOneAsync(watchedTask, project, repository, number, login, cancellationToken))
                {
                    recalled++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception, "Auto-pr-review could not record the withdrawn assignment for task {TaskId}",
                    watchedTask.Id);
            }
        }

        return recalled;
    }

    /// <summary>Returns whether a recall was actually recorded — false for every path that leaves the task untouched.</summary>
    private async Task<bool> ConcludeOneAsync(
        TaskListItem watchedTask, ProjectDetails project, string repository,
        int number, string login, CancellationToken cancellationToken)
    {
        ReviewRequestActor actor = await reviewAssignments.FindMostRecentRequestActorAsync(
            OwnerFrom(repository), NameFrom(repository), number, login,
            ReviewTimelineEventKind.Removed, project.RepositoryPath, cancellationToken);

        if (!actor.Found)
        {
            // Dropping out of the review-requested search is not proof of a recall (independent
            // pre-PR review, cycle 1, adversarial lens): a merge, a submitted review that cleared
            // the request, or a transient gh failure all look identical from here, and none of
            // them means the assignment was withdrawn. Only a timeline that actually shows the
            // removal event is positive evidence of one — absence alone concludes nothing, and
            // the next sweep's fresh read decides instead.
            logger.LogDebug(
                "Auto-pr-review saw {Repository}#{Number} drop out of the review-requested search for task "
                + "{TaskId}, but the timeline shows no removal event — not concluding a recall this sweep",
                repository, number, watchedTask.Id);
            return false;
        }

        await using IDocumentSession session = store.LightweightSession();
        StreamState? fence = await session.Events.FetchStreamStateAsync(watchedTask.Id, cancellationToken);
        if (fence is null)
        {
            return false;
        }

        TaskAggregate? task = await session.Events.AggregateStreamAsync<TaskAggregate>(
            watchedTask.Id, version: fence.Version, token: cancellationToken);
        if (task is null || task.AutoPrReviewAssigneeLogin is null)
        {
            // Already handled by an earlier tick, or the stream moved since this sweep's own
            // read — a lost race, not a defect: the next poll's own fresh read is authoritative.
            return false;
        }

        // Positive evidence that this removal is the one that actually recalled the request this
        // task was minted from — not just any removal sitting somewhere in the last 20 timeline
        // events, which can be a leftover from an earlier request/re-request cycle whose own
        // removal predates the request that later minted this task (independent pre-PR review,
        // cycle 1, both lenses). Conservative in the same shape IsGenuineReRequestAsync already
        // applies on the mint side: no baseline recorded on this task (a stream predating
        // PullRequestReviewAssignmentObserved.RequestedAt) means there is nothing to compare
        // against, so any removal found counts; a baseline exists but the removal itself carries
        // no timestamp (GitHub's createdAt failed to parse) means there is nothing to prove this
        // evidence is fresh, so it does not conclude a recall. ">=", not ">": GitHub's own
        // createdAt timestamps are second-resolution, and a removal genuinely paired with this
        // task's own minting request can legitimately read identically to it once truncated —
        // a stale removal from a genuinely earlier cycle is strictly, meaningfully earlier than
        // the request that later minted this task, never merely tied with it.
        PullRequestReviewAssignmentObserved? mintObserved = await MostRecentObservedAsync(
            session, watchedTask.Id, cancellationToken);
        bool removalPostdatesMint = mintObserved?.RequestedAt is not { } mintedAt
            || (actor.RequestedAt is { } removedAt && removedAt >= mintedAt);
        if (!removalPostdatesMint)
        {
            logger.LogDebug(
                "Auto-pr-review saw a removal event for {Repository}#{Number} on task {TaskId}, but it "
                + "predates the request the task was minted from — not concluding a recall this sweep",
                repository, number, watchedTask.Id);
            return false;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool concludesBeforeDispatch = task.State == TaskState.Published || task.State == TaskState.Queued;

        // The real observed URL from this task's own minting event, matching the sibling Observed
        // event's own field — never a hardcoded github.com URL, which resolves to the wrong
        // repository (or nothing) on a GitHub Enterprise host (independent pre-PR review, cycle 1,
        // adversarial lens), and never the bare "owner/repo#42" canonical reference either, which
        // is a different shape for a different reader.
        string pullRequestUrl = mintObserved?.PullRequestUrl ?? $"https://github.com/{repository}/pull/{number}";
        PullRequestReviewAssignmentRecalled recalled = new(
            task.Id, pullRequestUrl, actor.Login, now, concludesBeforeDispatch);

        if (!concludesBeforeDispatch)
        {
            session.Events.Append(task.Id, recalled);
            await session.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Task {TaskId}'s GitHub reviewer assignment was recalled after its run already started — "
                + "recorded as an observation; the work continues", task.Id);
            return true;
        }

        task.Apply(recalled);
        string reason = actor.Login is { } recaller
            ? $"The GitHub reviewer assignment that created this task was recalled by {recaller} before the "
              + "run ever dispatched — the go signal recalled by the same authority that gave it "
              + "(PLAN.md §16 decision #34's amendment)."
            : "The GitHub reviewer assignment that created this task was recalled before the run ever "
              + "dispatched — the go signal recalled by the same authority that gave it "
              + "(PLAN.md §16 decision #34's amendment).";
        TaskAbandoned abandoned = TaskDecider.Abandon(task, reason, now, node.OwnerId);

        // Fenced (independent pre-PR review, cycle 1, both lenses): concludesBeforeDispatch was
        // decided from the aggregate read above, and a claim can commit between that read and
        // this append the same way GenerationFence.LoadFencedAsync's own doc comment warns about
        // — the ordinary claim sweep dispatches independently of this one. An unfenced append
        // would land TaskAbandoned on top of a TaskClaimed it never accounted for, stranding a
        // live agent's worktree and lease under a task that now reads Abandoned. Mirrors
        // FailDeliberateLaunchAsync's identical fenced-compensation shape for the same hazard.
        try
        {
            session.Events.Append(task.Id, expectedVersion: fence.Version + 2, recalled, abandoned);
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            logger.LogInformation(
                "Task {TaskId} was claimed between this sweep's read and its recall — not concluding it; "
                + "the work continues and the next poll's own fresh read decides instead", task.Id);
            return false;
        }

        logger.LogInformation(
            "Task {TaskId} concluded: its GitHub reviewer assignment was recalled before the run dispatched",
            task.Id);
        return true;
    }

    private static string OwnerFrom(string repository) => repository.Split('/')[0];

    private static string NameFrom(string repository) => repository.Split('/')[1];
}

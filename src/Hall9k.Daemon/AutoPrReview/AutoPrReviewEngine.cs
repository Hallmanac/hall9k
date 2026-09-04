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
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
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
    private static readonly string[] LiveNonTerminalStates =
        [TaskState.Done.Value, TaskState.Abandoned.Value];

    private readonly GitHubReviewAssignments reviewAssignments = new(processRunner);

    public async Task<AutoPrReviewSweepResult> PollOnceAsync(CancellationToken cancellationToken)
    {
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
        WorkItemImporter importer = await WorkItemConnections.ImporterAsync(session, cancellationToken);
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

        TaskListItem? previousReview = await session.Query<TaskListItem>()
            .Where(task => task.ExternalReference == canonical)
            .Where(task => task.MatchesSql(
                "d.data ->> 'type' = ? AND d.data ->> 'state' = ?", TaskType.PrReview.Value, TaskState.Done.Value))
            .OrderByDescending(task => task.AddedAt)
            .FirstOrDefaultAsync(cancellationToken);

        ReviewRequestActor actor = await reviewAssignments.FindMostRecentRequestActorAsync(
            OwnerFrom(repository), NameFrom(repository), candidate.Number, login, project.RepositoryPath, cancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string objective = RelayedText.WithoutClosingKeywords(RelayedText.OneLine(imported.Title)).Trim() is { Length: > 0 } seed
            ? seed
            : $"Review pull request {imported.Reference.Key}";

        string provenance = actor.Login is { } assigner
            ? $"GitHub reviewer assignment observed: {assigner} requested {login} as a reviewer"
              + (actor.RequestedAt is { } at ? $" at {at:yyyy-MM-dd HH:mm:ss}Z" : string.Empty) + "."
            : $"GitHub reviewer assignment observed: {login} was requested as a reviewer (the actor who "
              + "requested it could not be read from GitHub's own timeline).";
        string? reReviewNote = previousReview is not null
            ? $"This is a re-review: task {DomainId.Short(previousReview.Id)} already reviewed this pull "
              + $"request and closed Done on {previousReview.AddedAt:yyyy-MM-dd}."
            : null;
        string additionalContext = reReviewNote is null ? provenance : $"{provenance}\n{reReviewNote}";
        string agentContext = WorkItemContext.Compose(imported, additionalContext);

        string[] criteria =
        [
            "The findings report is walked with the owner (walk-pr-review-findings) and every finding is directed.",
        ];

        Guid taskId = DomainId.New();
        TaskAdded added = TaskDecider.Add(
            taskId, project.Id, objective, criteria, TaskType.PrReview, agentContext, constraints: null,
            imported.Reference, now, node.OwnerId, model: null, blockedBy: null, sourceIdeaId: null, epicId: null);

        PullRequestReviewAssignmentObserved observed = new(
            taskId, imported.Url?.ToString() ?? candidate.Url, login, actor.Login, now);

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

        Guid? deliberateRunId = null;
        int? deliberateLeaseGeneration = null;
        if (project.AutoPrReview == AutoPrReviewSpeed.First)
        {
            TaskRevised revised = TaskDecider.Revise(
                task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
                Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None,
                now, node.OwnerId, Optional<Guid?>.None, Optional<bool>.Of(true));
            task.Apply(revised);
            events.Add(revised);
        }
        else if (project.AutoPrReview == AutoPrReviewSpeed.Now)
        {
            deliberateRunId = DomainId.New();
            TaskClaimed claimed = TaskDecider.ClaimDeliberately(
                task, node.OwnerId, deliberateRunId.Value, now, dependencyOverrideAcknowledged: false);
            task.Apply(claimed);
            events.Add(claimed);
            deliberateLeaseGeneration = claimed.LeaseGeneration;
        }

        session.Events.StartStream<TaskAggregate>(taskId, [.. events]);
        await session.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Auto-created pr-review task {TaskId} for {Repository}#{Number}, assigned to {Login} at "
            + "{Speed} speed", taskId, repository, candidate.Number, login, project.AutoPrReview.Value);

        if (deliberateRunId is { } runId && deliberateLeaseGeneration is { } generation)
        {
            // The ceiling-exempt sentinel node id, exactly as h9k task start's own claim uses:
            // launched through this daemon's own run-launching mechanism (RunLauncher already
            // knows how to dispatch a pr-review task, per Decisions Log #99 — nothing about that
            // is rebuilt here) rather than waiting for the ordinary claim sweep, which would never
            // pick this run up anyway since NodeLoad never counts a Guid.Empty-claimed run.
            await launcher.LaunchAsync(taskId, runId, Guid.Empty, node.OwnerId, generation, cancellationToken);
        }

        return 1;
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
            .Where(task => task.MatchesSql("d.data ->> 'state' NOT IN (?, ?)", LiveNonTerminalStates[0], LiveNonTerminalStates[1]))
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
                await ConcludeOneAsync(watchedTask, project, repository, number, login, cancellationToken);
                recalled++;
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

    private async Task ConcludeOneAsync(
        TaskListItem watchedTask, ProjectDetails project, string repository,
        int number, string login, CancellationToken cancellationToken)
    {
        ReviewRequestActor actor = await reviewAssignments.FindMostRecentRequestActorAsync(
            OwnerFrom(repository), NameFrom(repository), number, login, project.RepositoryPath, cancellationToken);

        await using IDocumentSession session = store.LightweightSession();
        TaskAggregate? task = await session.Events.AggregateStreamAsync<TaskAggregate>(watchedTask.Id, token: cancellationToken);
        if (task is null || task.AutoPrReviewAssigneeLogin is null)
        {
            // Already handled by an earlier tick, or the stream moved since this sweep's own
            // read — a lost race, not a defect: the next poll's own fresh read is authoritative.
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool concludesBeforeDispatch = task.State == TaskState.Published || task.State == TaskState.Queued;

        // A real URL, matching the sibling Observed event's own field — never the bare
        // "owner/repo#42" canonical reference, which is a different shape for a different reader.
        PullRequestReviewAssignmentRecalled recalled = new(
            task.Id, $"https://github.com/{repository}/pull/{number}", actor.Login, now, concludesBeforeDispatch);

        if (!concludesBeforeDispatch)
        {
            session.Events.Append(task.Id, recalled);
            await session.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Task {TaskId}'s GitHub reviewer assignment was recalled after its run already started — "
                + "recorded as an observation; the work continues", task.Id);
            return;
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

        session.Events.Append(task.Id, recalled, abandoned);
        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Task {TaskId} concluded: its GitHub reviewer assignment was recalled before the run dispatched",
            task.Id);
    }

    private static string OwnerFrom(string repository) => repository.Split('/')[0];

    private static string NameFrom(string repository) => repository.Split('/')[1];
}

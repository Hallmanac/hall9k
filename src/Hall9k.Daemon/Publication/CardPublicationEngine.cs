using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Publication;

/// <summary>
/// One sweep's tally: how many publication sessions this node dispatched, how many produced a
/// verified card, and how many sessions it adopted — ones it had dispatched on an earlier run and
/// never recorded an outcome for.
/// </summary>
public sealed record CardPublicationSweepResult(int Dispatched, int Linked, int Adopted = 0);

/// <summary>
/// Turns a publication request into an agent session, and records what came of it (backlog 18).
/// <para>
/// The platform does not write the card. It cannot honestly: an issue type, a required field, and
/// a routing rule are one organisation's Jira configuration, and the teams that have them have
/// them written down already — so the session runs in the project's own repository, where those
/// rules live as skills, with the owner's MCP access. What this class owns is everything around
/// that: which requests are this node's to do, that exactly one session runs per request, that a
/// session which hangs is not waited on forever, that a session the daemon stopped in the middle
/// of is picked back up rather than left hanging over the task, and that the outcome recorded
/// afterwards is read off the task rather than off anything the agent said.
/// </para>
/// <para>
/// Requests are handled one at a time on purpose. A publication is a rare, human-initiated act
/// rather than a queue to be drained, and the failure that actually costs somebody an afternoon
/// is two cards for one task — so serial is both sufficient and the safer shape.
/// </para>
/// </summary>
public sealed class CardPublicationEngine(
    IDocumentStore store,
    NodeContext node,
    IExecutor executor,
    IProcessManager processManager,
    IOptions<DaemonOptions> options,
    ILogger<CardPublicationEngine> logger)
{
    /// <summary>
    /// How long the shutdown path gets to record an outcome. Short on purpose: it runs while the
    /// daemon is stopping, and a stop that waits on Postgres is worse than an outcome recorded a
    /// restart later by adoption.
    /// </summary>
    private static readonly TimeSpan ShutdownRecordTimeout = TimeSpan.FromSeconds(5);

    private readonly DaemonOptions _options = options.Value;

    /// <summary>One sweep over this owner's outstanding publication requests.</summary>
    public async Task<CardPublicationSweepResult> PollOnceAsync(CancellationToken cancellationToken)
    {
        // Adoption comes first, and for the same reason the dispatch query excludes a dispatched
        // request: what is already running is settled before anything new is started.
        int adopted = await AdoptStrandedAsync(cancellationToken);

        IReadOnlyList<TaskDetails> pending;
        await using (IQuerySession query = store.QuerySession())
        {
            Guid ownerId = node.OwnerId;
            pending = await query.Query<TaskDetails>()
                .Where(task => task.PendingPublicationProvider != null)
                .Where(task => !task.PublicationSessionDispatched)
                .Where(task => task.PublicationRequestedByOwnerId == ownerId)
                .OrderBy(task => task.PublicationRequestedAt)
                .ToListAsync(cancellationToken);
        }

        int dispatched = 0;
        int linked = 0;
        foreach (TaskDetails task in pending)
        {
            try
            {
                bool? result = await PublishAsync(task, cancellationToken);
                if (result is { } gotLink)
                {
                    dispatched++;
                    linked += gotLink ? 1 : 0;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Publication failed for task {TaskId}; recording it and moving on", task.Id);
                await CompleteAsync(
                    task.Id,
                    linkedNow: false,
                    $"The daemon could not run the publication session: {exception.Message}",
                    cancellationToken);
            }
        }

        return new CardPublicationSweepResult(dispatched, linked, adopted);
    }

    /// <summary>
    /// Publication sessions this node dispatched and never recorded an outcome for, finished now.
    /// <para>
    /// Without this a stopped daemon strands the task permanently. The dispatch is on the stream
    /// and the completion is not, and nothing else clears that: the dispatch sweep skips a request
    /// whose session has already been spawned (the rule that stops a second card),
    /// <c>h9k task push-to-jira</c> refuses while a publication is outstanding, and
    /// <c>h9k task link-jira</c> needs a card key that may not exist — so the task reads "a session
    /// is writing the card" forever with nothing writing anything. Origin incident (2026-08-21):
    /// the pre-PR review of this branch traced it from <c>h9k daemon stop</c> during the
    /// publication timeout window, which killed the session between the two events.
    /// </para>
    /// <para>
    /// Scoped to this node because a pid is only meaningful on the machine it belongs to, the same
    /// rule run adoption follows. A session found alive is waited on rather than assumed dead: the
    /// daemon can be restarted while a detached session it spawned is still going, and killing that
    /// one would throw away a card it may be halfway through creating.
    /// </para>
    /// </summary>
    private async Task<int> AdoptStrandedAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<TaskDetails> stranded;
        await using (IQuerySession query = store.QuerySession())
        {
            Guid nodeId = node.NodeId;
            stranded = await query.Query<TaskDetails>()
                .Where(task => task.PendingPublicationProvider != null)
                .Where(task => task.PublicationSessionDispatched)
                .Where(task => task.PublicationSessionNodeId == nodeId)
                .OrderBy(task => task.PublicationRequestedAt)
                .ToListAsync(cancellationToken);
        }

        int adopted = 0;
        foreach (TaskDetails task in stranded)
        {
            try
            {
                await AdoptAsync(task, cancellationToken);
                adopted++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception, "Could not finish the stranded publication for task {TaskId}", task.Id);
                await CompleteAsync(
                    task.Id,
                    linkedNow: false,
                    "The daemon stopped while this publication's session was running, and picking it "
                    + $"back up failed: {exception.Message}. Check the board before requesting it again.",
                    cancellationToken);
                adopted++;
            }
        }

        return adopted;
    }

    private async Task AdoptAsync(TaskDetails task, CancellationToken cancellationToken)
    {
        if (task.PublicationSessionId is not { } sessionId
            || task.PublicationSessionProcessId is not { } processId
            || task.PublicationSessionStartedAt is not { } startedAt)
        {
            // Dispatched with no process identity recorded beside it, so there is nothing to ask
            // about it. The honest answer is that nobody knows how it ended, which is what the
            // outcome says rather than a guess in either direction.
            await CompleteAsync(
                task.Id,
                linkedNow: false,
                "This publication's session was dispatched without a recorded process, so nothing can "
                + "say how it ended. Check the board for a card before running h9k task push-to-jira "
                + "again.",
                cancellationToken);
            return;
        }

        bool alive = processManager.IsAlive(processId, startedAt);
        logger.LogInformation(
            "Task {TaskId}: adopting card-publication session {SessionId} (pid {ProcessId}, still running: {Alive})",
            task.Id, sessionId, processId, alive);

        (bool linked, string outcome) = await WaitAsync(
            task.Id, sessionId, new SpawnedAgent(processId, startedAt), cancellationToken);

        await CompleteAsync(task.Id, linked, linked ? outcome : Stranded(alive, outcome), cancellationToken);
    }

    /// <summary>
    /// What an adopted session that produced no link is recorded as. It says who was watching and
    /// when, because that is the difference between "no card was created" and "no card was seen
    /// created" — and only the second one is true here, which is why it asks for the board to be
    /// looked at rather than telling anybody to publish again.
    /// </summary>
    private static string Stranded(bool alive, string outcome) =>
        (alive
            ? "The daemon was restarted while this session was running and picked it back up. "
            : "The daemon stopped while this session was running and was not there when it ended. ")
        + outcome
        + " Check the board before running h9k task push-to-jira again: a session that created a "
        + "card and never reported it back through h9k task link-jira leaves the card there and "
        + "this task unlinked.";

    /// <summary>
    /// One request: spawn the session, wait for it under a ceiling, then record what the task
    /// actually came out carrying. Null means the request was not this sweep's to act on after
    /// all — another sweep beat it to the dispatch, or the request was already resolved.
    /// </summary>
    private async Task<bool?> PublishAsync(TaskDetails task, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();

        // Fence before reading, the DispatchEngine order: the dispatch append below carries this
        // version, so a link or a second sweep landing in between fails the commit rather than
        // being absorbed — which is what makes "exactly one session per request" true rather than
        // likely. Two sessions would mean two cards, and a duplicate card is a human's cleanup.
        StreamState? fence = await session.Events.FetchStreamStateAsync(task.Id, cancellationToken);
        TaskAggregate? aggregate = fence is null
            ? null
            : await session.Events.AggregateStreamAsync<TaskAggregate>(
                task.Id, version: fence.Version, token: cancellationToken);
        if (aggregate?.PendingPublicationProvider is null || aggregate.PublicationSessionDispatched)
        {
            return null;
        }

        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(aggregate.ProjectId, cancellationToken);
        if (project is null)
        {
            await CompleteAsync(task.Id, false, "The task's project is not registered on this node.", cancellationToken);
            return false;
        }

        if (!Directory.Exists(project.RepositoryPath))
        {
            await CompleteAsync(
                task.Id,
                false,
                $"The project's repository is not at {project.RepositoryPath}, and the session runs there to "
                + "read this project's card rules. Fix the path and run h9k task push-to-jira again.",
                cancellationToken);
            return false;
        }

        ConnectionDetails? connection = await WorkItemConnections.FindJiraConnectionAsync(session, cancellationToken);
        if (connection?.SiteUrl is not { } site)
        {
            await CompleteAsync(
                task.Id,
                false,
                "No usable Jira connection is registered, so nothing could verify the card this session "
                + "would create. Register one with h9k connection add jira and request it again.",
                cancellationToken);
            return false;
        }

        Guid sessionId = DomainId.New();
        AgentModel model = _options.ResolveModel(AgentRole.Publication, aggregate.Model, project.Model);
        string prompt = AgentPromptBuilder.BuildCardPublication(
            task,
            project,
            project.RepositoryPath,
            site.GetLeftPart(UriPartial.Authority),
            aggregate.PendingPublicationProjectKey,
            $"h9k task link-jira {task.Id}");

        SpawnedAgent agent = await executor.SpawnAsync(
            // RunId doubles as the artifact key and there is no run here: a publication has no
            // worktree, no branch, and no lease. The session's own id names its directory, which
            // is what WorkItemPublicationDispatched records so the prompt and stream stay findable.
            new AgentSpawnRequest(
                sessionId, sessionId, project.RepositoryPath, prompt,
                ExecutorMode.Subscription, model, project.SkipPermissions),
            cancellationToken);

        try
        {
            session.Events.Append(task.Id, expectedVersion: fence!.Version + 1, new WorkItemPublicationDispatched(
                task.Id, sessionId, node.NodeId, agent.ProcessId, agent.StartedAt, DateTimeOffset.UtcNow, model));
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            // Somebody else moved the task between the read and the append. The session is already
            // running and now belongs to nobody, so it is stopped rather than left to create a card
            // this node has no record of asking for.
            logger.LogDebug("Task {TaskId} advanced while its publication was being dispatched; stopping the session", task.Id);
            Terminate(task.Id, agent);
            return null;
        }

        logger.LogInformation(
            "Task {TaskId}: card publication session {SessionId} dispatched (pid {ProcessId}, model {Model}, artifacts in {Directory})",
            task.Id, sessionId, agent.ProcessId, model.Value, RunPaths.RunDirectory(sessionId));

        (bool linked, string outcome) = await WaitAsync(task.Id, sessionId, agent, cancellationToken);
        await CompleteAsync(task.Id, linked, outcome, cancellationToken);
        return linked;
    }

    /// <summary>
    /// Wait for the session, then ask the task — not the agent — whether a card was linked.
    /// <para>
    /// That order is the observation gate closing. The session's own last words are recorded as
    /// the outcome because they are what a human reads when something went wrong, but whether the
    /// publication succeeded is read off the task's own reference, which only
    /// <c>h9k task link-jira</c> can have set, and only after reading the card back from Jira. A
    /// session that reported a beautiful success and never got a key past that command completed
    /// without a link, and the record says exactly that.
    /// </para>
    /// </summary>
    private async Task<(bool Linked, string Outcome)> WaitAsync(
        Guid taskId, Guid sessionId, SpawnedAgent agent, CancellationToken cancellationToken)
    {
        AgentResult? result;
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_options.CardPublicationTimeout);
        try
        {
            result = await SessionResultWaiter.WaitAsync(
                RunPaths.StreamFile(sessionId), agent.ProcessId, agent.StartedAt,
                processManager, onOutput: null, budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Task {TaskId}: the card-publication session exceeded {Timeout} — terminating it",
                taskId, _options.CardPublicationTimeout);
            Terminate(taskId, agent);
            result = null;
        }
        catch (OperationCanceledException)
        {
            // The daemon is stopping. The session is detached and would outlive it, so it is
            // stopped — and the outcome is recorded here rather than left for the next start, so
            // the task stops reading as "a session is writing the card" the moment there is no
            // session. The write needs a token of its own because the sweep's is already
            // cancelled; if it does not land, AdoptStrandedAsync finishes the job when the daemon
            // comes back, which is why this is best-effort rather than something to fail on.
            Terminate(taskId, agent);
            try
            {
                using CancellationTokenSource shutdown = new(ShutdownRecordTimeout);
                await CompleteAsync(
                    taskId,
                    await IsLinkedAsync(taskId, shutdown.Token),
                    "The daemon stopped while this session was writing the card, so the session was "
                    + "stopped with it. Check the board before running h9k task push-to-jira again: a "
                    + "session that created a card and never reported it back through h9k task link-jira "
                    + "leaves the card there and this task unlinked.",
                    shutdown.Token);
            }
            catch (Exception recording)
            {
                logger.LogWarning(
                    recording,
                    "Task {TaskId}: could not record the publication outcome during shutdown; the next "
                    + "sweep adopts it", taskId);
            }

            throw;
        }

        bool linked = await IsLinkedAsync(taskId, cancellationToken);
        string outcome = linked
            ? Summarize(result) ?? "The session created the card and reported it through h9k task link-jira."
            : Summarize(result) is { } said
                ? $"The session ended without a verified card key. It said: {said}"
                : "The session ended without a verified card key and left no result to read. Its prompt and "
                  + $"transcript are in {RunPaths.RunDirectory(sessionId)}.";

        return (linked, outcome);
    }

    /// <summary>
    /// Whether the task now carries an external reference. Read fresh from the store rather than
    /// from anything held in memory: the reference was written by a different process — the agent's
    /// own <c>h9k task link-jira</c> — while this method was waiting.
    /// </summary>
    private async Task<bool> IsLinkedAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();
        TaskDetails? task = await session.LoadAsync<TaskDetails>(taskId, cancellationToken);
        return task?.ExternalReference.IsNotBlank() is true;
    }

    /// <summary>
    /// The session's last words, bounded. It goes onto an event and into a terminal, and it is
    /// text an agent wrote about someone else's Jira, so it is neither trusted to be short nor
    /// trusted to be printable.
    /// </summary>
    private static string? Summarize(AgentResult? result) =>
        result?.Summary is { } summary && summary.IsNotBlank()
            ? Connectors.Text.RelayedText.Truncate(Connectors.Text.RelayedText.OneLine(summary).Trim(), 500)
            : null;

    private async Task CompleteAsync(
        Guid taskId, bool linkedNow, string outcome, CancellationToken cancellationToken)
    {
        try
        {
            await using IDocumentSession session = store.LightweightSession();
            session.Events.Append(taskId, new WorkItemPublicationCompleted(
                taskId, linkedNow, outcome, DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The pending marker stays set, which means the next sweep will not redispatch (the
            // session is recorded as dispatched) and h9k task show still says a publication is
            // outstanding. That is the safe way to fail: a stuck marker is visible, a cleared one
            // invites a second card.
            logger.LogError(exception, "Could not record the publication outcome for task {TaskId}", taskId);
        }
    }

    private void Terminate(Guid taskId, SpawnedAgent agent)
    {
        try
        {
            processManager.Terminate(agent.ProcessId, agent.StartedAt);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Task {TaskId}: could not terminate the abandoned card-publication session (pid {ProcessId})",
                taskId, agent.ProcessId);
        }
    }
}

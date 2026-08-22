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

    /// <summary>
    /// What every outcome that ends a publication without a link has to say, in one place because
    /// every one of them has to say it.
    /// <para>
    /// Completing clears the pending marker, which is what makes the task publishable again — so
    /// an outcome that reports no link without this reads as "no card was created" when what it
    /// actually means is "no card was seen created". Those are different facts, and only the
    /// second one was observed: the session that timed out, or died leaving nothing behind, may
    /// have filed a card first. An operator who takes the first reading runs push-to-jira again
    /// and gets the duplicate this whole class is arranged to avoid. Origin incident (2026-08-21):
    /// the pre-PR review of this branch found the caution on the adoption and shutdown paths and
    /// missing from the ordinary timed-out and died-without-a-result ones; its second cycle found
    /// the last one without it, the sweep's catch-all, which was also ending publications whose
    /// session was still running.
    /// </para>
    /// </summary>
    private const string CheckTheBoard =
        "Check the board before running h9k task push-to-jira again: a session that created a card "
        + "and never reported it back through h9k task link-jira leaves the card there and this task "
        + "unlinked.";

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
                // What reaches here is the dispatch failing before a session exists: PublishAsync
                // owns everything after the spawn, where a live session has to be stopped rather
                // than merely reported. The caution is still on the message, because a catch-all
                // cannot prove which side of the spawn it came from — SpawnAsync itself can throw
                // with a started process behind it — and this outcome clears the pending marker
                // either way.
                logger.LogError(exception, "Publication failed for task {TaskId}; recording it and moving on", task.Id);
                await CompleteAsync(
                    task.Id,
                    linkedNow: false,
                    $"The daemon could not run the publication session: {exception.Message}. {CheckTheBoard}",
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

                // The same rule the dispatch path follows: the outcome below clears the pending
                // marker, so an adopted session nobody managed to watch to an end is stopped
                // rather than left detached to file a card against a task that is publishable
                // again. Its identity is on the stream, which is what adoption reads.
                if (task.PublicationSessionProcessId is { } processId
                    && task.PublicationSessionStartedAt is { } startedAt)
                {
                    Terminate(task.Id, new SpawnedAgent(processId, startedAt));
                }

                await CompleteAsync(
                    task.Id,
                    linkedNow: false,
                    "The daemon stopped while this publication's session was running, and picking it "
                    + $"back up failed: {exception.Message}. {CheckTheBoard}",
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
            // Dispatched with no process identity recorded beside it — the daemon died in the
            // window between committing the dispatch and recording the process it spawned — so
            // there is nothing to ask about it. The honest answer is that nobody knows whether a
            // session ever started, let alone how it ended, which is what the outcome says rather
            // than a guess in either direction.
            await CompleteAsync(
                task.Id,
                linkedNow: false,
                "This publication's session was dispatched and no process was ever recorded beside it, "
                + $"so nothing can say whether it ran. {CheckTheBoard}",
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
    /// created" — and only the second one is true here.
    /// <para>
    /// The caution is not appended: every outcome this prefixes comes from
    /// <see cref="WaitAsync"/>'s no-link return, which already ends with <see cref="CheckTheBoard"/>.
    /// Adding it here said the same thing twice in one sentence, which reads as a bug in the
    /// record rather than an emphasis. Origin incident (2026-08-22): the pre-PR review of this
    /// branch found the duplicate on the adoption path in its third cycle.
    /// </para>
    /// </summary>
    private static string Stranded(bool alive, string outcome) =>
        (alive
            ? "The daemon was restarted while this session was running and picked it back up. "
            : "The daemon stopped while this session was running and was not there when it ended. ")
        + outcome;

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

        // The decider's other publication rule, enforced again at the point where a card would
        // actually be written. TaskDecider.RequestWorkItemPublication refuses a task that already
        // carries an external reference, and this is the last gate between that rule and a real
        // second card on somebody's board, so it does not get to be true only in the command that
        // asked. Origin incident (2026-08-21): the pre-PR review of this branch found
        // h9k task push-to-jira appending its request unfenced, so a link landing between its read
        // and its append left a task both linked and pending, and this sweep dispatched a session
        // to write a card for work that already had one. The command is fenced now; this stays
        // because the guard belongs where the consequence is.
        if (aggregate.ExternalReference is { } already)
        {
            await CompleteAsync(
                task.Id,
                linkedNow: false,
                $"This task was already linked to {already} by the time the daemon picked the request "
                + "up, so no session was dispatched: a second session would have created a second card "
                + "for work that already has one.",
                cancellationToken);
            return false;
        }

        // The decider's first publication rule, enforced again for the same reason as the one
        // above. TaskDecider.RequestWorkItemPublication refuses an abandoned task because a card
        // for it would put work on somebody's board that nobody here intends to do, and abandoning
        // *after* the request is asking for the same thing a moment later: the request outlives
        // the intent behind it. Abandoning drops the marker now (TaskAggregate.Apply(TaskAbandoned)),
        // so this is the second lock on the same door rather than the only one — and it is the one
        // standing where the card would actually be written.
        if (aggregate.State == TaskState.Abandoned)
        {
            await CompleteAsync(
                task.Id,
                linkedNow: false,
                "This task was abandoned before the daemon picked the request up, so no session was "
                + "dispatched and nothing was put on a board: a card for abandoned work is work "
                + "nobody here intends to do.",
                cancellationToken);
            return false;
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

        // Recorded before anything is spawned, which is RunLauncher's order (RunDispatched, then
        // spawn, then RunProcessStarted) and it is the order for the same reason. The fence makes
        // a concurrent writer lose; committing first is what makes a *crash* lose too. Spawned
        // first, this would leave a window — a dropped connection on the save, a kill -9, a power
        // cut — where a live session is creating a card and the stream has no record that anything
        // was dispatched, so the next sweep starts a second one against the same request. Two
        // sessions mean two cards, and a duplicate card is a human's cleanup rather than a retry
        // here. Origin incident (2026-08-21): the pre-PR review of this branch traced both paths.
        session.Events.Append(task.Id, expectedVersion: fence!.Version + 1, new WorkItemPublicationDispatched(
            task.Id, sessionId, node.NodeId, DateTimeOffset.UtcNow, model));
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            // Somebody else moved the task between the read and the append — a link landing, or
            // another sweep getting there first. Nothing has been spawned, so there is nothing to
            // stop and nothing to record: the request was simply not this sweep's after all.
            logger.LogDebug(
                "Task {TaskId} advanced while its publication was being dispatched; leaving it", task.Id);
            return null;
        }

        SpawnedAgent agent = await executor.SpawnAsync(
            // RunId doubles as the artifact key and there is no run here: a publication has no
            // worktree, no branch, and no lease. The session's own id names its directory, which
            // is what WorkItemPublicationDispatched records so the prompt and stream stay findable.
            new AgentSpawnRequest(
                sessionId, sessionId, project.RepositoryPath, prompt,
                ExecutorMode.Subscription, model, project.SkipPermissions),
            cancellationToken);

        // Everything past the spawn is handled here rather than by the sweep's catch-all, because
        // this is the only place that still holds the session's identity and the two failures are
        // not the same fact. Before the spawn, nothing is running and "the daemon could not run
        // the publication session" is true. After it, an agent is writing a card, and the sweep's
        // handler would end the publication as if nothing had started: no kill, no caution to look
        // at the board, and completing clears the pending marker and the recorded session — so the
        // live one is invisible to AdoptStrandedAsync, the request is publishable again, and an
        // operator reading "could not run" runs push-to-jira and gets the second card this class
        // exists to prevent. Origin incident (2026-08-21): the pre-PR review of this branch traced
        // it from a transient store failure inside IsLinkedAsync while a session was mid-flight.

        // Whether the session has been handed to the wait yet, which is what decides who stops it
        // if the daemon is told to stop.
        bool waiting = false;
        try
        {
            await RecordProcessAsync(task.Id, sessionId, agent, cancellationToken);

            logger.LogInformation(
                "Task {TaskId}: card publication session {SessionId} dispatched (pid {ProcessId}, model {Model}, artifacts in {Directory})",
                task.Id, sessionId, agent.ProcessId, model.Value, RunPaths.RunDirectory(sessionId));

            waiting = true;
            (bool linked, string outcome) = await WaitAsync(task.Id, sessionId, agent, cancellationToken);
            await CompleteAsync(task.Id, linked, outcome, cancellationToken);
            return linked;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The daemon is stopping. Past the wait the session is no longer this method's to
            // stop: either WaitAsync's shutdown path stopped it and recorded the outcome itself,
            // or the wait returned and the session is already over, and a cancelled completion
            // after that leaves a dispatch with its process on the stream, which is exactly what
            // adoption reads on the next start.
            // Before the wait, nothing has dealt with it. RecordProcessAsync lets a cancelled save
            // out on purpose — its own catch filter excludes cancellation — so a SIGTERM landing
            // between the spawn and that append arrives here with a live session behind it and
            // nothing on the stream naming its process. Left alone that is the worst of both: the
            // agent outlives the daemon and keeps writing a card, and the restart's adoption finds
            // a dispatch with no process recorded beside it, which by contract does not terminate
            // anything and does complete the publication — so the request is publishable again
            // while a detached session is still filing against it, which is the second card this
            // class exists to prevent. Origin incident (2026-08-22): the pre-PR review of this
            // branch traced it from h9k daemon stop inside that window.
            if (!waiting)
            {
                await StopForShutdownAsync(task.Id, agent);
            }

            throw;
        }
        catch (Exception exception)
        {
            // The session is stopped for the same reason the timeout path stops one: nobody is
            // watching it any more, nothing will record what it does next, and a detached session
            // still writing a card is exactly how a surprise card arrives on a board.
            logger.LogError(
                exception,
                "Task {TaskId}: the card-publication session (pid {ProcessId}) could not be seen through to an "
                + "outcome; stopping it and recording that",
                task.Id, agent.ProcessId);
            Terminate(task.Id, agent);
            await CompleteAsync(
                task.Id,
                linkedNow: false,
                $"The session was dispatched and the daemon then lost track of it: {exception.Message}. It was "
                + $"stopped without a verified card key. Its prompt and transcript are in "
                + $"{RunPaths.RunDirectory(sessionId)}. {CheckTheBoard}",
                cancellationToken);
            return false;
        }
    }

    /// <summary>
    /// Record which process the dispatched session turned out to be, in its own session because
    /// the dispatch is already committed and this is a second observation rather than part of it.
    /// <para>
    /// A failure here is logged and not thrown. The session is running and this method's caller
    /// still holds its identity in memory, so this sweep can see it through to an outcome either
    /// way; letting the exception out would land in the caller's catch, which records the
    /// publication as over while a live session is still writing the card. What is lost is only
    /// the ability of a <em>later</em> daemon to ask about it, and adoption already has an honest
    /// answer for a dispatch with no process recorded beside it.
    /// </para>
    /// </summary>
    private async Task RecordProcessAsync(
        Guid taskId, Guid sessionId, SpawnedAgent agent, CancellationToken cancellationToken)
    {
        try
        {
            await using IDocumentSession session = store.LightweightSession();
            session.Events.Append(taskId, new WorkItemPublicationSessionStarted(
                taskId, sessionId, agent.ProcessId, agent.StartedAt));
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                exception,
                "Task {TaskId}: the card-publication session (pid {ProcessId}) was spawned but its process "
                + "could not be recorded; a restart will not be able to ask about it",
                taskId, agent.ProcessId);
        }
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
        bool timedOut = false;
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
            timedOut = true;
        }
        catch (OperationCanceledException)
        {
            await StopForShutdownAsync(taskId, agent);
            throw;
        }

        bool linked = await IsLinkedAsync(taskId, cancellationToken);
        if (linked)
        {
            return (true, Summarize(result)
                ?? "The session created the card and reported it through h9k task link-jira.");
        }

        // Everything from here ends the errand with no link, and completing clears the pending
        // marker — so each of these carries the caution, for the same reason the adoption and
        // shutdown paths do. None of them observed the absence of a card; they observed the
        // absence of a report, and a session stopped mid-flight is the likeliest of the three to
        // have left one behind.
        string what = timedOut
            ? $"The session was still running after {_options.CardPublicationTimeout} and was stopped "
              + $"without a verified card key. Its prompt and transcript are in {RunPaths.RunDirectory(sessionId)}."
            : Summarize(result) is { } said
                ? $"The session ended without a verified card key. It said: {said}"
                : "The session ended without a verified card key and left no result to read. Its prompt and "
                  + $"transcript are in {RunPaths.RunDirectory(sessionId)}.";

        return (false, $"{what} {CheckTheBoard}");
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

    /// <summary>
    /// A spawned session stopped because the daemon is, and the outcome recorded for it.
    /// <para>
    /// The session is detached and would outlive the daemon, so it is stopped — and the outcome is
    /// recorded here rather than left for the next start, so the task stops reading as "a session
    /// is writing the card" the moment there is no session. The write needs a token of its own
    /// because the sweep's is already cancelled; if it does not land, <c>AdoptStrandedAsync</c>
    /// finishes the job when the daemon comes back, which is why this is best-effort rather than
    /// something to fail on.
    /// </para>
    /// </summary>
    private async Task StopForShutdownAsync(Guid taskId, SpawnedAgent agent)
    {
        Terminate(taskId, agent);
        try
        {
            using CancellationTokenSource shutdown = new(ShutdownRecordTimeout);
            await CompleteAsync(
                taskId,
                await IsLinkedAsync(taskId, shutdown.Token),
                "The daemon stopped while this session was writing the card, so the session was "
                + $"stopped with it. {CheckTheBoard}",
                shutdown.Token);
        }
        catch (Exception recording)
        {
            logger.LogWarning(
                recording,
                "Task {TaskId}: could not record the publication outcome during shutdown; the next "
                + "sweep adopts it", taskId);
        }
    }

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

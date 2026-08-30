using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Daemon.Worktrees;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Project;
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
/// verified card, how many sessions it adopted — ones it had dispatched on an earlier run and
/// never recorded an outcome for — how many requests it answered without any session running at
/// all, because a guard refused them at the point where the card would have been written, and how
/// many it ended on the ceiling because they belonged to a node that never came back.
/// <para>
/// The five are counted apart because the sweep's log line is read as a record of what happened
/// on somebody's board. A refusal folded into <paramref name="Dispatched" /> reads as an agent
/// session that ran against a real repository, which is the one thing a refusal is not. Origin
/// incident (2026-08-22): the second cycle of this branch's pre-PR review found every refusal
/// counted as a dispatch, so a sweep that turned down a request for an abandoned task logged
/// "1 session(s) ran, 0 produced a verified card" — and a sweep that did nothing but adopt a
/// stranded session logged nothing at all.
/// </para>
/// <para>
/// <paramref name="Expired" /> is apart from <paramref name="Adopted" /> for the same reason:
/// adoption watched a session end, and this one only watched a clock. Nobody here saw that
/// session at all.
/// </para>
/// </summary>
public sealed record CardPublicationSweepResult(
    int Dispatched, int Linked, int Adopted = 0, int Refused = 0, int Expired = 0);

/// <summary>
/// Turns a publication request into an agent session, and records what came of it (backlog 18).
/// <para>
/// The platform does not decide the card's content itself, and that is the central design
/// decision rather than a staging limitation. An issue type, a required field, and a routing rule
/// are one organisation's Jira configuration, and the teams that have them have them written down
/// already — so the session runs in the project's own repository, where those rules live as
/// skills, to work out what the card should look like, but it makes no Jira call itself
/// (Decisions Log #102): it composes a payload and submits it through <c>h9k task write-jira</c>,
/// which is the sole executor of every Jira write. What this class owns is everything around
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
    IWorktreeManager worktrees,
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
        + "and never reported it back through h9k task write-jira leaves the card there and this task "
        + "unlinked.";

    private readonly DaemonOptions _options = options.Value;

    /// <summary>One sweep over this owner's outstanding publication requests.</summary>
    public async Task<CardPublicationSweepResult> PollOnceAsync(CancellationToken cancellationToken)
    {
        // Adoption comes first, and for the same reason the dispatch query excludes a dispatched
        // request: what is already running is settled before anything new is started.
        (int adopted, int expired) = await AdoptStrandedAsync(cancellationToken);

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
        int refused = 0;
        foreach (TaskDetails task in pending)
        {
            try
            {
                switch (await PublishAsync(task, cancellationToken))
                {
                    case PublicationAttempt.CardLinked:
                        dispatched++;
                        linked++;
                        break;
                    case PublicationAttempt.SessionRan:
                        dispatched++;
                        break;
                    case PublicationAttempt.Refused:
                        refused++;
                        break;
                    case PublicationAttempt.NotThisSweeps:
                    default:
                        break;
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
                // It is counted as neither dispatched nor refused for that same reason: the tally
                // is a claim about what ran, and this is the one outcome that cannot make one. The
                // log below is what reports it, at a level louder than the sweep's summary rather
                // than hidden behind it.
                logger.LogError(exception, "Publication failed for task {TaskId}; recording it and moving on", task.Id);
                bool? linkedAfterFailure = await TryReadLinkedAsync(task.Id, cancellationToken);
                await CompleteAsync(
                    task.Id,
                    linkedAfterFailure is true,
                    $"The daemon could not run the publication session: {exception.Message}. "
                    + WhatTheLinkSays(linkedAfterFailure),
                    cancellationToken);
            }
        }

        return new CardPublicationSweepResult(dispatched, linked, adopted, refused, expired);
    }

    /// <summary>
    /// Publication sessions this node dispatched and never recorded an outcome for, finished now.
    /// <para>
    /// Without this a stopped daemon strands the task permanently. The dispatch is on the stream
    /// and the completion is not, and nothing else clears that: the dispatch sweep skips a request
    /// whose session has already been spawned (the rule that stops a second card),
    /// <c>h9k task push-to-jira</c> refuses while a publication is outstanding, and
    /// <c>h9k task write-jira</c> needs a card key that may not exist — so the task reads "a session
    /// is writing the card" forever with nothing writing anything. Origin incident (2026-08-21):
    /// the pre-PR review of this branch traced it from <c>h9k daemon stop</c> during the
    /// publication timeout window, which killed the session between the two events.
    /// </para>
    /// <para>
    /// Adoption proper is scoped to this node because a pid is only meaningful on the machine it
    /// belongs to, the same rule run adoption follows. A session found alive is waited on rather
    /// than assumed dead: the daemon can be restarted while a detached session it spawned is still
    /// going, and killing that one would throw away a card it may be halfway through creating.
    /// </para>
    /// <para>
    /// A dispatch recorded against <em>another</em> node is not adopted — nothing here can ask
    /// that machine anything — but it is not left standing forever either. Past
    /// <see cref="DaemonOptions.ForeignPublicationCeiling" /> it is ended on the clock alone,
    /// which is the only way out of the one stranding adoption cannot cover. Origin incident
    /// (2026-08-22): the pre-PR review of this branch traced it from a machine rename, which gives
    /// the same install a new node identity — so every publication the old identity dispatched is
    /// foreign to the daemon that comes back, and nothing else clears it: the dispatch sweep skips
    /// it, push-to-jira refuses while it is outstanding, link-jira needs a card key that may not
    /// exist, and abandoning keeps the marker on purpose.
    /// </para>
    /// </summary>
    private async Task<(int Adopted, int Expired)> AdoptStrandedAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<TaskDetails> stranded;
        await using (IQuerySession query = store.QuerySession())
        {
            stranded = await query.Query<TaskDetails>()
                .Where(task => task.PendingPublicationProvider != null)
                .Where(task => task.PublicationSessionDispatched)
                .OrderBy(task => task.PublicationRequestedAt)
                .ToListAsync(cancellationToken);
        }

        int adopted = 0;
        int expired = 0;
        foreach (TaskDetails task in stranded)
        {
            if (task.PublicationSessionNodeId != node.NodeId)
            {
                expired += await ExpireForeignAsync(task, cancellationToken) ? 1 : 0;
                continue;
            }

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

                bool? linked = await TryReadLinkedAsync(task.Id, cancellationToken);
                await CompleteAsync(
                    task.Id,
                    linked is true,
                    "The daemon stopped while this publication's session was running, and picking it "
                    + $"back up failed: {exception.Message}. {WhatTheLinkSays(linked)}",
                    cancellationToken);
                adopted++;
            }
        }

        return (adopted, expired);
    }

    /// <summary>
    /// A publication dispatched by another node, ended once it has stood longer than any live
    /// session could. Returns whether it was ended.
    /// <para>
    /// Nothing is terminated and nothing is waited on: the process identity on the task belongs to
    /// a machine this one cannot ask, so the only honest act available is to stop the task waiting
    /// on it. The ceiling is what keeps that from cutting a live session short — a node that is
    /// running stops its own session at <see cref="DaemonOptions.CardPublicationTimeout" /> and
    /// records the outcome itself, so anything still dispatched an hour later is a node that is
    /// not coming back.
    /// </para>
    /// </summary>
    private async Task<bool> ExpireForeignAsync(TaskDetails task, CancellationToken cancellationToken)
    {
        // Written by the same event that sets the dispatched flag, so this cannot be null on a
        // task this method sees. It is read as an option rather than asserted because an age
        // nobody recorded is not an age to act on, and leaving the request standing is the
        // recoverable half of that choice.
        if (task.PublicationSessionDispatchedAt is not { } dispatchedAt
            || DateTimeOffset.UtcNow - dispatchedAt <= _options.ForeignPublicationCeiling)
        {
            return false;
        }

        string where = await DescribeNodeAsync(task.PublicationSessionNodeId, cancellationToken);
        logger.LogWarning(
            "Task {TaskId}: the publication session dispatched by {Node} at {DispatchedAt:u} has stood longer "
            + "than {Ceiling} with no outcome — ending the request here",
            task.Id, where, dispatchedAt, _options.ForeignPublicationCeiling);

        bool? linked = await TryReadLinkedAsync(task.Id, cancellationToken);
        await CompleteAsync(
            task.Id,
            linked is true,
            $"This publication's session was dispatched by {where} at {dispatchedAt:u} and no outcome was ever "
            + $"recorded for it. Only the node that spawned a session can judge it, and this one has stood for "
            + $"more than {_options.ForeignPublicationCeiling}, so the request is ended here rather than left "
            + $"hanging over the task forever. {WhatTheLinkSays(linked)}",
            cancellationToken);
        return true;
    }

    /// <summary>
    /// The node a stranded dispatch belongs to, named the way somebody could act on it. The
    /// machine name is the useful half — after a rename it is the previous name of this very
    /// machine — and a node that is not registered here is said to be that rather than described.
    /// </summary>
    private async Task<string> DescribeNodeAsync(Guid? nodeId, CancellationToken cancellationToken)
    {
        if (nodeId is not { } id)
        {
            return "a node this record does not name";
        }

        await using IQuerySession query = store.QuerySession();
        NodeDetails? details = await query.LoadAsync<NodeDetails>(id, cancellationToken);
        return details is null
            ? $"node {id}, which is not registered here"
            : $"{details.MachineName} (node {id})";
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
            bool? linkedWithoutAProcess = await TryReadLinkedAsync(task.Id, cancellationToken);
            await CompleteAsync(
                task.Id,
                linkedWithoutAProcess is true,
                "This publication's session was dispatched and no process was ever recorded beside it, "
                + "so nothing can say whether it ran. "
                + WhatTheLinkSays(linkedWithoutAProcess),
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
    /// What one publication request came to. An in-process outcome that is never persisted, which
    /// is the one thing an enum is for here (AGENTS.md, coding standards): what the stream records
    /// is the outcome text on the task, and this only decides how the sweep counts the request.
    /// </summary>
    private enum PublicationAttempt
    {
        /// <summary>
        /// The request was not this sweep's to act on after all — another sweep beat it to the
        /// dispatch, or the request was already resolved. Nothing was written and nothing counts.
        /// </summary>
        NotThisSweeps,

        /// <summary>
        /// A guard answered the request where the card would have been written, so no session was
        /// ever spawned. The request is over, and nothing ran against anybody's repository.
        /// </summary>
        Refused,

        /// <summary>A session was spawned and finished without a verified card key.</summary>
        SessionRan,

        /// <summary>A session was spawned and the task came out carrying a verified card.</summary>
        CardLinked,
    }

    /// <summary>
    /// One request: spawn the session, wait for it under a ceiling, then record what the task
    /// actually came out carrying.
    /// </summary>
    private async Task<PublicationAttempt> PublishAsync(TaskDetails task, CancellationToken cancellationToken)
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
            return PublicationAttempt.NotThisSweeps;
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
                // True, and read rather than assumed: the branch condition *is* the read, taken
                // off the aggregate under the fence a line above. WorkItemPublicationCompleted's
                // contract is that Linked says what the task carries, and the task carries a
                // verified key here — the whole reason this refusal exists. Origin incident
                // (2026-08-22): the second cycle of this branch's pre-PR review found this path
                // hardcoding false, so the one outcome that knows for certain the task is linked
                // was the one recording on the stream that it was not.
                linkedNow: true,
                $"This task was already linked to {already} by the time the daemon picked the request "
                + "up, so no session was dispatched: a second session would have created a second card "
                + "for work that already has one.",
                cancellationToken);
            return PublicationAttempt.Refused;
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
            return PublicationAttempt.Refused;
        }

        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(aggregate.ProjectId, cancellationToken);
        if (project is null)
        {
            await CompleteAsync(task.Id, false, "The task's project is not registered on this node.", cancellationToken);
            return PublicationAttempt.Refused;
        }

        // The session reads this project's card-authoring skills, so it needs a working tree, and
        // a project with a home does not have one at its repository path: that path names the
        // bare clone inside repo/, which holds refs and objects and no files at all. repo/dev is
        // the checkout the home keeps for exactly this kind of reading. A project registered
        // before homes existed still points at an ordinary clone, so that stays the fallback.
        string checkout = ProjectCheckout.ForReading(project);
        if (!Directory.Exists(checkout) || ProjectCheckout.IsBare(checkout))
        {
            await CompleteAsync(
                task.Id,
                false,
                $"There is no working checkout of this project at {checkout}, and the session runs in one "
                + "to read this project's card rules. Create the home's repo/dev worktree with "
                + $"h9k project init {project.Name}, or point the project at an existing checkout with "
                + $"h9k project set {project.Name} --repo <path>, then run h9k task push-to-jira again.",
                cancellationToken);
            return PublicationAttempt.Refused;
        }

        // repo/dev is cut once by h9k project init and otherwise never touched, and this session
        // is spawned into it precisely to read the project's own card-authoring rules — so a
        // checkout months behind the remote answers with rules nobody follows any more. It is
        // fast-forwarded best-effort here, and the outcome is logged either way, because the
        // defect this closes was less the staleness than that nothing anywhere said the checkout
        // was behind. Only the home's own dev/ is moved: a project registered before homes existed
        // reads from somebody's ordinary clone, and that one is theirs to move. Origin incident
        // (2026-08-23): the second cycle of this branch's pre-PR review followed a card being
        // authored by rules as of the commit repo/dev was created at, months earlier, silently,
        // and the third found the refresh fetching this project's recorded repository path — which
        // is not the clone repo/dev reads through once --keep-repo-path or project set --repo has
        // separated them, so the freshness it reported was measured off refs nobody had updated.
        // The checkout is all that is handed over now; the repository is git's answer, not ours.
        if (ProjectCheckout.IsHomeDevWorktree(project, checkout))
        {
            CheckoutRefresh refresh = await worktrees.RefreshReadingCheckoutAsync(
                checkout, project.BaseBranch, cancellationToken);
            logger.Log(
                refresh.UpToDate ? LogLevel.Information : LogLevel.Warning,
                "Publication session for task {TaskId} reads {Checkout}, which {Detail}",
                task.Id, checkout, refresh.Detail);
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
            return PublicationAttempt.Refused;
        }

        // The sweep's snapshot names the request; it does not get to describe the work. PollOnceAsync
        // materialises its pending list once and then works through it one at a time, each publication
        // blocking on a session for up to CardPublicationTimeout, so a request's document can be many
        // minutes old by the time its turn comes — and publication is requested from Draft, which is
        // exactly the state h9k task revise edits, with no rule against doing both. Every guard above
        // re-reads the aggregate under the fence for that reason, and the card's own text is read from
        // the same moment. Origin incident (2026-08-22): the pre-PR review of this branch found the
        // freshly-read aggregate and the sweep's stale document mixed in the call below, so a task
        // revised while an earlier session ran would have had its card written from an objective and
        // criteria it no longer carried, and nothing downstream checks a card against its task.
        TaskDetails current = await session.LoadAsync<TaskDetails>(task.Id, cancellationToken) ?? task;

        Guid sessionId = DomainId.New();
        AgentModel model = _options.ResolveModel(AgentRole.Publication, aggregate.Model, project.Model);
        string prompt = AgentPromptBuilder.BuildCardPublication(
            current,
            project,
            checkout,
            site.GetLeftPart(UriPartial.Authority),
            aggregate.PendingPublicationProjectKey,
            $"h9k task write-jira {task.Id}",
            project.BacklogRoutingGuidance);

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
            return PublicationAttempt.NotThisSweeps;
        }

        SpawnedAgent agent = await executor.SpawnAsync(
            // RunId doubles as the artifact key and there is no run here: a publication has no
            // worktree, no branch, and no lease. The session's own id names its directory (the
            // platform-global location — a publication session belongs to no task directory,
            // never having a RunDispatched of its own), which is what
            // WorkItemPublicationDispatched records so the prompt and stream stay findable.
            new AgentSpawnRequest(
                sessionId, sessionId, checkout, RunPaths.GlobalDirectory(sessionId), prompt,
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
                task.Id, sessionId, agent.ProcessId, model.Value, RunPaths.GlobalDirectory(sessionId));

            waiting = true;
            (bool linked, string outcome) = await WaitAsync(task.Id, sessionId, agent, cancellationToken);
            await CompleteAsync(task.Id, linked, outcome, cancellationToken);
            return linked ? PublicationAttempt.CardLinked : PublicationAttempt.SessionRan;
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
            bool? linked = await TryReadLinkedAsync(task.Id, cancellationToken);
            await CompleteAsync(
                task.Id,
                linked is true,
                $"The session was dispatched and the daemon then lost track of it: {exception.Message}. It was "
                + $"stopped {(linked is true ? "after it reported its card key" : "without a verified card key")}. "
                + $"Its prompt and transcript are in {RunPaths.GlobalDirectory(sessionId)}. "
                + WhatTheLinkSays(linked),
                cancellationToken);

            // It ran, whatever became of watching it: this is the one place that knows the
            // difference between a session that was spawned and a request that was refused.
            return PublicationAttempt.SessionRan;
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
    /// <c>h9k task write-jira</c> can have set, and only after reading the card back from Jira. A
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
                RunPaths.StreamFile(RunPaths.GlobalDirectory(sessionId)), agent.ProcessId, agent.StartedAt,
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
                ?? "The session created the card and reported it through h9k task write-jira.");
        }

        // Everything from here ends the errand with no link, and completing clears the pending
        // marker — so each of these carries the caution, for the same reason the adoption and
        // shutdown paths do. None of them observed the absence of a card; they observed the
        // absence of a report, and a session stopped mid-flight is the likeliest of the three to
        // have left one behind.
        string what = timedOut
            ? $"The session was still running after {_options.CardPublicationTimeout} and was stopped "
              + $"without a verified card key. Its prompt and transcript are in {RunPaths.GlobalDirectory(sessionId)}."
            : Summarize(result) is { } said
                ? $"The session ended without a verified card key. It said: {said}"
                : "The session ended without a verified card key and left no result to read. Its prompt and "
                  + $"transcript are in {RunPaths.GlobalDirectory(sessionId)}.";

        // A session that submitted a write through h9k task write-jira and hit an unauthenticated
        // twg has not left an unreported card behind: the write is recorded pending on the task
        // (TaskDetails.PendingJiraWriteIsAuthFailure) and the retry sweep finishes it once 'twg
        // login' succeeds. Reporting the generic CheckTheBoard caution there guesses at a stranded
        // card that was never filed and sends the operator to push-to-jira again instead of to the
        // retry that is already queued (independent pre-PR review, conformance lens, cycle 1).
        string tail = await IsPendingAuthFailureAsync(taskId, cancellationToken)
            ? "twg was not authenticated when the session submitted its write, so the write is "
              + "recorded pending on the task rather than lost: it will retry automatically once you "
              + "run 'twg login', and there is no card to check the board for yet."
            : CheckTheBoard;

        return (false, $"{what} {tail}");
    }

    /// <summary>
    /// Whether the task now carries an external reference. Read fresh from the store rather than
    /// from anything held in memory: the reference was written by a different process — the agent's
    /// own <c>h9k task write-jira</c> — while this method was waiting.
    /// </summary>
    private async Task<bool> IsLinkedAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();
        TaskDetails? task = await session.LoadAsync<TaskDetails>(taskId, cancellationToken);
        return task?.ExternalReference.IsNotBlank() is true;
    }

    /// <summary>
    /// Whether the task's own write-jira submission is sitting on an unauthenticated twg —
    /// distinguishes "no card was ever filed" from "a card write is queued for the retry sweep"
    /// for <see cref="WaitAsync"/>'s no-link outcome, the same fact <c>h9k task show</c> and the
    /// attention pane's own needs-you row already read off this field.
    /// </summary>
    private async Task<bool> IsPendingAuthFailureAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();
        TaskDetails? task = await session.LoadAsync<TaskDetails>(taskId, cancellationToken);
        return task?.PendingJiraWriteIsAuthFailure is true;
    }

    /// <summary>
    /// The same read, for the paths that record an outcome after something has already gone
    /// wrong: null is "nobody could read it", because a fact this class failed to observe is not
    /// a fact it may then state.
    /// <para>
    /// Every one of those paths used to record <c>Linked: false</c> outright, and that is a guess
    /// wherever the session's own <c>h9k task write-jira</c> landed before the failure did. The
    /// event's contract is that Linked is read off the task's own state rather than assumed, and
    /// an adopted session is precisely the case where the agent has been running unwatched for a
    /// daemon restart's worth of time. Origin incident (2026-08-22): the third cycle of this
    /// branch's pre-PR review found the adoption failure path recording "no card produced", plus
    /// the caution to go find an unrecorded one, against a task already carrying a verified key.
    /// </para>
    /// </summary>
    private async Task<bool?> TryReadLinkedAsync(Guid taskId, CancellationToken cancellationToken)
    {
        try
        {
            return await IsLinkedAsync(taskId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Task {TaskId}: could not read whether the task is linked while recording a publication "
                + "outcome; the outcome will say so rather than claim a card either way", taskId);
            return null;
        }
    }

    /// <summary>
    /// What an outcome says about the link, given what the read said. The caution belongs to the
    /// outcomes that end with no card recorded; a task that came out of one carrying a verified
    /// key needs no board check, and telling its reader to go looking would send them hunting for
    /// a duplicate that is not there. A read nobody managed says both halves.
    /// </summary>
    private static string WhatTheLinkSays(bool? linked) => linked switch
    {
        true => "The task carries a verified card key, reported by the session through h9k task write-jira "
            + "before this, so the card exists and is recorded.",
        false => CheckTheBoard,
        _ => $"Whether the task ended up carrying a card key could not be read either. {CheckTheBoard}",
    };

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

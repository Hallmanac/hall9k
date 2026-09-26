using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.RunSkills;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.RunSkills;

/// <summary>How many discoveries this tick answered and how many ledger writes it made, for the loop's own log line.</summary>
public sealed record RunSkillSweepResult(int Discovered, int Pushed);

/// <summary>
/// The whole run-skill seam on the daemon's side (idea b9b09779, piece 4): the only place a
/// discovery session is dispatched, and the only place <c>run-skill.md</c> on
/// <see cref="LedgerRefRegistry.RunSkill"/> is ever written.
/// <para>
/// Two independent jobs per eligible project, every tick, the same split
/// <c>PromptAddendaSweepEngine</c> already uses and for the same reason.
/// <see cref="DiscoverAsync"/> answers an outstanding
/// <see cref="ProjectRunSkillDiscoveryRequested"/>: it surveys the repository mechanically first
/// and, only when there is something to read, dispatches a read-only session to compose the
/// skill from it. <see cref="PushAsync"/> scans this node's own new
/// <see cref="ProjectRunSkillRecorded"/> events past a durable
/// <see cref="RunSkillSyncPosition"/> and writes each to the ledger.
/// </para>
/// <para>
/// Tools before tokens is the rule the discovery half is built around, and it has two teeth. The
/// survey (<see cref="RunSkillRepositorySurvey"/>) does the finding, so no session spends turns
/// on a directory listing; and a repository the survey finds nothing in produces the
/// none-discoverable skill right here, with no session dispatched at all, because there is
/// nothing for one to read and spending a session to learn that would be spending tokens on what
/// a tool already answered.
/// </para>
/// <para>
/// The session never writes the ledger. It composes markdown and reports it in its own summary;
/// this engine parses that, appends the event, and the push half — a separate pass, on a later
/// tick or this one — is what actually reaches <see cref="ILedger"/>. That split is the same
/// trust boundary the prompt addenda draw: content an agent session composed becomes something
/// every member's node reads, and it crosses only through the daemon.
/// </para>
/// </summary>
public sealed class RunSkillSweepEngine(
    IDocumentStore store,
    NodeContext node,
    ILedger ledger,
    NodeKeyStore keyStore,
    IExecutor executor,
    IProcessManager processManager,
    ProcessRunner processRunner,
    IOptions<DaemonOptions> options,
    ILogger<RunSkillSweepEngine> logger)
{
    private const int MaxConflictRetries = 5;

    private readonly DaemonOptions _options = options.Value;

    public async Task<RunSkillSweepResult> SweepOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ProjectDetails> projects;
        await using (IDocumentSession lookup = store.LightweightSession())
        {
            projects = await lookup.Query<ProjectDetails>()
                .Where(project => !project.IsArchived)
                .ToListAsync(cancellationToken);
        }

        int discovered = 0;
        int pushed = 0;
        foreach (ProjectDetails project in projects.Where(project => project.RepositoryPath.IsNotBlank()))
        {
            try
            {
                // At most one discovery per tick. A discovery is a synchronous spawn-and-wait
                // bounded by RunSkillDiscoveryTimeout, so answering every outstanding request in
                // one pass would make a tick's own ceiling that timeout times the number of
                // projects asking — and a fresh install registering several projects at once is
                // exactly when they all ask. Everything else on the tick, every other project's
                // ledger push included, would wait behind it. One per tick keeps the tick bounded
                // by a single timeout and costs only a poll interval per extra project.
                if (discovered == 0 && project.RunSkillDiscoveryOutstanding
                    && await DiscoverAsync(project, cancellationToken))
                {
                    discovered++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Recorded as a failed discovery rather than retried silently every tick: an
                // outstanding request the sweep cannot answer would otherwise redispatch a
                // session forever against whatever is actually broken, and nothing anywhere
                // would say so. Asking again is a deliberate act (--discover-run-skill).
                logger.LogWarning(
                    exception, "Run-skill discovery failed for project {ProjectId}", project.Id);
                await RecordDiscoveryFailureAsync(
                    project.Id, $"the discovery could not be run: {exception.Message}", cancellationToken);
            }

            try
            {
                pushed += await PushAsync(project, cancellationToken);
                await ClearPushFailureAsync(project.Id, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception, "Run-skill push failed for project {ProjectId}; will retry next sweep", project.Id);
                await RecordPushFailureAsync(project.Id, exception.Message, cancellationToken);
            }
        }

        return new RunSkillSweepResult(discovered, pushed);
    }

    /// <summary>
    /// Answers one project's outstanding discovery request. True when it reached a recorded
    /// outcome (a skill, or an honest failure); false when it deliberately did nothing this tick.
    /// </summary>
    private async Task<bool> DiscoverAsync(ProjectDetails project, CancellationToken cancellationToken)
    {
        if (!project.HomeDirectory.HasValue)
        {
            // Nothing to read: without a home there is no dev worktree, and this engine never
            // creates one — a discovery session reads a checkout that already exists rather than
            // cutting one of its own. Recorded as a failure naming the fix rather than left
            // outstanding, which would redispatch this same nothing on every tick forever.
            await RecordDiscoveryFailureAsync(
                project.Id,
                "this project has no home directory, so there is no checkout to read. Give it one "
                + $"(h9k project init {project.Name}) and ask again with "
                + $"h9k project set {project.Name} --discover-run-skill.",
                cancellationToken);
            return true;
        }

        // Where a reading session runs is the platform's own answer, never this engine's
        // (ProjectCheckout.ForReading): the home's repo/dev when it is materialised, and
        // otherwise the recorded repository path. Hardcoding repo/dev here refused a whole
        // legitimate shape of project outright — one registered with --repo, whose home's repo/
        // is deliberately left unmaterialised and whose recorded path is the ordinary working
        // clone every other reading session is already given.
        string worktreePath = ProjectCheckout.ForReading(project);
        if (!Directory.Exists(worktreePath) || ProjectCheckout.IsBare(worktreePath))
        {
            // Bare as well as absent, per ProjectCheckout.IsBare's own contract: a homed project
            // whose repo/dev was wiped falls back to the home's BARE clone, which carries refs
            // and objects and not one file to read. Refused before a session is spawned into it
            // rather than after one reports it found nothing.
            //
            // But not on sight, and this is why: a home being MADE looks exactly like a home that
            // is broken. h9k project add registers the project and asks for the discovery, then
            // bare-clones the repository and cuts repo/dev, which takes as long as the repository
            // is big; a tick landing in that window used to consume the request outright and tell
            // the operator to repair a home that finished materialising a minute later, and
            // nothing ever asked again (independent pre-PR review, cycle 1, both lenses). So an
            // absent checkout leaves the request outstanding and costs only a poll interval,
            // until RunSkillCheckoutGrace has passed with still nothing there — at which point it
            // is no longer a clone in flight, and the honest failure is recorded as before.
            if (project.RunSkillDiscoveryRequestedAt is { } requestedAt
                && DateTimeOffset.UtcNow - requestedAt < _options.RunSkillCheckoutGrace)
            {
                logger.LogDebug(
                    "Project {ProjectId}: no checkout at {WorktreePath} yet — leaving the run-skill discovery "
                    + "request outstanding until {Grace} has passed since it was asked for",
                    project.Id, worktreePath, _options.RunSkillCheckoutGrace);
                return false;
            }

            await RecordDiscoveryFailureAsync(
                project.Id,
                $"this project has no checkout with files in it to read ({worktreePath}) after "
                + $"{_options.RunSkillCheckoutGrace} of waiting for one to appear, so there is nothing to "
                + $"compose a run skill from. Repair the home (h9k project init {project.Name}) and ask "
                + $"again with h9k project set {project.Name} --discover-run-skill.",
                cancellationToken);
            return true;
        }

        RunSkillSurvey survey = RunSkillRepositorySurvey.Survey(worktreePath);
        string headCommit = await HeadCommitAsync(worktreePath, cancellationToken);

        if (!survey.HasAnything)
        {
            // Tools before tokens: the survey already answered the only question a session could
            // have answered here, so the none-discoverable skill is composed and recorded right
            // here, authored by the platform rather than attributed to a session that never ran.
            logger.LogInformation(
                "Project {ProjectId}: the repository survey found nothing that says how to run it — recording "
                + "the none-discoverable run skill without dispatching a session",
                project.Id);
            await RecordRunSkillAsync(
                project,
                RunSkillDocument.ComposeNoneDiscoverable(RunSkillRepositorySurvey.LookedFor),
                RunSkillShape.NoneDiscoverable, RunSkillAuthor.Platform, headCommit, cancellationToken);
            return true;
        }

        RunSkillComposition composition = await ComposeAsync(project, worktreePath, headCommit, survey, cancellationToken);
        if (!composition.Usable)
        {
            await RecordDiscoveryFailureAsync(project.Id, composition.Problem!, cancellationToken);
            return true;
        }

        await RecordRunSkillAsync(
            project, RunSkillDocument.Compose(composition.Shape, composition.Markdown), composition.Shape,
            RunSkillAuthor.DiscoverySession, headCommit, cancellationToken);
        return true;
    }

    /// <summary>
    /// Spawns the read-only discovery session, waits for it under
    /// <see cref="DaemonOptions.RunSkillDiscoveryTimeout"/> and
    /// <see cref="DaemonOptions.RunSkillDiscoveryMaxTurns"/> (bounded twice over, the same shape
    /// the stack assessment uses), and parses its trailer. Every failure — a spawn that did not
    /// take, an expired wait, an unreadable trailer — comes back as an unusable composition with
    /// the reason stated, never as a composed document this method invented.
    /// <para>
    /// <see cref="ProjectRunSkillDiscoveryDispatched"/> is appended BEFORE the spawn, not after:
    /// a daemon restart mid-wait must find that fact on the stream, or the next tick reads the
    /// request as still outstanding and puts a second session onto the same repository.
    /// </para>
    /// </summary>
    private async Task<RunSkillComposition> ComposeAsync(
        ProjectDetails project, string worktreePath, string headCommit, RunSkillSurvey survey,
        CancellationToken cancellationToken)
    {
        Guid sessionId = DomainId.New();
        AgentModel model = _options.ResolveModel(AgentRole.Synthesis, taskModel: null, project.Model);
        string sessionName = SessionRoleName.For(DomainId.Short(project.Id), SessionRoleName.RunSkillDiscovery);
        string sessionDirectory = ProjectHomePaths.RunSkillDirectory(project.HomeDirectory.Value);
        string artifactName = DiscoveryArtifactName(sessionId);
        string prompt = AgentPromptBuilder.BuildRunSkillDiscovery(
            project, worktreePath, headCommit, survey, ProjectDecider.RunSkillMaximumLength,
            commandTimeout: _options.VerifyGateTimeout);

        await using (IDocumentSession dispatchSession = store.LightweightSession())
        {
            dispatchSession.Events.Append(project.Id, new ProjectRunSkillDiscoveryDispatched(
                project.Id, sessionId, model, sessionName, headCommit, DateTimeOffset.UtcNow));
            await dispatchSession.SaveChangesAsync(cancellationToken);
        }

        SpawnedAgent? unfinished = null;
        AgentResult? result;
        try
        {
            SpawnedAgent agent;
            try
            {
                agent = await executor.SpawnAsync(
                    new AgentSpawnRequest(
                        // No run owns this session: it is project-scoped, dispatched off a project
                        // setting rather than inside anybody's run. Guid.Empty is the honest
                        // sentinel for that, and the executor only ever puts RunId in a log line
                        // and an error message, never in a path or a decision.
                        RunId: Guid.Empty, sessionId, worktreePath, sessionDirectory, prompt,
                        ExecutorMode.Subscription, model, project.SkipPermissions,
                        SessionArtifactName: artifactName,
                        MaxTurns: _options.RunSkillDiscoveryMaxTurns)
                    {
                        SessionName = sessionName,
                    },
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    exception, "Project {ProjectId}: the run-skill discovery session could not be spawned", project.Id);
                return RunSkillComposition.Unreadable(
                    $"the discovery session could not even be started ({exception.Message})");
            }

            unfinished = agent;
            string streamFile = RunPaths.SessionStreamFile(sessionDirectory, artifactName);

            using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(_options.RunSkillDiscoveryTimeout);
            try
            {
                SessionWaitResult wait = await SessionResultWaiter.WaitAsync(
                    streamFile, agent.ProcessId, agent.StartedAt, processManager, onOutput: null, timeoutSource.Token);
                unfinished = null;
                result = wait.Result;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                processManager.Terminate(agent.ProcessId, agent.StartedAt);
                unfinished = null;
                logger.LogWarning(
                    "Project {ProjectId}: the run-skill discovery session exceeded its {Timeout} bound and was terminated",
                    project.Id, _options.RunSkillDiscoveryTimeout);
                return RunSkillComposition.Unreadable(
                    $"the discovery session exceeded its {_options.RunSkillDiscoveryTimeout} bound and was terminated "
                    + "before it reported anything");
            }
        }
        catch (Exception exception)
        {
            Terminate(project.Id, unfinished);
            if (exception is OperationCanceledException)
            {
                throw;
            }

            logger.LogWarning(
                exception, "Project {ProjectId}: the run-skill discovery session failed before it could finish", project.Id);
            return RunSkillComposition.Unreadable(
                $"the discovery session failed before it could finish ({exception.Message})");
        }

        if (result?.IsError == true)
        {
            logger.LogWarning(
                "Project {ProjectId}: the run-skill discovery session ended in error: {Summary}",
                project.Id, result.Summary ?? "(no message)");
        }

        return RunSkillResultParser.Parse(result?.Summary);
    }

    /// <summary>
    /// This discovery's own prompt, stream, stderr and settings prefix inside the home's
    /// <c>run-skill/</c> directory — keyed by the session id, the same way
    /// <c>PrReviewEngine.ConformanceArtifactName</c> keys its own.
    /// <para>
    /// Every discovery for a project used to share one fixed path, and re-running one is a
    /// designed feature here (<c>--discover-run-skill</c>). The waiter starts tailing the stream
    /// the instant the spawn returns, which is before the child shell has opened and truncated
    /// its redirect target: a second discovery therefore read the FIRST one's result line as its
    /// own, force-terminated the live session a grace later, and recorded the honest-sounding but
    /// entirely wrong "the discovery session failed before it could finish" (independent pre-PR
    /// review, cycle 1, adversarial lens). A path no earlier session ever wrote to cannot be
    /// misread that way.
    /// </para>
    /// </summary>
    internal static string DiscoveryArtifactName(Guid sessionId) =>
        $"{SessionRoleName.RunSkillDiscovery}-{sessionId:N}";

    private void Terminate(Guid projectId, SpawnedAgent? agent)
    {
        if (agent is not { } session)
        {
            return;
        }

        try
        {
            processManager.Terminate(session.ProcessId, session.StartedAt);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception, "Project {ProjectId}: could not terminate the abandoned run-skill discovery session (pid {ProcessId})",
                projectId, session.ProcessId);
        }
    }

    /// <summary>
    /// The commit the composition is against, read by the daemon rather than asked of the
    /// session. Blank when it could not be read — an honest unknown on the event, never a
    /// plausible-looking value.
    /// </summary>
    private async Task<string> HeadCommitAsync(string worktreePath, CancellationToken cancellationToken)
    {
        try
        {
            ProcessResult result = await processRunner("git", ["rev-parse", "HEAD"], worktreePath, cancellationToken);
            return result.ExitCode == 0 ? result.StandardOutput.Trim() : string.Empty;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not read HEAD in {WorktreePath} for the run-skill discovery", worktreePath);
            return string.Empty;
        }
    }

    /// <summary>
    /// Appends the recorded skill, or — when the decider refuses the document — the honest
    /// failure instead.
    /// <para>
    /// The parser already checks the shared shape, but the length cap lives on the decider, where
    /// a hand-set file meets it too. A composed document past it would otherwise leave this
    /// method throwing into <see cref="SweepOnceAsync"/>'s own catch, which records a failure
    /// reading "the discovery could not be run" — a sentence that is simply false about a
    /// discovery that ran, read the repository, and wrote too much. Caught here so the reason a
    /// human reads is the rule that actually refused it.
    /// </para>
    /// </summary>
    private async Task RecordRunSkillAsync(
        ProjectDetails project, string content, RunSkillShape shape, RunSkillAuthor author, string headCommit,
        CancellationToken cancellationToken)
    {
        ProjectRunSkillRecorded recorded;
        try
        {
            recorded = ProjectDecider.RecordRunSkill(
                project.Id, content, shape, author, headCommit, project.OwnerId, DateTimeOffset.UtcNow);
        }
        catch (DomainValidationException refusal)
        {
            await RecordDiscoveryFailureAsync(
                project.Id, $"the composed run skill was refused: {refusal.Message}", cancellationToken);
            return;
        }

        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(project.Id, recorded);
        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task RecordDiscoveryFailureAsync(Guid projectId, string reason, CancellationToken cancellationToken)
    {
        try
        {
            await using IDocumentSession session = store.LightweightSession();
            session.Events.Append(
                projectId, ProjectDecider.FailRunSkillDiscovery(projectId, reason, DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Best-effort, like the prompt-addenda sweep's own failure recording: a failure
            // recording the failure is logged rather than allowed to mask what actually went
            // wrong. What the project is left in depends on how far the discovery got, and only
            // one of the two retries itself: a failure recorded BEFORE any session was dispatched
            // leaves the request outstanding, so the next tick tries again, while one recorded
            // after ProjectRunSkillDiscoveryDispatched already landed leaves the project in the
            // dispatched-and-nothing-recorded-since state h9k project show names — never
            // redispatched on its own, and asked for again by hand.
            logger.LogWarning(
                exception, "Could not record the run-skill discovery failure itself for project {ProjectId}", projectId);
        }
    }

    /// <summary>
    /// Scans this project's own event log past wherever the last sweep left off for a run-skill
    /// change this node's own stream carries, and writes the newest one to the ledger. Only the
    /// newest: unlike an addendum, there is exactly one path here, so writing every intermediate
    /// record in a batch would be several ledger commits that all end at the same content.
    /// </summary>
    private async Task<int> PushAsync(ProjectDetails project, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();

        RunSkillSyncPosition? position = await session.LoadAsync<RunSkillSyncPosition>(project.Id, cancellationToken);
        long sinceSequence = position?.LastScannedGlobalSequence ?? 0;

        IReadOnlyList<IEvent> candidates = await session.Events.QueryAllRawEvents()
            .Where(e => e.StreamId == project.Id && e.Sequence > sinceSequence)
            .OrderBy(e => e.Sequence)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return 0;
        }

        // A replicated recording can land on this stream behind a newer one (a catch-up answer
        // serves a pre-switch-on head after the tail), so a recording stamped older than the one
        // the projection applied is never pushed: it would overwrite the newer file in the ledger.
        // The projection is read after the events, so it has applied every event scanned above.
        ProjectDetails? applied = await session.LoadAsync<ProjectDetails>(project.Id, cancellationToken);
        int pushed = 0;
        if (candidates.Select(candidate => candidate.Data).OfType<ProjectRunSkillRecorded>()
            .LastOrDefault(candidate => applied?.RunSkill is not { } current || candidate.RecordedAt >= current.RecordedAt)
            is { } recorded)
        {
            (LedgerCommitter committer, LedgerSigningKey signingKey) = await IdentityAsync(session, project, cancellationToken);
            await WriteAsync(project.RepositoryPath, recorded, committer, signingKey, cancellationToken);
            pushed = 1;
        }

        long newPosition = candidates[^1].Sequence;
        if (position is null)
        {
            session.Store(new RunSkillSyncPosition { Id = project.Id, LastScannedGlobalSequence = newPosition });
        }
        else
        {
            position.LastScannedGlobalSequence = newPosition;
            session.Store(position);
        }

        await session.SaveChangesAsync(cancellationToken);
        return pushed;
    }

    private async Task<(LedgerCommitter Committer, LedgerSigningKey SigningKey)> IdentityAsync(
        IDocumentSession session, ProjectDetails project, CancellationToken cancellationToken)
    {
        OwnerAggregate owner =
            await session.Events.AggregateStreamAsync<OwnerAggregate>(project.OwnerId, token: cancellationToken)
            ?? throw new InvalidOperationException($"No owner {project.OwnerId} for project {project.Id}.");
        NodeSigningKey key = await keyStore.EnsureAsync(node.NodeId, cancellationToken);
        return (
            new LedgerCommitter(
                owner.Name.IsNotBlank() ? owner.Name : Environment.UserName,
                owner.Email.IsNotBlank() ? owner.Email : $"{node.NodeId}@hall9k.local"),
            new LedgerSigningKey(key.PrivateKeyPath));
    }

    private async Task WriteAsync(
        string repositoryPath, ProjectRunSkillRecorded recorded, LedgerCommitter committer,
        LedgerSigningKey signingKey, CancellationToken cancellationToken)
    {
        string refName = LedgerRefRegistry.RunSkill.RefspecSource;

        for (int attempt = 1; attempt <= MaxConflictRetries; attempt++)
        {
            LedgerFile current = await ledger.ReadAsync(
                repositoryPath, refName, LedgerRefRegistry.RunSkillPath, cancellationToken);
            if (current.Content == recorded.Content)
            {
                return;
            }

            LedgerWriteOutcome outcome = await ledger.WriteAsync(
                new LedgerWriteRequest(
                    repositoryPath, refName, LedgerRefRegistry.RunSkillPath, recorded.Content, current.BlobId,
                    $"Set the run skill ({recorded.Shape}, by {recorded.Author})", committer, signingKey),
                cancellationToken);
            if (outcome.Verdict == LedgerWriteVerdict.Written)
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"{LedgerRefRegistry.RunSkillPath} kept changing out from under the run-skill sync after "
            + $"{MaxConflictRetries} attempts; will retry next sweep.");
    }

    private async Task RecordPushFailureAsync(Guid projectId, string error, CancellationToken cancellationToken)
    {
        try
        {
            await using IDocumentSession session = store.LightweightSession();
            RunSkillSyncPosition position =
                await session.LoadAsync<RunSkillSyncPosition>(projectId, cancellationToken)
                ?? new RunSkillSyncPosition { Id = projectId };
            position.LastPushError = error;
            position.LastPushErrorAt = DateTimeOffset.UtcNow;
            session.Store(position);
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception, "Could not record the run-skill push failure itself for project {ProjectId}", projectId);
        }
    }

    /// <summary>The success-path mirror of <see cref="RecordPushFailureAsync"/> — cleared on every
    /// tick whose own <see cref="PushAsync"/> completed without throwing, whether or not it pushed
    /// anything, the same reasoning <c>PromptAddendaSweepEngine</c> records for its own.</summary>
    private async Task ClearPushFailureAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        RunSkillSyncPosition? position = await session.LoadAsync<RunSkillSyncPosition>(projectId, cancellationToken);
        if (position is not { LastPushError: not null })
        {
            return;
        }

        position.LastPushError = null;
        position.LastPushErrorAt = null;
        session.Store(position);
        await session.SaveChangesAsync(cancellationToken);
    }
}

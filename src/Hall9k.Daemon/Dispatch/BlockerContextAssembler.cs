using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Dispatch;

/// <summary>
/// Routes context along dependency edges when a task is claimed (Decisions Log #36): the
/// launched run starts with the handoffs of its IMMEDIATE BlockedBy blockers, condensed by a
/// synthesis session when the fan-in is wide enough to warrant it.
/// <para>
/// The edge does double duty by construction. BlockedBy was built for scheduling, but "this
/// could not start until that finished" is also a statement about relatedness, so the graph
/// the human already declared answers the context question with no second structure to
/// maintain. Depth one only: <see cref="BlockerHandoffQuery"/> reads the first hop and stops,
/// which is what keeps the graph self-correcting.
/// </para>
/// </summary>
public sealed class BlockerContextAssembler(
    IDocumentStore store,
    IExecutor executor,
    IProcessManager processManager,
    IOptions<DaemonOptions> options,
    ILogger<BlockerContextAssembler> logger)
{
    private readonly DaemonOptions _options = options.Value;

    /// <summary>
    /// The starting context for <paramref name="task"/>'s claimed run, or null when it has no
    /// blockers and there is therefore nothing to route. The document is also written to the
    /// run's directory, so what the agent was actually handed is inspectable afterwards
    /// rather than reconstructable only from the prompt.
    /// </summary>
    public async Task<string?> AssembleAsync(
        Guid runId,
        string runDirectory,
        TaskDetails task,
        ProjectDetails project,
        string worktreePath,
        ExecutorMode mode,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<BlockerHandoff> blockers;
        await using (IQuerySession query = store.QuerySession())
        {
            blockers = await BlockerHandoffQuery.LoadAsync(query, task.BlockedBy, cancellationToken);
        }

        if (BlockerContextDocument.Render(blockers) is not { } raw)
        {
            return null;
        }

        string context = blockers.Count > _options.BlockerSynthesisThreshold
            ? await SynthesizeOrFallBackAsync(
                runId, runDirectory, task, project, worktreePath, mode, blockers.Count, raw, cancellationToken)
            : raw;

        await WriteArtifactAsync(runDirectory, context, cancellationToken);
        logger.LogInformation(
            "Run {RunId}: starting context assembled from {Count} immediate blocker(s), depth one",
            runId, blockers.Count);
        return context;
    }

    /// <summary>
    /// The fan-in pass. Eight tasks converging on an integration task is good decomposition
    /// rather than a smell, but eight handoffs is a heavy way to start a session, so above the
    /// configured threshold a platform-dispatched session condenses them into one document
    /// first. It follows the review-session patterns: its model resolves through the chain and
    /// is recorded (log #33), its tokens are recorded on the dependent's run, and its
    /// artifacts live in that run's own directory.
    /// <para>
    /// A synthesis that dies, errors, or returns nothing usable — which includes a response
    /// that drops the document's own heading — falls back to the raw handoffs and says so on
    /// the event. Condensing is an optimization over a context that already exists, so it may
    /// never be the reason a dispatch fails.
    /// </para>
    /// </summary>
    private async Task<string> SynthesizeOrFallBackAsync(
        Guid runId,
        string runDirectory,
        TaskDetails task,
        ProjectDetails project,
        string worktreePath,
        ExecutorMode mode,
        int blockerCount,
        string raw,
        CancellationToken cancellationToken)
    {
        SpawnedAgent? unfinished = null;
        try
        {
            Guid sessionId = DomainId.New();
            string artifactName = $"context-synthesis-{sessionId.ToString("N")[..8]}";
            AgentModel model = _options.ResolveModel(AgentRole.Synthesis, task.Model, project.Model);
            SpawnedAgent agent = await executor.SpawnAsync(new AgentSpawnRequest(
                runId, sessionId, worktreePath, runDirectory,
                AgentPromptBuilder.BuildContextSynthesis(task, blockerCount, raw),
                mode, model, project.SkipPermissions, artifactName), cancellationToken);
            unfinished = agent;

            await using (IDocumentSession dispatched = store.LightweightSession())
            {
                dispatched.Events.Append(runId, new ContextSynthesisDispatched(
                    runId, sessionId, blockerCount, agent.ProcessId, agent.StartedAt, DateTimeOffset.UtcNow, model));
                await dispatched.SaveChangesAsync(cancellationToken);
            }

            logger.LogInformation(
                "Run {RunId}: {Count} blockers exceed the synthesis threshold of {Threshold} — condensing session {SessionId} dispatched (pid {ProcessId}, model {Model})",
                runId, blockerCount, _options.BlockerSynthesisThreshold, sessionId, agent.ProcessId, model.Value);

            AgentResult? result = await WaitWithinBudgetAsync(runId, runDirectory, artifactName, agent, cancellationToken);

            // The wait either read the session's own result or terminated it on expiry, so
            // from here on there is nothing of ours still running to abandon.
            unfinished = null;

            string? condensed = UsableDocument(result);
            await RecordCompletionAsync(runId, result, condensed.IsNotBlank(), cancellationToken);
            if (condensed.IsBlank())
            {
                logger.LogWarning(
                    "Run {RunId}: the context synthesis returned nothing usable — passing the {Count} raw handoffs through instead",
                    runId, blockerCount);
                return raw;
            }

            return condensed;
        }
        catch (Exception exception)
        {
            // Any exit that is not the wait's own leaves a session nobody is monitoring: the
            // daemon shutting down mid-wait, or a throw between the spawn and the wait. The
            // ceiling's invariant applies to all of them — a condenser this dispatch has
            // stopped caring about must not outlive it, because nothing else will ever adopt
            // it (adoption reattaches to pids recorded by RunProcessStarted, which a
            // synthesis session never reaches).
            Terminate(runId, unfinished);
            if (exception is OperationCanceledException)
            {
                throw;
            }

            logger.LogWarning(exception,
                "Run {RunId}: context synthesis failed — passing the raw handoffs through instead", runId);
            return raw;
        }
    }

    /// <summary>
    /// Kills an abandoned synthesis session, and never at the cost of the exception that is
    /// on its way out: a process manager that throws here would replace a daemon shutdown or
    /// the fallback path with a failed dispatch, which is exactly the outcome the fallback
    /// exists to prevent.
    /// </summary>
    private void Terminate(Guid runId, SpawnedAgent? agent)
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
            logger.LogWarning(exception,
                "Run {RunId}: could not terminate the abandoned context-synthesis session (pid {ProcessId})",
                runId, session.ProcessId);
        }
    }

    /// <summary>
    /// The condensed document when the session produced one the dependent can actually be
    /// handed, and null otherwise. Non-blank is not the bar: the text is pasted into the
    /// dependent's prompt verbatim, between the task's own context and the project links, so
    /// a response that does not open with <see cref="BlockerContextDocument.Heading"/> would
    /// land there as unlabelled prose continuing the objective rather than as what the
    /// blockers handed down — the structure lost silently, which is the one failure mode the
    /// raw handoffs cannot have. The heading the synthesis prompt asks for is therefore the
    /// contract, and falling back to those raw handoffs is cheap enough to enforce it.
    /// </summary>
    private static string? UsableDocument(AgentResult? result)
    {
        string? document = result is { IsError: false } ? result.Summary?.Trim() : null;
        return document?.StartsWith(BlockerContextDocument.Heading, StringComparison.Ordinal) is true
            ? document
            : null;
    }

    /// <summary>
    /// The waiter under a ceiling. This session is the one place the daemon blocks a dispatch
    /// on an agent, and LaunchAsync is awaited inside the dispatch loop, so an unbounded wait
    /// here would let one hung condenser stall every other claim on the node. On expiry the
    /// session is terminated rather than left running: it can no longer affect this dispatch,
    /// and an abandoned agent burning tokens for nobody is worse than no condensing at all.
    /// Cancellation of the daemon itself propagates as it should, and is not a timeout — the
    /// caller terminates the session on that path before letting it through.
    /// </summary>
    private async Task<AgentResult?> WaitWithinBudgetAsync(
        Guid runId, string runDirectory, string artifactName, SpawnedAgent agent, CancellationToken cancellationToken)
    {
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_options.BlockerSynthesisTimeout);
        try
        {
            return await SessionResultWaiter.WaitAsync(
                RunPaths.SessionStreamFile(runDirectory, artifactName), agent.ProcessId, agent.StartedAt,
                processManager, onOutput: null, budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Run {RunId}: the context synthesis exceeded {Timeout} — terminating it and starting on the raw handoffs",
                runId, _options.BlockerSynthesisTimeout);
            Terminate(runId, agent);
            return null;
        }
    }

    private async Task RecordCompletionAsync(
        Guid runId, AgentResult? result, bool synthesized, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();
        if (result is not null)
        {
            session.Events.Append(runId, result.ToTokensRecorded(runId, now));
        }

        session.Events.Append(runId, new ContextSynthesisCompleted(runId, synthesized, now));
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The context as the agent received it, beside the run's other artifacts. Losing the
    /// copy must not cost the dispatch the context itself, so a write failure is logged and
    /// the prompt goes out regardless.
    /// </summary>
    private async Task WriteArtifactAsync(string runDirectory, string context, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(runDirectory);
            await File.WriteAllTextAsync(RunPaths.BlockerContextFile(runDirectory), context, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not write the blocker-context artifact for {RunDirectory}", runDirectory);
        }
    }
}

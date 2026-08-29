using Hall9k.Domain.Infrastructure.Storage;
using System.Diagnostics;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using JasperFx.Events;
using Marten;
using Marten.Events;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// Opens the closeout phase (Decisions Log #18): push the task branch, open the PR as
/// the owner (PLAN.md §6.6 — no bot attribution anywhere), complete the task, release
/// the lease. The run stays AwaitingReview for the closeout monitor, which appends
/// RunCompleted when it observes the merge. The worktree is deliberately retained — it
/// IS the follow-up workspace for review resolution and CI fixes (log #21; origin
/// incident: worktrees deleted at PR-open had to be recreated by hand for PRs 4 and 5).
/// Removal happens at closeout completion, in the CloseoutEngine. Non-GitHub origins
/// still get the branch pushed and the task completed, just without a PR.
/// </summary>
public sealed class PullRequestOpener(
    IDocumentStore store,
    ILogger<PullRequestOpener> logger)
{
    public async Task OpenAsync(Guid runId, Guid taskId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = store.QuerySession();
        RunDetails? run = await query.LoadAsync<RunDetails>(runId, cancellationToken);
        TaskDetails? task = run is null ? null : await query.LoadAsync<TaskDetails>(taskId, cancellationToken);
        var project = task is null
            ? null
            : await query.LoadAsync<Domain.Features.Project.Projections.ProjectDetails>(task.ProjectId, cancellationToken);

        if (run is null || task is null || project is null)
        {
            logger.LogError("Cannot open PR for run {RunId}: run, task, or project missing", runId);
            return;
        }

        try
        {
            // Follow-up-ness is recorded on the run at dispatch (RunDispatched.IsFollowUp) —
            // an observed fact, never re-inferred from the task's PR URL, which a stranded
            // first run adopted after another run completed the task would get wrong.
            bool followUp = run.IsFollowUp;

            // Always --force-with-lease, never a plain push and never plain --force. A
            // follow-up may have rewritten the branch's history (narrative commit style:
            // fixups folded into their owning commits, Decisions Log #26), and a fresh build
            // session's own end-of-work checkpoint recompose (task: build sessions stop
            // stranding finished work uncommitted) rewrites it too — a mixed reset to the
            // branch's fork point followed by commit-plan composing new history means a
            // retried task resuming a branch this same opener already pushed once (push
            // succeeded, `gh pr create` then failed) diverges from that earlier push.
            // --force-with-lease lands either rewrite while still refusing a tip this node
            // has not fetched (a human or another node moved the branch). A first-time push
            // is unaffected: with no remote-tracking ref to protect, the lease is satisfied
            // trivially (verified: `git push --force-with-lease` creates a brand-new remote
            // branch exactly like a plain push does) — which matters because `h9k task
            // deliver` now publishes an interactive claim's branch itself before this run
            // reaches here, so `IsFollowUp` is no longer a reliable proxy for "does a remote
            // copy already exist" (conformance review, cycle 1). A failed lease fails the run
            // honestly below — no blind retry — and h9k pr resolve is the requeue lever.
            // Origin incident (2026-08-17): the first two automatic follow-up runs rebased
            // per the authored-history rule and a conditional plain push rejected both,
            // stranding completed, gated work in the worktrees.
            string[] pushArguments = ["push", "--force-with-lease", "origin", run.Branch];
            await RunInWorktreeAsync(run.WorktreePath, "git", pushArguments, cancellationToken);
            (string? pullRequestUrl, int pullRequestNumber) = task.PullRequestUrl is { } existingUrl
                ? (existingUrl, PullRequestUrls.ParseNumber(existingUrl))
                : await IsGitHubOriginAsync(run.WorktreePath, cancellationToken)
                    ? await CreatePullRequestAsync(run, task, project.BaseBranch, cancellationToken)
                    : (null, 0);

            DateTimeOffset now = DateTimeOffset.UtcNow;
            await using IDocumentSession session = store.LightweightSession();
            if (pullRequestUrl is not null)
            {
                session.Events.Append(runId, followUp
                    ? new PullRequestUpdated(runId, pullRequestUrl, pullRequestNumber, now)
                    : new PullRequestOpened(runId, pullRequestUrl, pullRequestNumber, now));
            }

            // LoadFencedAsync's read must happen before the AllowsAsync identity check below —
            // not after — so a reclaim landing between the two is caught by AllowsAsync's
            // fresh read rather than baked into `current.Task` as an already-stale ownership
            // fact that AllowsAsync never gets asked about (adversarial review, cycle 2).
            (TaskAggregate Task, long Version)? fenced = await GenerationFence.LoadFencedAsync(session, taskId, cancellationToken);
            if (!await GenerationFence.AllowsAsync(
                session, logger, taskId, runId, run.LeaseGeneration, nameof(TaskCompleted), cancellationToken))
            {
                // A stale completion: on a GitHub origin the PullRequestOpened/Updated event
                // above is enough for the closeout monitor's own PR-state read to notice this
                // run is done and stop watching it, but a non-GitHub origin appended nothing
                // — without retiring the run here it stays in its pre-PR live RunState, and
                // the supervisor dropping its monitor just means adoption or resume finds it
                // "recoverable" and repeats this same stale path (Copilot review, PR #30).
                // The live generation's lease is untouched: it is not this stale run's to take.
                if (await session.Events.FetchStreamStateAsync(runId, cancellationToken) is not null)
                {
                    TaskDetails? currentTask = await session.LoadAsync<TaskDetails>(taskId, cancellationToken);
                    session.Events.Append(
                        runId, new RunSuperseded(runId, currentTask?.LeaseGeneration ?? run.LeaseGeneration, now));
                }

                await session.SaveChangesAsync(cancellationToken);
                return;
            }

            if (fenced is { } current && current.Task.State == TaskState.Claimed)
            {
                session.Events.Append(
                    taskId, expectedVersion: current.Version + 1, TaskDecider.Complete(current.Task, runId, pullRequestUrl, now));
            }

            session.Delete<TaskLease>(taskId);
            try
            {
                await session.SaveChangesAsync(cancellationToken);
            }
            catch (EventStreamUnexpectedMaxEventIdException)
            {
                logger.LogInformation(
                    "Task {TaskId}: lost the generation race completing the task for run {RunId} — a newer claim committed first",
                    taskId, runId);
                return;
            }

            // DaemonLogEvents.PullRequestOpened is the structural hook (Decisions Log #89, origin:
            // PR #50 sat Delivered for 23 minutes with no signal): an operator's monitor can wake
            // on this EventId plus the RunId/TaskId/Url fields without matching the prose below,
            // which is free to reword. The no-PR arm carries no Url, so it logs under its own
            // BranchPushedWithNoPullRequest id rather than the one a monitor keys on to mean "a
            // pull request opened" (DaemonLogEvents.BranchPushedWithNoPullRequest's own doc).
            if (pullRequestUrl is not null)
            {
                logger.LogInformation(
                    DaemonLogEvents.PullRequestOpened,
                    followUp
                        ? "Run {RunId} task {TaskId}: follow-up pushed to existing PR {Url} — task complete, awaiting review"
                        : "Run {RunId} task {TaskId}: PR opened at {Url} — task complete, awaiting review",
                    runId, taskId, pullRequestUrl);
            }
            else
            {
                logger.LogInformation(
                    DaemonLogEvents.BranchPushedWithNoPullRequest,
                    "Run {RunId} task {TaskId}: branch pushed (origin is not GitHub; no PR) — task complete",
                    runId, taskId);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "PR opening failed for run {RunId}", runId);
            await RecordFailureAsync(runId, taskId, exception.Message, cancellationToken);
        }
    }

    /// <summary>
    /// Where this task's work item lives, asked of the registered connections rather than of a
    /// default importer: placing a Jira reference needs the site that connection recorded, so a
    /// card would otherwise fall back to its bare key in every pull request body. Null when no
    /// registered source recognises the reference, which the body then reports honestly.
    /// <para>
    /// A failure here costs the link and not the pull request. The reference is on the task
    /// either way, and refusing to open a PR because a connection could not be read would let a
    /// Jira misconfiguration block work that has nothing to do with Jira.
    /// </para>
    /// </summary>
    private async Task<Uri?> SourceUrlAsync(TaskDetails task, CancellationToken cancellationToken)
    {
        if (task.ExternalReference.IsBlank())
        {
            return null;
        }

        try
        {
            await using IQuerySession query = store.QuerySession();
            WorkItemImporter importer = await WorkItemConnections.ImporterAsync(query, cancellationToken);
            return importer.WebUrl(task.ExternalReference);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception,
                "Could not resolve a link for {Reference}; the pull request will name it by reference",
                task.ExternalReference);
            return null;
        }
    }

    private async Task<(string Url, int Number)> CreatePullRequestAsync(
        RunDetails run, TaskDetails task, string baseBranch, CancellationToken cancellationToken)
    {
        string bodyFile = Path.Combine(RunPaths.ResolveCurrentDirectory(run.RunDirectory), "pr-body.md");
        await File.WriteAllTextAsync(
            bodyFile,
            PullRequestBody.Build(run, task, TryReadAgentSummary(run), await SourceUrlAsync(task, cancellationToken)),
            cancellationToken);

        // The title goes through PullRequestBody too, not straight from the projection: on a
        // squash merge GitHub's default commit message is the pull request's title, so a title is
        // a closing instruction with a longer fuse than anything in the body.
        string output = await RunInWorktreeAsync(run.WorktreePath, "gh",
            [
                "pr", "create",
                "--title", PullRequestBody.Title(task.Objective),
                "--body-file", bodyFile,
                "--base", baseBranch,
                "--head", run.Branch,
            ],
            cancellationToken);

        string url = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith("https://", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"gh pr create returned no URL. Output: {output}");

        return (url, PullRequestUrls.ParseNumber(url));
    }

    private string? TryReadAgentSummary(RunDetails run)
    {
        try
        {
            string streamFile = RunPaths.StreamFile(RunPaths.ResolveCurrentDirectory(run.RunDirectory));
            if (!File.Exists(streamFile))
            {
                return null;
            }

            foreach (string line in File.ReadLines(streamFile))
            {
                if (StreamJsonParser.TryParseResult(line, out AgentResult result) && result.Summary.IsNotBlank())
                {
                    return result.Summary.Length <= 4000 ? result.Summary : result.Summary[..4000];
                }
            }
        }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "Could not read agent summary for run {RunId}", run.Id);
        }

        return null;
    }

    private static async Task<bool> IsGitHubOriginAsync(string worktreePath, CancellationToken cancellationToken)
    {
        try
        {
            string url = await RunInWorktreeAsync(worktreePath, "git", ["remote", "get-url", "origin"], cancellationToken);
            return url.Contains("github.com", StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<string> RunInWorktreeAsync(
        string worktreePath, string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = worktreePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return process.ExitCode == 0
            ? (await standardOutput).Trim() + (await standardError).Trim()
            : throw new InvalidOperationException(
                $"{fileName} {string.Join(' ', arguments)} exited {process.ExitCode}: {(await standardError).Trim()}");
    }

    private async Task RecordFailureAsync(Guid runId, Guid taskId, string reason, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new RunFailed(runId, $"PR opening failed: {reason}", now));

        // LoadFencedAsync's read must happen before the AllowsAsync identity check below —
        // not after — so a reclaim landing between the two is caught by AllowsAsync's fresh
        // read rather than baked into `current.Task` as an already-stale ownership fact
        // that AllowsAsync never gets asked about (adversarial review, cycle 2).
        (TaskAggregate Task, long Version)? fenced = await GenerationFence.LoadFencedAsync(session, taskId, cancellationToken);
        RunDetails? run = await session.LoadAsync<RunDetails>(runId, cancellationToken);
        if (fenced is { } current
            && TaskDecider.CanFail(current.Task)
            && (run is null || await GenerationFence.AllowsAsync(
                session, logger, taskId, runId, run.LeaseGeneration, nameof(TaskFailed), cancellationToken)))
        {
            // One transaction with the RunFailed append above (Copilot review, PR #30's
            // expectedVersion fix, kept atomic with it on purpose — see
            // RunSupervisor.AppendFencedTaskFailureAsync): a lost race here rolling back
            // the run's own failure fact too is a smaller cost than a reader observing the
            // run Failed while its task still reads Claimed.
            session.Events.Append(
                taskId, expectedVersion: current.Version + 1,
                TaskDecider.Fail(current.Task, runId, $"PR opening failed: {reason}", now));
            session.Delete<TaskLease>(taskId);
        }

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            logger.LogInformation(
                "Task {TaskId}: lost the generation race recording a PR-opening failure for run {RunId} — a newer claim committed first",
                taskId, runId);
        }
    }
}

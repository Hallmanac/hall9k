using Hall9k.Domain.Infrastructure.Storage;
using System.Diagnostics;
using Hall9k.Connectors.Processes;
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
            // session's own end-of-work checkpoint recompose (Decisions Log #104) rewrites it
            // too — a mixed reset to the branch's fork point followed by commit-plan composing
            // new history means a retried task resuming a branch this same opener already
            // pushed once (push succeeded, `gh pr create` then failed) diverges from that
            // earlier push. A plain push rejects that as non-fast-forward and strands the
            // completed, gated work. The bare flag alone is not the fix: it would still trust
            // whatever this node's remote-tracking ref currently reads, and BestEffortFetchAsync
            // refreshes that ref on every worktree create/checkout regardless of whether the sync
            // that follows actually integrates it (GitWorktreeManager.SyncToOriginBestEffortAsync's
            // uncommitted-work veto, in particular), so a bare lease can pass — and silently
            // overwrite — a tip this run never actually accounted for (conformance review,
            // cycle 1). This matters for a first-time push too, not only a follow-up's: `h9k
            // task deliver` now publishes an interactive claim's branch itself before this run
            // reaches here, so `IsFollowUp` is no longer a reliable proxy for "does a remote
            // copy already exist" (conformance review, cycle 1). PushBranchAsync below guards
            // that with an explicit ancestor-or-reflog check before ever pushing, and only then
            // pins the lease's expected value to the tip the guard just verified — closing the
            // narrower race between that read (a direct ls-remote against origin, not a local
            // ref) and the push itself, where another node could still push to origin in
            // between. Never plain --force. A refused or failed push fails the run honestly
            // below, and h9k task retry is the requeue lever — the task lands Failed, where
            // retry (not pr resolve, which only applies to a Done task's open PR) applies. Origin
            // incident (2026-08-17): the first two automatic follow-up runs rebased per the
            // authored-history rule and a then-plain push rejected both, stranding completed,
            // gated work in the worktrees.
            await PushBranchAsync(run.WorktreePath, run.Branch, cancellationToken);
            (string? pullRequestUrl, int pullRequestNumber) = task.PullRequestUrl is { } existingUrl
                ? (existingUrl, PullRequestUrls.ParseNumber(existingUrl))
                : await IsGitHubOriginAsync(run.WorktreePath, cancellationToken)
                    // The base this run recorded at dispatch, not the project's own: a stacked
                    // child's pull request targets its parent's branch, which is what forms the
                    // stack on GitHub (task: a stacked pull-request edge exists as an explicit
                    // opt-in dependency). ResolveOpenBaseAsync reads that recorded base and, for a
                    // stacked child only, checks the branch is still on origin at all before aiming
                    // a pull request at it. Every unstacked pull request opens exactly as it always
                    // has, without a single extra call.
                    ? await CreatePullRequestAsync(
                        run, task, await ResolveOpenBaseAsync(run, project, cancellationToken), cancellationToken)
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

    /// <summary>
    /// The branch this run's pull request actually opens against: the base it recorded at dispatch,
    /// unless that base is a parent branch origin no longer has.
    /// <para>
    /// This is the stacked edge's mainline race, not an edge case (adversarial review, cycle 6): the
    /// whole point of the edge is that the child dispatches at the parent's <em>Delivered</em>, so
    /// the parent's merge — a human's, or the daemon's own pre-approved auto-merge — routinely lands
    /// during the child's hours-long build and review pipeline, and the parent's closeout deletes
    /// its branch everywhere unconditionally. <c>gh pr create --base &lt;a deleted branch&gt;</c> is a
    /// raw 422 that fails the run and the task with an error naming none of this; the retry then
    /// resumes the branch and inherits the same frozen base (<c>StackedBaseResolver.ResumedBaseAsync</c>,
    /// correctly — a resumed branch's base is a fact about the branch), so it fails identically,
    /// forever. Every retarget and replay mechanism the feature has is reachable only from
    /// closeout's inspection of an ALREADY-OPEN pull request, so nothing recovered it.
    /// </para>
    /// <para>
    /// Opening against the project's own base is what recovers it, and it is exactly where the
    /// retarget would have put this pull request anyway. What deliberately does NOT happen here is
    /// touching the run's recorded base: the branch still physically carries the parent's commits,
    /// so the replay that drops them is still owed, and the record is what
    /// <c>StackedParentWatch.IsStackedChild</c> and the merge-bar guard read to know that. Left
    /// alone, the next closeout sweep observes the merged parent, records the retarget over a
    /// pull request already on the right base (<c>gh pr edit --base</c> is idempotent), and
    /// dispatches the replay — the ordinary path, arriving at the ordinary place.
    /// </para>
    /// <para>
    /// A branch origin could not be READ about is not a branch that is gone (AGENTS.md's never-guess
    /// rule): the recorded base stands, <c>gh</c> fails the run honestly, and <c>h9k task retry</c>
    /// asks again — which is the same lever a failed push already names.
    /// </para>
    /// </summary>
    private async Task<string> ResolveOpenBaseAsync(
        RunDetails run, Domain.Features.Project.Projections.ProjectDetails project, CancellationToken cancellationToken)
    {
        string recorded = run.BaseBranchOr(project.BaseBranch);
        if (!run.AwaitsStackedRetarget(project.BaseBranch))
        {
            return recorded;
        }

        // Through the deadline-bounded runner, not the raw process helper further down: this call
        // reaches origin — through whatever credential helper the node has configured, possibly over
        // a slow uplink — and that helper carries no deadline at all, so a wedged remote would hang
        // the opener with nothing to time it out. ExternalProcess's DEFAULT deadline rather than
        // PushBranchAsync's own longer one, because a single-ref ls-remote is exactly "the short
        // metadata read" that default is sized for (ExternalProcess.Deadline's own doc), unlike a
        // push that transfers real data.
        ProcessResult probe;
        try
        {
            probe = await ExternalProcess.Runner(
                "git",
                ["ls-remote", "--exit-code", "origin", $"refs/heads/{recorded}"],
                run.WorktreePath,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A deadline, a held-open output pipe, a refused spawn: origin was not read, which is
            // not the same fact as the parent's branch being gone. Same direction as a nonzero exit
            // below — the recorded base stands.
            logger.LogWarning(
                exception,
                "Run {RunId}: could not read whether the stacked parent branch {ParentBranch} is still on origin "
                + "— opening against it as recorded rather than assuming it is gone",
                run.Id, recorded);
            return recorded;
        }

        string open = OpenBaseFor(recorded, project.BaseBranch, probe.ExitCode);
        if (open == recorded)
        {
            if (probe.ExitCode != 0)
            {
                logger.LogWarning(
                    "Run {RunId}: could not read whether the stacked parent branch {ParentBranch} is still on "
                    + "origin ({Error}) — opening against it as recorded rather than assuming it is gone; if it "
                    + "has been deleted, gh fails this run and h9k task retry asks again",
                    run.Id, recorded, probe.StandardError.IsBlank() ? "no output" : probe.StandardError);
            }

            return recorded;
        }

        LogStackedParentBranchGone(logger, run.Id, recorded, project.BaseBranch);
        return open;
    }

    /// <summary>
    /// The warning that accompanies the fallback, in a named method purely so a test can render it
    /// through a real logger without a GitHub origin to reach.
    /// <para>
    /// Its template repeats <c>{ParentBranch}</c> and <c>{BaseBranch}</c>, and Microsoft.Extensions.Logging
    /// binds placeholders POSITIONALLY per occurrence — <c>LogValuesFormatter</c> rewrites the template to
    /// <c>{0}</c>…<c>{4}</c> and never deduplicates repeated names — so a repeated placeholder needs its
    /// argument repeated too (the same shape <c>DispatchEngine</c> and <c>DispatchLoop</c>'s own repeated
    /// templates already use). Passing three arguments for five slots threw a <c>FormatException</c> out of
    /// the <c>LogWarning</c> call itself, which aborted <see cref="ResolveOpenBaseAsync"/> before it could
    /// return the fallback base: the run failed on a logging error, and every <c>h9k task retry</c> resumed
    /// the branch, probed origin, and failed identically — reinstating the permanent-failure loop the
    /// fallback exists to end (independent pre-PR review, cycle 8, both lenses).
    /// </para>
    /// </summary>
    internal static void LogStackedParentBranchGone(
        ILogger logger, Guid runId, string parentBranch, string projectBaseBranch) =>
        logger.LogWarning(
            DaemonLogEvents.StackedParentBranchGoneAtPullRequestOpen,
            "Run {RunId}: the stacked parent branch {ParentBranch} is gone from origin — its pull request merged "
            + "while this branch was still building — so this pull request opens against {BaseBranch} instead. "
            + "The run still records {ParentBranch} as its base, because this branch still carries the parent's "
            + "commits and closeout's replay onto {BaseBranch} is still owed",
            runId, parentBranch, projectBaseBranch, parentBranch, projectBaseBranch);

    /// <summary>
    /// The decision <see cref="ResolveOpenBaseAsync"/> makes once it has asked origin, split out
    /// from the asking so the rule can be exercised without a remote: only <c>ls-remote</c>'s
    /// documented "no matching ref" code (2) moves a stacked child's pull request off the base its
    /// run recorded. 0 is the branch still being there; every other code is git failing to answer,
    /// which is not the same fact and must not move the base — the whole hazard here is guessing a
    /// branch away and opening a stacked pull request against the wrong base while the parent is
    /// still very much alive.
    /// </summary>
    internal static string OpenBaseFor(string recordedBase, string projectBaseBranch, int lsRemoteExitCode) =>
        lsRemoteExitCode == 2 ? projectBaseBranch : recordedBase;

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

    /// <summary>
    /// How long a branch push gets before Hall9k stops waiting for it. Deliberately longer than
    /// <see cref="ExternalProcess.Deadline"/> (120 seconds, sized for "the short metadata read" —
    /// its own doc comment), the same way <c>UpdateCommand</c>'s release-archive download uses
    /// <see cref="ExternalProcess.RunnerWithDeadline"/> rather than the plain runner: a branch
    /// push transfers real data, so a first push carrying large blobs over a slow uplink is not
    /// the call that default was sized for (independent pre-PR review, cycle 1, adversarial lens
    /// — the prior, unrouted push helper this replaced had no timeout at all).
    /// </summary>
    private static readonly TimeSpan PushDeadline = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Pushes the branch with the lease pinned to a value this node knows is safe to overwrite,
    /// rather than trusting the bare <c>--force-with-lease</c> flag against whatever this node's
    /// remote-tracking ref currently reads (see the caller's comment for why that ref alone is
    /// not proof the local branch actually accounts for it). The ancestor-or-reflog guard itself
    /// lives in <see cref="ForceWithLeasePusher"/>, shared with the closeout engine's mechanical
    /// rebase fast path — only the recovery lever named in a refusal differs per caller.
    /// </summary>
    private static async Task PushBranchAsync(string worktreePath, string branch, CancellationToken cancellationToken)
    {
        ProcessRunner git = ExternalProcess.RunnerWithDeadline(PushDeadline);
        try
        {
            await ForceWithLeasePusher.PushAsync(git, worktreePath, branch, cancellationToken);
        }
        catch (ProcessOutputStuckException exception) when (exception.ExitCode == 0)
        {
            // git itself exited 0 here — the read or the push it was running genuinely completed
            // — but a spawned credential helper held the output pipe open past
            // ExternalProcess.DrainGrace (ProcessOutputStuckException's own doc comment: exit code
            // 0 can tell a genuine success from a genuine failure). Exit 0 alone does not say
            // *which* of ForceWithLeasePusher's own git calls (the read-only
            // ls-remote/merge-base/reflog, or the push itself) is the one that got stuck, so
            // origin's actual tip for the branch is read back rather than assumed — the same
            // distinction the closeout engine's mechanical rebase fast path draws for the
            // identical reason (independent pre-PR review, cycle 1, both lenses; swept here as the
            // same shape).
            ProcessResult localHead = await git("git", ["rev-parse", "HEAD"], worktreePath, cancellationToken);
            ProcessResult originTip = await git(
                "git", ["ls-remote", "--exit-code", "origin", $"refs/heads/{branch}"], worktreePath, cancellationToken);
            string? originHead = originTip.ExitCode == 0
                ? originTip.StandardOutput.Split('\t', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
                : null;

            if (localHead.ExitCode == 0 && originHead is not null && originHead == localHead.StandardOutput.Trim())
            {
                return;
            }

            throw new InvalidOperationException(
                $"{exception.Message} This fails the run, so h9k task retry is the way to requeue "
                + "once the branch or the remote are sorted out.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
            // ForceWithLeasePusher's own message is shared with the closeout engine's mechanical
            // rebase path and says nothing about which lever recovers a failed push — this run's
            // own recovery lever, named here rather than there. TimeoutException is caught
            // alongside the refusal, not just the refusal: PushDeadline above still expires, and
            // without this the timeout escaped straight to the outer catch with no lever named
            // (independent pre-PR review, cycle 1, adversarial lens).
            throw new InvalidOperationException(
                $"{exception.Message} This fails the run, so h9k task retry is the way to requeue "
                + "once the branch or the remote are sorted out.");
        }
    }

    private static async Task<string> RunInWorktreeAsync(
        string worktreePath, string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        (int exitCode, string standardOutput, string standardError) =
            await TryRunInWorktreeAsync(worktreePath, fileName, arguments, cancellationToken);
        return exitCode == 0
            ? standardOutput + standardError
            : throw new InvalidOperationException(
                $"{fileName} {string.Join(' ', arguments)} exited {exitCode}: {standardError}");
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> TryRunInWorktreeAsync(
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

        return (process.ExitCode, (await standardOutput).Trim(), (await standardError).Trim());
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

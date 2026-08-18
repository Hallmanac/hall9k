using Hall9k.Domain.Infrastructure.Storage;
using System.Diagnostics;
using System.Text;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Marten;

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

            // A follow-up may have rewritten the branch's history (narrative commit style:
            // fixups folded into their owning commits, Decisions Log #26), so its push
            // must be --force-with-lease: it lands the rebase yet still refuses a tip this
            // node has not fetched (a human or another node moved the branch). Never plain
            // --force. A failed lease fails the run honestly below — no blind retry — and
            // h9k pr resolve is the requeue lever. Origin incident (2026-08-17): the first
            // two automatic follow-up runs rebased per the authored-history rule and this
            // plain push rejected both, stranding completed, gated work in the worktrees.
            string[] pushArguments = followUp
                ? ["push", "--force-with-lease", "origin", run.Branch]
                : ["push", "origin", run.Branch];
            await RunInWorktreeAsync(run.WorktreePath, "git", pushArguments, cancellationToken);
            (string? pullRequestUrl, int pullRequestNumber) = task.PullRequestUrl is { } existingUrl
                ? (existingUrl, ParsePullRequestNumber(existingUrl))
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

            TaskAggregate? aggregate = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken);
            if (aggregate is not null && aggregate.State == TaskState.Claimed)
            {
                session.Events.Append(taskId, TaskDecider.Complete(aggregate, runId, pullRequestUrl, now));
            }

            session.Delete<TaskLease>(taskId);
            await session.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                (followUp, pullRequestUrl) switch
                {
                    (true, _) => "Run {RunId}: follow-up pushed to existing PR {Url} — task complete, awaiting review",
                    (false, not null) => "Run {RunId}: PR opened at {Url} — task complete, awaiting review",
                    _ => "Run {RunId}: branch pushed (origin is not GitHub; no PR) — task complete",
                },
                runId, pullRequestUrl);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "PR opening failed for run {RunId}", runId);
            await RecordFailureAsync(runId, taskId, exception.Message, cancellationToken);
        }
    }

    private async Task<(string Url, int Number)> CreatePullRequestAsync(
        RunDetails run, TaskDetails task, string baseBranch, CancellationToken cancellationToken)
    {
        string bodyFile = Path.Combine(RunPaths.RunDirectory(run.Id), "pr-body.md");
        await File.WriteAllTextAsync(bodyFile, BuildBody(run, task), cancellationToken);

        string output = await RunInWorktreeAsync(run.WorktreePath, "gh",
            ["pr", "create", "--title", task.Objective, "--body-file", bodyFile, "--base", baseBranch, "--head", run.Branch],
            cancellationToken);

        string url = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith("https://", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"gh pr create returned no URL. Output: {output}");

        return (url, ParsePullRequestNumber(url));
    }

    private static int ParsePullRequestNumber(string url) =>
        int.TryParse(url[(url.LastIndexOf('/') + 1)..], out int parsed) ? parsed : 0;

    private string BuildBody(RunDetails run, TaskDetails task)
    {
        StringBuilder body = new();
        body.AppendLine(task.Objective);
        body.AppendLine();
        body.AppendLine("## Acceptance criteria");
        foreach (string criterion in task.AcceptanceCriteria)
        {
            body.AppendLine($"- [ ] {criterion}");
        }

        string? summary = TryReadAgentSummary(run.Id);
        if (summary.IsNotBlank())
        {
            body.AppendLine();
            body.AppendLine("## Agent summary");
            body.AppendLine(summary);
        }

        body.AppendLine();
        body.AppendLine($"---");
        body.AppendLine($"Hall9k run `{run.Id}` · {run.InputTokens + run.OutputTokens} tokens");
        return body.ToString();
    }

    private string? TryReadAgentSummary(Guid runId)
    {
        try
        {
            string streamFile = RunPaths.StreamFile(runId);
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
            logger.LogWarning(exception, "Could not read agent summary for run {RunId}", runId);
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
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new RunFailed(runId, $"PR opening failed: {reason}", DateTimeOffset.UtcNow));

        TaskAggregate? task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken);
        if (task is not null && TaskDecider.CanFail(task))
        {
            session.Events.Append(taskId, TaskDecider.Fail(task, runId, $"PR opening failed: {reason}", DateTimeOffset.UtcNow));
        }

        session.Delete<TaskLease>(taskId);
        await session.SaveChangesAsync(cancellationToken);
    }
}

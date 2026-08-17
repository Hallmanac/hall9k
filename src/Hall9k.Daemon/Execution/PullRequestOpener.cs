using Hall9k.Domain.Infrastructure.Storage;
using System.Diagnostics;
using System.Text;
using Hall9k.Daemon.Worktrees;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Marten;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// The last link of the Slice-1 pipeline: push the task branch, open the PR as the owner
/// (PLAN.md §6.6 — no bot attribution anywhere), complete the task, release the lease,
/// remove the worktree (the branch is safe on the remote). The run stays AwaitingReview —
/// merge detection is post-v0, and RunCompleted waits for it. Non-GitHub origins still get
/// the branch pushed and the task completed, just without a PR.
/// </summary>
public sealed class PullRequestOpener(
    IDocumentStore store,
    IWorktreeManager worktrees,
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
            await RunInWorktreeAsync(run.WorktreePath, "git", ["push", "origin", run.Branch], cancellationToken);

            (string? pullRequestUrl, int pullRequestNumber) = await IsGitHubOriginAsync(run.WorktreePath, cancellationToken)
                ? await CreatePullRequestAsync(run, task, project.BaseBranch, cancellationToken)
                : (null, 0);

            DateTimeOffset now = DateTimeOffset.UtcNow;
            await using IDocumentSession session = store.LightweightSession();
            if (pullRequestUrl is not null)
            {
                session.Events.Append(runId, new PullRequestOpened(runId, pullRequestUrl, pullRequestNumber, now));
            }

            TaskAggregate? aggregate = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken);
            if (aggregate is not null && aggregate.State == TaskState.Claimed)
            {
                session.Events.Append(taskId, TaskDecider.Complete(aggregate, runId, pullRequestUrl, now));
            }

            session.Delete<TaskLease>(taskId);
            await session.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                pullRequestUrl is not null
                    ? "Run {RunId}: PR opened at {Url} — task complete, awaiting review"
                    : "Run {RunId}: branch pushed (origin is not GitHub; no PR) — task complete",
                runId, pullRequestUrl);

            await RemoveWorktreeBestEffortAsync(project.RepositoryPath, run.WorktreePath, cancellationToken);
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

        int number = int.TryParse(url[(url.LastIndexOf('/') + 1)..], out int parsed) ? parsed : 0;
        return (url, number);
    }

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

    private async Task RemoveWorktreeBestEffortAsync(
        string repositoryPath, string worktreePath, CancellationToken cancellationToken)
    {
        try
        {
            await worktrees.RemoveAsync(repositoryPath, worktreePath, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Worktree removal failed for {Path} (branch is pushed; safe to prune later)", worktreePath);
        }
    }

    private async Task RecordFailureAsync(Guid runId, Guid taskId, string reason, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new RunFailed(runId, $"PR opening failed: {reason}", DateTimeOffset.UtcNow));

        TaskAggregate? task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken);
        if (task is not null && !task.State.IsTerminal)
        {
            session.Events.Append(taskId, TaskDecider.Fail(task, runId, $"PR opening failed: {reason}", DateTimeOffset.UtcNow));
        }

        session.Delete<TaskLease>(taskId);
        await session.SaveChangesAsync(cancellationToken);
    }
}

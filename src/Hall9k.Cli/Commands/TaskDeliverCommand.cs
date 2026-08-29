using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Deliver an interactive claim: refuse on uncommitted files (naming them — the operator is
/// present to commit), push the branch, then hand into the standard delivery pipeline. Delivery
/// means handed back (RULED): appending AgentSessionCompleted is the same event a headless run's
/// own agent session completing appends, moving this run to Verifying exactly as that run would
/// be — the daemon's RunSupervisor.ResumeStrandedPipelinesAsync notices a Verifying run with no
/// monitor on its very next sweep and runs the real gates, the independent review loop, and
/// PullRequestOpener, all through the identical code a headless run's own pipeline uses. From
/// here on the run is indistinguishable from a headless one: interactive participation in review
/// rounds is a later enhancement, and the review loop's own parks already provide the human hook
/// if something needs attention.
/// </summary>
public sealed class TaskDeliverCommand : Hall9kAsyncCommand<TaskDeliverCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--handoff <TEXT>")]
        [Description("What this run hands down to a dependent task or a resuming session (Decisions Log #36). Omit to be prompted on an interactive terminal, or to leave it unauthored on a non-interactive one.")]
        public string? Handoff { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        TaskDetails task = await session.LoadAsync<TaskDetails>(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        if (task.State != TaskState.Claimed || !task.IsInteractiveClaim || task.CurrentRunId is not { } runId)
        {
            throw new DomainConflictException(
                $"Task {taskId} is {task.State.Value} — only a task with an active interactive claim delivers this way.");
        }

        RunDetails run = await session.LoadAsync<RunDetails>(runId, cancellationToken)
            ?? throw new DomainConflictException($"Task {taskId} is claimed interactively but run {runId} has no record.");
        ProjectDetails project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId}'s project no longer exists.");

        if (run.State != RunState.Dispatched && run.State != RunState.Running)
        {
            throw new DomainConflictException(
                $"Run {runId} is already {run.State.Value} — task {taskId} was delivered already. h9k task show {taskId} to see where it stands.");
        }

        (IReadOnlyList<string>? modified, IReadOnlyList<string> untracked) =
            await InteractiveWorktreeGit.ListUncommittedFilesAsync(run.WorktreePath, cancellationToken);
        if (modified is null)
        {
            // Never guessed at as clean (InteractiveWorktreeGit's own contract): git could not
            // be asked, so the operator is told the check was skipped rather than delivery
            // silently proceeding over a tree nobody actually looked at.
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Could not read the worktree's git status at {run.WorktreePath}; skipping the uncommitted-files check.[/]");
        }
        else if (modified.Count > 0)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[red]Task {taskId}'s worktree has uncommitted file(s); commit or discard them first:[/]");
            foreach (string file in modified)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]  {file}[/]");
            }

            return ExitCodes.Conflict;
        }

        if (untracked.Count > 0)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Untracked file(s) in the worktree (not blocking delivery): {string.Join(", ", untracked)}[/]");
        }

        int commits = await InteractiveWorktreeGit.CountBranchCommitsAsync(run.WorktreePath, project.BaseBranch, cancellationToken);
        if (commits == 0)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[red]Branch '{run.Branch}' holds no commits beyond its base branch — nothing to deliver.[/]");
            return ExitCodes.Conflict;
        }

        (bool pushed, string pushError) = await InteractiveWorktreeGit.PushAsync(run.WorktreePath, run.Branch, cancellationToken);
        if (!pushed)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Push failed: {pushError}[/]");
            return ExitCodes.Error;
        }

        // Mirrors RunSupervisor.CaptureHandoffAsync, called in the same place relative to
        // AgentSessionCompleted: without this, an interactively delivered task hands nothing
        // down to a dependent — CloseoutEngine.ComposeHandoffAsync reads this same file off
        // disk at true closeout, agnostic of whether the run behind it was headless or
        // interactive, so writing it here is all that is missing (conformance review, cycle 1).
        string handoff = settings.Handoff ?? PromptForHandoff();
        await WriteHandoffAsync(run.RunDirectory, handoff, cancellationToken);

        session.Events.Append(runId, new AgentSessionCompleted(runId, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(cancellationToken);

        await Doorbell.RingAsync($"task-delivered:{taskId}", cancellationToken);
        AnsiConsole.MarkupLineInterpolated(
            $"[dim]Branch {run.Branch} pushed. Task {taskId} handed into the standard delivery pipeline — h9k task show {taskId} to watch it.[/]");
        return ExitCodes.Ok;
    }

    private static string PromptForHandoff()
    {
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            return string.Empty;
        }

        return AnsiConsole.Prompt(
            new TextPrompt<string>(
                "[dim]Handoff for a dependent task or a resuming session (blank to leave unauthored):[/]")
                .AllowEmpty());
    }

    private static async Task WriteHandoffAsync(string runDirectory, string handoff, CancellationToken cancellationToken)
    {
        string resolvedRunDirectory = RunPaths.ResolveCurrentDirectory(runDirectory);
        Directory.CreateDirectory(resolvedRunDirectory);
        await File.WriteAllTextAsync(RunPaths.HandoffFile(resolvedRunDirectory), handoff, cancellationToken);
    }
}

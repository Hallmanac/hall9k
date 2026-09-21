using System.ComponentModel;
using System.Globalization;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Processes;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Stands a pull request's own branch up on the reviewer's machine, by the project's run skill
/// (idea b9b09779, piece 5). The command an orchestrator window runs when a reviewer says yes to
/// the offer a QA or design review report ends with.
/// <para>
/// The reviewer names nothing. The offer in the report carries the identity — task, run, worktree
/// — so a yes resolves to exactly one branch and one checkout, and this command reads the rest off
/// that: the worktree the pr-review task already created, and the run skill that project recorded
/// on its ledger. Nothing here invents a launch command; a project with no run skill is refused
/// with the sentence saying so, because the alternative is a review offering to run something
/// nobody wrote down how to run.
/// </para>
/// <para>
/// The review itself never does this. That is the ruling this piece is built on: a review session
/// may drive the product to reach its own findings, but standing the branch up for a person to
/// walk happens on that person's word, through their own window, and never on a session's
/// judgment that they would probably want it.
/// </para>
/// </summary>
public sealed class TaskRunLocalCommand : Hall9kAsyncCommand<TaskRunLocalCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<TASK>")]
        [Description("Task id (full, or an unambiguous prefix) — the one the review report's offer names.")]
        public string Task { get; init; } = string.Empty;

        [CommandOption("--continue")]
        [Description(
            "Resume a launch that stopped at a human step, from the step after it. The launch "
            + "prints exactly what the step needs when it stops; this is what you run once you have "
            + "done it. The platform takes your word that you did — it cannot observe a login or a "
            + "credential, and does not pretend to.")]
        public bool Continue { get; init; }

        [CommandOption("--stop")]
        [Description(
            "End the launch: kills every process it started and records the stop. A launch is torn "
            + "down on its own when the task closes out or the worktree is removed, so this is for "
            + "finishing early rather than for cleaning up after yourself.")]
        public bool Stop { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Continue && settings.Stop)
        {
            throw new DomainValidationException(
                "--continue and --stop are opposites: one resumes a launch waiting on you and the "
                + "other ends it. Pass one or neither.");
        }

        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        return await RunAsync(session, settings, cancellationToken);
    }

    /// <summary>Internal so the integration tier can drive the whole command against a real store rather than only its parts.</summary>
    internal static async Task<int> RunAsync(
        IDocumentSession session, Settings settings, CancellationToken cancellationToken)
    {
        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Task, cancellationToken);
        TaskDetails task = await session.LoadAsync<TaskDetails>(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        RunDetails run = await CurrentRunAsync(session, task, cancellationToken);

        return settings.Stop ? await StopAsync(session, task, run, cancellationToken)
            : settings.Continue ? await ContinueAsync(session, task, run, cancellationToken)
            : await StartAsync(session, task, run, cancellationToken);
    }

    private static async Task<int> StartAsync(
        IDocumentSession session, TaskDetails task, RunDetails run, CancellationToken cancellationToken)
    {
        string worktree = RequireWorktree(task, run);
        ProjectDetails project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken)
            ?? throw new DomainNotFoundException($"No project {task.ProjectId} for task {task.Id}.");

        string skill = ProjectRunSkillReader.Read(project)
            ?? throw new DomainBusinessRuleException(LocalLaunchRefusal.NoRunSkill(project.Name));
        RunSkillPlan plan = RunSkillSteps.Parse(skill);
        if (plan.Steps.Count == 0)
        {
            throw new DomainBusinessRuleException(LocalLaunchRefusal.RunSkillHasNoSteps(project.Name));
        }

        RefuseOrClearExisting(session, task, run);

        Guid launchId = DomainId.New();
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        session.Events.Append(
            run.Id,
            new LocalLaunchStarted(
                run.Id, launchId, task.Id, context.NodeId, worktree, plan.Steps, ThisWalker(task, resuming: false),
                DateTimeOffset.UtcNow, context.OwnerId));
        await session.SaveChangesAsync(cancellationToken);

        AnsiConsole.MarkupLine(
            $"Standing task [bold]{Short(task.Id)}[/] up in [dim]{worktree.EscapeMarkup()}[/], "
            + $"by {project.Name.EscapeMarkup()}'s run skill ({plan.Steps.Count} step(s)).");
        AnsiConsole.WriteLine();

        // Cleared once, here, so the log holds this launch's output and not the last one's — the
        // spawn itself appends, because a run skill's launch section may start more than one
        // process into it.
        string log = LogFile(run);
        Directory.CreateDirectory(
            Path.GetDirectoryName(log) is { Length: > 0 } directory ? directory : worktree);
        File.WriteAllText(log, string.Empty);
        return await WalkAndRecordAsync(session, task, run, launchId, plan, worktree, log, 1, cancellationToken);
    }

    private static async Task<int> ContinueAsync(
        IDocumentSession session, TaskDetails task, RunDetails run, CancellationToken cancellationToken)
    {
        if (run.LocalLaunch is not { Live: true, AwaitingHumanAtStep: { } pausedAt } launch)
        {
            throw new DomainConflictException(LocalLaunchRefusal.NothingToContinue(task.Id));
        }

        string worktree = RequireWorktree(task, run);
        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        int from = pausedAt + 1;
        session.Events.Append(
            run.Id,
            new LocalLaunchResumed(
                run.Id, launch.LaunchId, from, ThisWalker(task, resuming: true), DateTimeOffset.UtcNow,
                context.OwnerId));
        await session.SaveChangesAsync(cancellationToken);

        AnsiConsole.MarkupLine(
            $"Resuming task [bold]{Short(task.Id)}[/] at step {from} of {launch.Steps.Count}.");
        AnsiConsole.WriteLine();

        // The steps come from the launch's own recorded plan, always: this walk continues the one
        // that started, and a run skill re-discovered by the ledger sweep while the reviewer was
        // away must not renumber the step underneath them. The two readout sections are re-read
        // off the project instead, because they are prose rather than steps, and the fresher
        // wording of "where to open it" is strictly the more useful one to hand somebody.
        RunSkillPlan current = RunSkillSteps.Parse(project is null ? null : ProjectRunSkillReader.Read(project));
        RunSkillPlan plan = new(
            launch.Steps,
            current.HowToKnowItIsUp,
            current.AddressOrEntryPoint.IsNotBlank() ? current.AddressOrEntryPoint : launch.Address);
        return await WalkAndRecordAsync(
            session, task, run, launch.LaunchId, plan, worktree, LogFile(run), from, cancellationToken);
    }

    private static async Task<int> StopAsync(
        IDocumentSession session, TaskDetails task, RunDetails run, CancellationToken cancellationToken)
    {
        if (run.LocalLaunch is not { Live: true } launch)
        {
            throw new DomainConflictException(LocalLaunchRefusal.NothingToStop(task.Id));
        }

        IReadOnlyList<int> ended = LocalLaunchProcesses.EndAll(launch);
        session.Events.Append(
            run.Id,
            new LocalLaunchStopped(
                run.Id, launch.LaunchId, LocalLaunchStopReason.Requested, ended, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(cancellationToken);

        AnsiConsole.MarkupLine(ended.Count > 0
            ? $"[green]Stopped[/] the local launch of task {Short(task.Id)}: ended "
              + $"{ended.Count} process tree(s) ({string.Join(", ", ended)})."
            : $"[green]Stopped[/] the local launch of task {Short(task.Id)}. Nothing of it was still "
              + "running, so there was no process left to end.");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// One pass over the plan, and the events and prose that pass earns. The three endings are the
    /// three the reviewer can be in: the product is up, a step needs them, or a command failed.
    /// <para>
    /// A pass knows only what it started itself, and a resume is a second pass over one launch, so
    /// everything below works from the union of this pass's processes and the ones this launch had
    /// already recorded. Getting that wrong is not cosmetic: a resumed walk that failed would have
    /// killed only its own half and left the rest running under a launch its own failure had
    /// already marked dead, which nothing afterwards ever looks at again.
    /// </para>
    /// </summary>
    private static async Task<int> WalkAndRecordAsync(
        IDocumentSession session, TaskDetails task, RunDetails run, Guid launchId, RunSkillPlan plan,
        string worktree, string log, int from, CancellationToken cancellationToken)
    {
        LocalLaunchState? recorded = run.LocalLaunch is { } existing && existing.LaunchId == launchId
            ? existing
            : null;

        // The port this launch already settled on goes back in, so a resumed pass's launch steps
        // land on the same number as the ones before the pause rather than choosing a second.
        LocalLaunchWalk walk = await LocalLaunchWalker.WalkAsync(
            plan, from, recorded?.Port, worktree, log, RunStepAsync, SpawnStep, EphemeralPort.Free,
            cancellationToken);

        IReadOnlyList<LocalLaunchProcess> running = [.. recorded?.Processes ?? [], .. walk.Processes];
        int? port = walk.Port ?? recorded?.Port;
        string address = RunSkillLaunchPort.ApplyToAddress(plan.AddressOrEntryPoint, port);

        // Recorded when this pass started something, and also when it finished the plan having
        // started nothing — a resume whose launch steps were already behind it still has to move
        // the cursor to the end, or the record reads as a launch stuck mid-plan while the product
        // is up in front of the reviewer.
        if (walk.Processes.Count > 0 || walk.Completed)
        {
            session.Events.Append(
                run.Id,
                new LocalLaunchRunning(
                    run.Id, launchId, running, port, address, walk.NextStepNumber, DateTimeOffset.UtcNow));
        }

        if (walk.PausedAtStep is { } pausedAt)
        {
            RunSkillStep step = plan.Steps[pausedAt - 1];
            session.Events.Append(
                run.Id,
                new LocalLaunchPausedForHuman(run.Id, launchId, pausedAt, step.Text, DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(cancellationToken);
            PrintPause(task, plan, step);
            return ExitCodes.Ok;
        }

        if (walk.FailedAtStep is { } failedAt)
        {
            RunSkillStep step = plan.Steps[failedAt - 1];

            // Whatever any launch step of this launch already started comes down with the failure,
            // in the one event: a half-launched product on a reviewer's machine is worse than none,
            // because it looks like the one they were offered.
            session.Events.Append(
                run.Id,
                new LocalLaunchFailed(
                    run.Id, launchId, failedAt, step.Command, walk.FailureReason,
                    LocalLaunchProcesses.EndAll(running), DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(cancellationToken);
            PrintFailure(plan, step, walk.FailureReason, log);
            return ExitCodes.Error;
        }

        await session.SaveChangesAsync(cancellationToken);
        PrintUp(task, plan, running, port, address, log);
        return ExitCodes.Ok;
    }

    /// <summary>
    /// This <c>h9k</c> process, recorded on the launch as the one walking its plan. It is what
    /// lets a second invocation tell a walk still in flight — a plan whose one-time setup step has
    /// been restoring for four minutes and has therefore started nothing — from a launch whose
    /// processes have all gone, since both records carry none
    /// (<see cref="LocalLaunchAdmission.ForStart"/>). Without it the second invocation reads the
    /// first as stale, clears it, and walks the same checkout alongside it.
    /// </summary>
    private static LocalLaunchProcess ThisWalker(TaskDetails task, bool resuming)
    {
        LaunchedProcess current = WorktreeShell.Current();
        return new LocalLaunchProcess(
            current.ProcessId, current.StartedAt,
            $"h9k task run-local {Short(task.Id)}{(resuming ? " --continue" : string.Empty)}");
    }

    private static Task<ShellResult> RunStepAsync(
        RunSkillStep step, string directory, CancellationToken cancellationToken)
    {
        Narrate(step, step.Command);
        return WorktreeShell.RunAsync(step.Command, directory, WorktreeShell.StepDeadline, cancellationToken);
    }

    private static LaunchedProcess SpawnStep(
        RunSkillStep step, string command, string directory, string log)
    {
        Narrate(step, command);
        return WorktreeShell.Spawn(command, directory, log);
    }

    /// <summary>
    /// Each step named as it runs. A launch can sit on a restore for minutes, and a window that
    /// printed nothing until it was done would leave the reviewer unable to tell a slow step from
    /// a wedged one.
    /// </summary>
    private static void Narrate(RunSkillStep step, string command) =>
        AnsiConsole.MarkupLine(
            $"[dim]{step.Number}. ({step.Section.EscapeMarkup()})[/] {command.EscapeMarkup()}");

    private static void PrintPause(TaskDetails task, RunSkillPlan plan, RunSkillStep step)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[yellow]Stopped at step {step.Number} of {plan.Steps.Count} — this one needs you.[/] "
            + $"[dim]({step.Section.EscapeMarkup()})[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine(step.Text);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"When it is done, run [bold]h9k task run-local {Short(task.Id)} --continue[/] and the "
            + $"launch picks up at step {step.Number + 1}.");
    }

    private static void PrintFailure(RunSkillPlan plan, RunSkillStep step, string reason, string log)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[red]Step {step.Number} of {plan.Steps.Count} failed[/] [dim]({step.Section.EscapeMarkup()})[/]: "
            + step.Command.EscapeMarkup());
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine(reason);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[dim]Nothing is left running. Anything the launch had already started is in {log.EscapeMarkup()}.[/]");
    }

    private static void PrintUp(
        TaskDetails task, RunSkillPlan plan, IReadOnlyList<LocalLaunchProcess> running, int? chosenPort,
        string address, string log)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]Up.[/] {Processes(running)}");
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[bold]Address or entry point[/]");
        AnsiConsole.WriteLine(address.IsNotBlank()
            ? address
            : "This project's run skill names no address or entry point, so there is nothing here to open; "
              + "see its own \"How to know it is up\" below for what it does say.");
        if (chosenPort is { } port)
        {
            AnsiConsole.MarkupLine(
                $"[dim]On ephemeral port {port.ToString(CultureInfo.InvariantCulture)}, chosen because the "
                + "run skill's launch command had somewhere to put one — not this project's usual port.[/]");
        }

        if (plan.HowToKnowItIsUp.IsNotBlank())
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]How to know it is up[/]");
            AnsiConsole.WriteLine(plan.HowToKnowItIsUp);
        }

        // Every human step, again, in plan order. They have each already been printed and waited
        // on one at a time, which is the wrong shape for somebody now about to walk the change:
        // what they want in front of them is the whole list of things only a person can do, not
        // whichever one they were last asked about.
        if (plan.HumanSteps.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Human steps from this project's run skill, in order[/]");
            foreach (RunSkillStep step in plan.HumanSteps)
            {
                AnsiConsole.MarkupLine($"[dim]{step.Number}. ({step.Section.EscapeMarkup()})[/]");
                AnsiConsole.WriteLine(step.Text);
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[dim]Output: {log.EscapeMarkup()}[/]");
        AnsiConsole.MarkupLine(
            $"[dim]Stop it with h9k task run-local {Short(task.Id)} --stop. It also comes down on its own "
            + "when the task closes out or the worktree is removed.[/]");
    }

    /// <summary>
    /// Applies <see cref="LocalLaunchAdmission.ForStart"/>: refuse, or close out a record whose
    /// processes are already gone so the fresh launch is not the second live one on this run.
    /// </summary>
    private static void RefuseOrClearExisting(IDocumentSession session, TaskDetails task, RunDetails run)
    {
        LocalLaunchAdmission admission = LocalLaunchAdmission.ForStart(
            run.LocalLaunch, task.Id,
            process => WorktreeShell.IsAlive(process.ProcessId, process.StartedAt));
        if (admission.Refusal is { } refusal)
        {
            throw new DomainConflictException(refusal);
        }

        if (admission.ClearStaleLaunch && run.LocalLaunch is { } stale)
        {
            session.Events.Append(
                run.Id,
                new LocalLaunchStopped(
                    run.Id, stale.LaunchId, LocalLaunchStopReason.ProcessGone, [], DateTimeOffset.UtcNow));
        }
    }

    private static string RequireWorktree(TaskDetails task, RunDetails run)
    {
        if (run.WorktreePath.IsBlank())
        {
            throw new DomainBusinessRuleException(LocalLaunchRefusal.NoWorktreeRecorded(task.Id));
        }

        return Directory.Exists(run.WorktreePath)
            ? run.WorktreePath
            : throw new DomainBusinessRuleException(LocalLaunchRefusal.WorktreeGone(run.WorktreePath));
    }

    private static async Task<RunDetails> CurrentRunAsync(
        IDocumentSession session, TaskDetails task, CancellationToken cancellationToken)
    {
        if (task.CurrentRunId is not { } runId)
        {
            throw new DomainConflictException(LocalLaunchRefusal.NoRun(task.Id));
        }

        return await session.LoadAsync<RunDetails>(runId, cancellationToken)
            ?? throw new DomainConflictException(LocalLaunchRefusal.NoRun(task.Id));
    }

    private static string LogFile(RunDetails run) =>
        RunPaths.LocalLaunchLogFile(RunPaths.ResolveCurrentDirectory(run.RunDirectory));

    private static string Processes(IReadOnlyList<LocalLaunchProcess> running) =>
        running.Count switch
        {
            0 => "This project's run skill has no launch command in it, so nothing was started; "
                + "everything below is what the skill itself says.",
            1 => $"One process left running (pid {running[0].ProcessId}).",
            var count => $"{count} processes left running "
                + $"(pids {string.Join(", ", running.Select(process => process.ProcessId))}).",
        };

    private static string Short(Guid id) => DomainId.Short(id);
}

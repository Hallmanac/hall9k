using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The readiness gate (Decisions Log #34). Publishing is the quality decision, not the go
/// signal: it says the contract is complete and the dependency graph is sane, after which the
/// task is immutable and assignable. Starting it is a separate, explicit act.
/// </summary>
public sealed class TaskPublishCommand : Hall9kAsyncCommand<TaskPublishCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--assign [OWNER]")]
        [Description(
            "Assign the task in the same breath, so it dispatches: the owner's name, an unambiguous "
            + "fragment, or their id — or the bare flag when the platform has exactly one owner. This "
            + "is the same explicit TaskAssigned event h9k task assign appends, never a silent one")]
        public FlagValue<string> Assign { get; init; } = new();

        [CommandOption("--no-assign")]
        [Description(
            "Publish and stop there, without being asked about assignment. Use it in scripts: an "
            + "interactive terminal is otherwise offered the single-owner assignment as a convenience")]
        public bool NoAssign { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Assign.IsSet && settings.NoAssign)
        {
            throw new DomainValidationException("--assign and --no-assign say opposite things; pass one.");
        }

        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        // The whole reachable chain, not just the first hop: a cycle three tasks away is still
        // a cycle this task could never run inside.
        TaskDependencyGraph graph = await TaskDependencyQuery.LoadGraphAsync(
            session, task.BlockedBy, cancellationToken);

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        TaskPublished published = TaskDecider.Publish(task, graph, DateTimeOffset.UtcNow, context.OwnerId);
        session.Events.Append(taskId, published);
        task.Apply(published);

        // Resolve the assignee and commit before announcing anything. Both steps can still throw
        // — a bare --assign with several owners registered, a name that matches nobody — and the
        // session is then disposed unsaved, leaving the task a Draft. Announcing the publish
        // first would tell a human (or an agent reading the message to self-correct) about a
        // state change the failed transaction never made.
        OwnerDetails? assignee = await ChooseAssigneeAsync(session, settings, cancellationToken);
        TaskAssigned? assigned = assignee is null
            ? null
            : await TaskAssignCommand.AppendAsync(session, task, assignee.Id, context.OwnerId, cancellationToken);

        await session.SaveChangesAsync(cancellationToken);

        string shortId = TaskListCommand.ShortId(taskId);
        AnsiConsole.MarkupLine(
            $"[green]Task {shortId} published[/]: {TaskListCommand.Truncate(task.Objective, 72).EscapeMarkup()}");

        if (assignee is null || assigned is null)
        {
            AnsiConsole.MarkupLine(
                $"[dim]It is ready to assign but will not run until you say so:[/] h9k task assign {shortId}");
            return ExitCodes.Ok;
        }

        await Doorbell.RingAsync($"task-assigned:{taskId}", cancellationToken);
        await TaskAssignCommand.AnnounceAsync(assigned, assignee, session, cancellationToken);
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Who to assign to, or null for "nobody yet". The flag is an explicit answer either way.
    /// The interactive offer exists only where it cannot be wrong: exactly one owner is
    /// registered, so "assign it" has one possible meaning. With more than one owner it is
    /// never offered — deciding whose nodes run a task is the human's call, and a prompt that
    /// guessed would be the multi-owner mistake IDEA-task-assignment exists to avoid.
    /// </summary>
    private static async Task<OwnerDetails?> ChooseAssigneeAsync(
        IQuerySession session, Settings settings, CancellationToken cancellationToken)
    {
        if (settings.NoAssign)
        {
            return null;
        }

        if (settings.Assign.IsSet)
        {
            return settings.Assign.Value.IsNotBlank()
                ? await OwnerResolver.ResolveAsync(session, settings.Assign.Value, cancellationToken)
                : await OwnerResolver.SoleOwnerAsync(session, cancellationToken)
                    ?? throw new DomainValidationException(
                        "More than one owner is registered, so a bare --assign cannot say who this task "
                        + "is for. Name them: h9k task publish <id> --assign <owner>");
        }

        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            return null;
        }

        OwnerDetails? sole = await OwnerResolver.SoleOwnerAsync(session, cancellationToken);
        return sole is not null && AnsiConsole.Confirm(
            $"Assign it to {sole.Name.EscapeMarkup()} now, so it can dispatch?", defaultValue: false)
            ? sole
            : null;
    }
}

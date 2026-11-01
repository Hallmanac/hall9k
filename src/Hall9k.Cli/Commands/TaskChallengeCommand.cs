using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using JasperFx.Events;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Ask for an independent challenge of a draft: one headless session, started by the daemon on this node, that tests the
/// draft's claims against the code and reports a verdict (publish as written, publish as revised, or do not publish)
/// with each change marked must, should, or park. It records the request, rings the doorbell, and returns without
/// waiting; nothing here runs a session.
/// <para>
/// A challenge changes nothing about the draft: applying a verdict is the person's act, by <c>h9k task revise</c>, before
/// <c>h9k task publish</c>. The request is node-scoped (<see cref="DraftChallengeRequested"/>), so it never leaves this
/// node and no other node starts a session for it. Repeating the verb after a recorded verdict or failure asks for
/// another; nothing repeats it on its own.
/// </para>
/// </summary>
public sealed class TaskChallengeCommand : Hall9kAsyncCommand<TaskChallengeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment). It must be a Draft with acceptance criteria.")]
        public string Id { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);

        // Fenced on the version this decision reads: a second request, a revise, or a publish landing while this
        // command does its own reads would otherwise be absorbed, and a published task must never gain a request.
        StreamState fence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        ProjectDetails project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId} names a project that is not registered.");

        // Whoever may publish the task may challenge it: the same guard, because a verdict is what opens the publish gate.
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        await TaskOwnerGuard.AssertMayActAsync(session, task, context, cancellationToken);

        string checkout = ProjectCheckout.ForReading(project);
        string? missingCheckout = !Directory.Exists(checkout) || ProjectCheckout.IsBare(checkout)
            ? checkout
            : null;

        DraftChallengeRequested requested = TaskDecider.RequestChallenge(
            task, DateTimeOffset.UtcNow, context.OwnerId, project.IsArchived, project.Name, missingCheckout);
        session.Events.Append(taskId, expectedVersion: fence.Version + 1, requested);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Task {taskId} changed while this request was being prepared, so nothing was requested. "
                + $"Check h9k task show {taskId} and run h9k task challenge again if it is still a Draft with no challenge outstanding.");
        }

        await Doorbell.RingAsync($"challenge-requested:{taskId}", cancellationToken);

        string shortId = TaskListCommand.ShortId(taskId);
        AnsiConsole.MarkupLine(
            $"[blue]Challenge requested[/] for task {shortId}: {ExternalText.OneLineMarkup(task.Objective)}");
        AnsiConsole.MarkupLine(
            "[dim]  This returns without waiting. The daemon on this node starts one headless session that tests the "
            + "draft's claims against the code and writes a report; it records the verdict and the counts of its "
            + "must, should, and park changes, and the project's orchestrator feed carries the outcome. A request made "
            + "while the daemon is stopped waits for it. The draft itself is not changed: apply what you accept with "
            + "h9k task revise, then publish. Read the outcome with:[/] "
            + $"h9k task show {shortId}");
        return ExitCodes.Ok;
    }
}

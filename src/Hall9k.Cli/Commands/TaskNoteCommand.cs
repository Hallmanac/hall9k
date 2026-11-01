using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Leaves a short note on a task for the sessions that work it next. Works from a plain terminal,
/// an attended session and a dispatched session, on a task in any state; inside a dispatched
/// session it works on that session's own task and no other. The note is one project-scoped event
/// on the task's stream (<see cref="TaskNoteRecorded"/>), so it travels exactly as far as the task
/// does, and it is permanent and public.
/// <para>
/// What the note names as its provenance is observed, never inferred: a dispatched session names
/// its own run (and the session's id and name), an attended session names the attended run, and a
/// note typed at a plain terminal names no run even while the task has a live one. The
/// recording node is stamped at the append because the task projection is Inline and reads it
/// before the listener that would otherwise stamp it (<see cref="EventRecordingNode.StampAtAppend"/>);
/// without the stamp every agent note would read as recorded on an unobserved node and be held out
/// of every prompt.
/// </para>
/// <para>
/// The append carries no <c>expectedVersion</c>, by the rule <see cref="TaskLogInteractionCommand"/>
/// documents: a note is a pure log entry, and a fenced note could lose a record. A fenced writer that
/// loses a race to a note recovers the way it does for any concurrent writer.
/// </para>
/// </summary>
public sealed class TaskNoteCommand : Hall9kAsyncCommand<TaskNoteCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<TASK>")]
        [Description("Task id (full, or an unambiguous prefix). Inside a dispatched session, only the session's own task.")]
        public string Task { get; init; } = string.Empty;

        [CommandArgument(1, "<TEXT>")]
        [Description(
            "The note, at most 1,000 characters: something you learned about THIS task that the sessions "
            + "after you would otherwise rediscover. A claim meant to outlive the task belongs in h9k "
            + "learn instead. A note is permanent and public, recorded in the project's shared ledger, so "
            + "never put secrets, credentials, personal data, customer details, or another project's "
            + "name in one.")]
        public string Text { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        // Refused before the store ever opens: blank or over-long text records nothing.
        TaskNoteDecider.RequireText(settings.Text);

        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        TaskNoteRecorded recorded = await RunAsync(
            session, settings, context.OwnerId, context.NodeId, Environment.GetEnvironmentVariable,
            DateTimeOffset.UtcNow, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        AnsiConsole.MarkupLine(
            $"[blue]Note recorded[/] [dim]({recorded.NoteId})[/] on task {DomainId.Short(recorded.Id)}");
        AnsiConsole.MarkupLine(recorded.Provenance.RunId is { } runId
            ? $"[dim]  from run:[/] {DomainId.Short(runId)}"
            : "[dim]  from run: none, recorded outside any run[/]");
        AnsiConsole.MarkupLine(
            "[dim]It is permanent and public. Sessions on this task read it from their prompt; a person reads it with:[/] "
            + $"h9k task notes {DomainId.Short(recorded.Id)}");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Everything but the store round trip and the printing, so the write is testable through the
    /// same seam <see cref="LearnCommand.RunAsync"/> uses. <paramref name="environment"/> is the
    /// process environment, handed in so a test never mutates the real one.
    /// </summary>
    internal static async Task<TaskNoteRecorded> RunAsync(
        IDocumentSession session,
        Settings settings,
        Guid ownerId,
        Guid thisNodeId,
        Func<string, string?> environment,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        string text = TaskNoteDecider.RequireText(settings.Text);
        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Task, cancellationToken);

        string? dispatchedRun = environment(DispatchedRunEnvironment.RunIdVariable);
        if (!string.IsNullOrWhiteSpace(dispatchedRun))
        {
            RefuseOtherTask(taskId, environment(DispatchedRunEnvironment.TaskIdVariable), dispatchedRun);
        }

        TaskDetails task = await session.LoadAsync<TaskDetails>(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        // Never inferred from the task's own current run: a dispatched session names the run its
        // environment carries (and its own session), an attended session names the attended run, and a
        // plain terminal names no run at all.
        (RecordedProvenance provenance, string? sessionId, string? sessionName) = string.IsNullOrWhiteSpace(dispatchedRun)
            ? (await ObserveAttendedOrShellAsync(session, task, ownerId, environment, cancellationToken), null, null)
            : (DispatchedProvenance(ownerId, dispatchedRun, taskId),
                environment(DispatchedRunEnvironment.SessionIdVariable),
                environment(DispatchedRunEnvironment.SessionNameVariable));

        TaskNoteRecorded note = TaskNoteDecider.Record(taskId, text, provenance, sessionId, sessionName, now);
        StreamAction stream = session.Events.Append(taskId, note);
        EventRecordingNode.StampAtAppend(stream, thisNodeId);
        return note;
    }

    /// <summary>
    /// What a process that is not a dispatched session can be observed to be. A session this platform
    /// launched itself (<c>--direct-launch</c>) carries the attended run in its environment; a session
    /// that was pasted a launch line and registered itself does not, so the task's current run is
    /// checked against this process's own <c>CLAUDE_PID</c> as well. Anything else is a plain terminal
    /// and names no run.
    /// </summary>
    private static async Task<RecordedProvenance> ObserveAttendedOrShellAsync(
        IDocumentSession session, TaskDetails task, Guid ownerId, Func<string, string?> environment,
        CancellationToken cancellationToken)
    {
        RecordedProvenance fromEnvironment = (await RecordingProvenanceReader.ObserveAsync(
            session, taskReference: null, ownerId, cancellationToken, environment)).Provenance;

        return fromEnvironment.RunId is not null
            ? fromEnvironment
            : await RecordingProvenanceReader.ObserveSelfRegisteredSessionAsync(
                    session, task, ownerId, environment, cancellationToken)
                ?? fromEnvironment;
    }

    /// <summary>
    /// A dispatched session writes on its own task only. The refusal names both tasks, so an agent
    /// that mistyped an id can see which is which and a person reading the log can too.
    /// </summary>
    private static void RefuseOtherTask(Guid requestedTaskId, string? ownTaskText, string runText)
    {
        if (!Guid.TryParse(ownTaskText, out Guid ownTaskId))
        {
            throw new DomainBusinessRuleException(
                $"h9k task note is refused for task {requestedTaskId}: this process is run {runText}, a dispatched "
                + "session with no task of its own, and a dispatched session leaves notes on its own task only.");
        }

        if (ownTaskId != requestedTaskId)
        {
            throw new DomainBusinessRuleException(
                $"h9k task note is refused for task {requestedTaskId}: this process is run {runText}, a dispatched "
                + $"session working task {ownTaskId}, and a dispatched session leaves notes on its own task "
                + $"({ownTaskId}) only, never on another ({requestedTaskId}).");
        }
    }

    private static RecordedProvenance DispatchedProvenance(Guid ownerId, string runText, Guid taskId) =>
        Guid.TryParse(runText, out Guid runId)
            ? new RecordedProvenance(ownerId, runId, taskId, HumanAttendance.Unattended)
            : RecordedProvenance.FromShell(ownerId);
}

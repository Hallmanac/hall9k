using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// h9k task handoff: the holding node leaves a note for whoever holds this task next (idea
/// 202383dc, item 3) — what is done, what is half done, what to watch, for work still in flight.
/// Not <c>Hall9k.Domain.Features.Run.Events.RunHandoffRecorded</c>, the closeout handoff to a
/// dependent task appended only at merge (Decisions Log #36): this is a task-stream event, and it
/// carries the note wherever the task itself replicates. The note's own source of truth is that
/// event; the ledger record's own field is the same event's own projection, composed by
/// <see cref="TaskRecordPublication"/> exactly the way every other field is (idea 202383dc's own
/// event-alignment ruling, 2026-09-13); the message queued alongside it is only a nudge — its body
/// carries no payload, and nothing but <c>h9k status</c>'s own unread count reads it.
/// </summary>
public sealed class TaskHandoffCommand : Hall9kAsyncCommand<TaskHandoffCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--text <TEXT>")]
        [Description("The note itself, quoted. Pass this or --file, never both.")]
        public string? Text { get; init; }

        [CommandOption("--file <PATH>")]
        [Description("Read the note from a file instead of typing it with --text.")]
        public string? File { get; init; }

        [CommandOption("--to <OWNER>")]
        [Description(
            "Nudge one owner's every node instead of the whole project: their root fingerprint "
            + "(h9k owner show prints it). Left off, the nudge goes to the whole project.")]
        public string? To { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        string note = await ResolveNoteAsync(settings, cancellationToken);

        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        StreamState? fence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        string? ownerRootFingerprint = await OwnerRootFingerprintResolver.ResolveAsync(
            session, context.OwnerId, cancellationToken);
        if (ownerRootFingerprint is null)
        {
            throw new DomainValidationException(
                "This node's owner has not claimed a root fingerprint yet, so the handoff note's own "
                + "author field has nothing to carry. Run h9k project join first.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        TaskHandoffNoted noted = TaskDecider.LeaveHandoff(task, note, context.NodeId, ownerRootFingerprint, now);
        session.Events.Append(taskId, expectedVersion: fence.Version + 1, noted);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Task {taskId} changed while leaving this note — check h9k status and try again.");
        }

        AnsiConsole.MarkupLine($"[blue]Handoff note left on task {taskId}.[/]");

        // Re-aggregated rather than mutated by hand: this is the identical event-sourced value
        // TaskRecordPublication.ComposeAsync will read, so the record it writes below is never a
        // fact this command merely believes landed.
        TaskAggregate updated = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken))!;
        await RewriteRecordAsync(session, updated, context, cancellationToken);
        await QueueNudgeAsync(session, updated.ProjectId, taskId, context, ownerRootFingerprint, settings.To, now, cancellationToken);

        return ExitCodes.Ok;
    }

    /// <summary>
    /// Checked before the store ever opens, the same discipline every other CLI command's own
    /// cheap-local-mistake gate follows (<c>TaskLogInteractionCommand.Validate</c>): a mistyped or
    /// missing option costs nothing here, rather than paying for a task resolution and a node
    /// bootstrap first.
    /// </summary>
    internal static async Task<string> ResolveNoteAsync(Settings settings, CancellationToken cancellationToken)
    {
        bool hasText = settings.Text.IsNotBlank();
        bool hasFile = settings.File.IsNotBlank();
        if (!hasText && !hasFile)
        {
            throw new DomainValidationException("The note needs --text or --file.");
        }

        if (hasText && hasFile)
        {
            throw new DomainValidationException("--text and --file both name the note; pass one.");
        }

        string note = hasText
            ? settings.Text!
            : await System.IO.File.ReadAllTextAsync(settings.File!, cancellationToken);
        note = note.Trim();
        return note.IsBlank()
            ? throw new DomainValidationException("The handoff note is blank.")
            : note;
    }

    /// <summary>
    /// Rewrite this task's record in the ledger so every node that shares the project sees the
    /// latest note (idea 202383dc, item 3, criterion 2) — best effort, like every other ledger
    /// write around a committed transaction: the event already landed either way, and the write is
    /// idempotent — the next handoff, publish, or revise writes it again.
    /// </summary>
    private static async Task RewriteRecordAsync(
        IDocumentSession session, TaskAggregate task, BootstrapContext context, CancellationToken cancellationToken)
    {
        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
        if (project is null)
        {
            return;
        }

        try
        {
            NodeDetails? node = await session.LoadAsync<NodeDetails>(context.NodeId, cancellationToken);
            (LedgerCommitter committer, LedgerSigningKey signingKey, string ownerFingerprint) =
                await TaskRecordPublication.ResolveIdentityAsync(session, context, cancellationToken);
            await TaskRecordPublication.WriteAsync(
                session, task, project, context.NodeId, node?.MachineName ?? Environment.MachineName,
                ownerFingerprint, DateTimeOffset.UtcNow, new GitLedger(new ConsoleWorktreeLogger<GitLedger>()),
                committer, signingKey, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]  Note:[/] [dim]The handoff note landed, but rewriting its task record failed: "
                + $"{exception.Message.EscapeMarkup()} The next publish, revise, or handoff writes it.[/]");
        }
    }

    /// <summary>
    /// Queues the nudge alongside the note (idea 202383dc, item 3, criterion 3) — never touches git
    /// or a network itself; the daemon's own message sweep is what actually lands it. Carries no
    /// payload beyond pointing the reader at the task: the note itself already travelled on the
    /// task's own event stream and is what the ledger record and the resuming prompt both read.
    /// </summary>
    private static async Task QueueNudgeAsync(
        IDocumentSession session, Guid projectId, Guid taskId, BootstrapContext context,
        string ownerRootFingerprint, string? to, DateTimeOffset now, CancellationToken cancellationToken)
    {
        MessageAudience audience = to.IsNotBlank() ? MessageAudience.Owner(to) : MessageAudience.Project;
        await MessageOutbox.QueueAsync(
            session, context.NodeId, projectId, ownerRootFingerprint, audience, about: taskId.ToString(),
            MessageKind.Handoff, $"Task {taskId} got a handoff note — see h9k task show {taskId}.", now,
            cancellationToken);
        AnsiConsole.MarkupLine(
            $"[dim]Nudge queued to {audience.Value} — the daemon's next message sweep sends it.[/]");
    }
}

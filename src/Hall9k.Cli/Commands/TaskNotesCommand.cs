using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Prompts;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Notes;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Prints a task's notes for a person: its own newest first, then those of the other tasks in its
/// epic in this project. Held notes (the trust rule keeps them out of every prompt) are shown here,
/// marked as held, because a note held back from a prompt is still the record and a person is the one
/// who can judge it.
/// <para>
/// Refused inside any dispatched session, review or not (<see cref="DispatchedSessionAccess.PersonOnly"/>):
/// a session receives its notes through its prompt, and this verb is how a held-back note or one past
/// the cap would otherwise reach a session that is not meant to see it. Note text is text any node that
/// was once trusted could have written, so it goes through <see cref="ExternalText"/> before it reaches
/// the terminal, and a replicated note names its author the way a replicated handoff note does.
/// </para>
/// </summary>
public sealed class TaskNotesCommand : Hall9kAsyncCommand<TaskNotesCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<TASK>")]
        [Description("Task id (full, or an unambiguous prefix)")]
        public string Task { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Task, cancellationToken);
        TaskDetails task = await session.LoadAsync<TaskDetails>(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        Guid ownerId = (await OwnerResolver.ResolveOrSoleAsync(session, null, cancellationToken)).Id;
        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
        LocalFleetSource? fleet = project is null ? null : new LocalFleetSource(session, project, ownerId);

        IReadOnlyList<TaskNoteView> views = await TaskNoteFeed.LoadViewsAsync(
            session, taskId, fleet?.LessonReader, cancellationToken);
        LocalFleet? localFleet = views.Any(view => view.Entry.IsReplicated) && fleet is not null
            ? await fleet.GetAsync(cancellationToken)
            : null;

        foreach (string line in Lines(taskId, views, localFleet))
        {
            AnsiConsole.MarkupLine(line);
        }

        return ExitCodes.Ok;
    }

    /// <summary>
    /// The markup lines this prints, pulled out so the rendering is testable without a store: a
    /// control character in a note acts on no terminal, and nothing in a note is read as markup.
    /// </summary>
    internal static IReadOnlyList<string> Lines(Guid taskId, IReadOnlyList<TaskNoteView> views, LocalFleet? localFleet)
    {
        List<string> lines = [];
        List<TaskNoteView> own = [.. views.Where(view => view.IsOwn)];
        List<TaskNoteView> siblings = [.. views.Where(view => !view.IsOwn)];

        lines.Add($"[bold]Notes on task {DomainId.Short(taskId)}[/] [dim]({own.Count} on this task, "
            + $"{siblings.Count} on others in its epic; newest first)[/]");
        if (views.Count == 0)
        {
            lines.Add("[dim]No notes recorded. Leave one:[/] h9k task note " + DomainId.Short(taskId) + " \"…\"");
            return lines;
        }

        foreach (TaskNoteView view in own)
        {
            lines.AddRange(Entry(view, localFleet));
        }

        if (siblings.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("[bold]Other tasks in this task's epic[/]");
            foreach (TaskNoteView view in siblings)
            {
                lines.AddRange(Entry(view, localFleet));
            }
        }

        if (views.Any(view => !view.ReachesAPrompt))
        {
            lines.Add(string.Empty);
            lines.Add("[dim]Notes marked HELD are kept out of every session's prompt by the trust rule lessons "
                + "use; they are shown here so a person can judge them.[/]");
        }

        return lines;
    }

    private static IEnumerable<string> Entry(TaskNoteView view, LocalFleet? localFleet)
    {
        TaskNoteRecorded note = view.Entry.Note;
        string held = view.ReachesAPrompt ? string.Empty : " [red]HELD[/]";
        string task = view.IsOwn ? string.Empty : $"task {DomainId.Short(view.TaskId)} · ";
        string session = note.SessionName.IsNotBlank()
            ? ExternalText.OneLineMarkup(note.SessionName)
            : note.SessionId.IsNotBlank() ? ExternalText.OneLineMarkup(note.SessionId) : "no session";
        yield return $"[dim]{task}[[{note.NoteId}]] {note.RecordedAt.ToLocalTime():yyyy-MM-dd HH:mm} · "
            + $"{Author(view.Entry, localFleet).EscapeMarkup()} · session: {session} · "
            + $"source: {note.Source.Label.EscapeMarkup()} · {view.Mark.Label.EscapeMarkup()}[/]{held}";
        yield return "  " + ExternalText.ForTerminalMarkup(note.Text).ReplaceLineEndings("\n  ");
    }

    /// <summary>
    /// Who wrote the note. A replicated note names its sender the way a replicated handoff note does
    /// (<see cref="ReplicatedNote.Origin"/>), because the recorded owner id resolves to nobody on
    /// another node; a note this node recorded names its owner.
    /// </summary>
    private static string Author(TaskNoteEntry entry, LocalFleet? localFleet) =>
        entry.ReceivedFromNodeId is { } sender
            ? ReplicatedNote.Origin(sender, entry.OriginNodeId, localFleet)
            : $"owner {DomainId.Short(entry.Note.Provenance.RecordedByOwnerId)}";
}

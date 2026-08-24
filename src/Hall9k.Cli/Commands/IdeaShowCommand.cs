using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// One idea in one pane: what it says now, what it used to say, where its discovery workspace
/// is, and what it became. The history is the point — an idea's value is often in how the
/// thinking moved, not in its final wording.
/// </summary>
public sealed class IdeaShowCommand : Hall9kAsyncCommand<IdeaShowCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Idea id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        Guid ideaId = await IdeaIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        IdeaDetails idea = await session.LoadAsync<IdeaDetails>(ideaId, cancellationToken)
            ?? throw new DomainNotFoundException($"No idea {ideaId}.");

        Table header = new Table().Border(TableBorder.None).HideHeaders();
        header.AddColumns("k", "v");
        header.AddRow("[bold]Idea[/]", idea.Text.EscapeMarkup());
        header.AddRow("State", StateMarkup(idea.State));
        header.AddRow("Id", $"[dim]{idea.Id}[/]");
        header.AddRow("Project", await ProjectMarkupAsync(session, idea, cancellationToken));
        header.AddRow("Captured", $"{idea.CapturedAt.ToLocalTime():g} "
            + $"[dim]({TaskStatusComposer.RelativeAge(DateTimeOffset.UtcNow - idea.CapturedAt)})[/]");
        header.AddRow("Captured by", await OwnerMarkupAsync(session, idea.OwnerId, cancellationToken));
        header.AddRow("Workspace", WorkspaceMarkup(idea));
        AnsiConsole.Write(header);

        if (idea.History.Count > 1)
        {
            AnsiConsole.MarkupLine(
                $"\n[bold]Discovery history[/] [dim]({idea.Revisions} revision(s); the note as it was written each time)[/]");
            for (int index = 0; index < idea.History.Count; index++)
            {
                IdeaNote note = idea.History[index];
                string label = index == 0 ? "captured" : $"revised {index}";
                string marker = index == idea.History.Count - 1 ? "[green]→[/]" : " ";
                AnsiConsole.MarkupLine(
                    $"  {marker} [dim]{note.WrittenAt.ToLocalTime():g} · {label}[/] {note.Text.EscapeMarkup()}");
            }
        }

        await AnnounceOutcomeAsync(session, idea, cancellationToken);
        return ExitCodes.Ok;
    }

    /// <summary>
    /// The workspace is a plain directory on disk, so what it holds is counted when someone
    /// looks. An empty one is not a problem to report — it is an invitation.
    /// </summary>
    private static string WorkspaceMarkup(IdeaDetails idea)
    {
        string ideaDirectory = IdeaPaths.ResolveDirectory(
            idea.WorkspaceHome, ProjectHomePaths.EntryDirectoryName(idea.Id, idea.Text), idea.Id);
        string path = IdeaPaths.WorkspaceDirectory(ideaDirectory);
        return IdeaPaths.FileCount(ideaDirectory) switch
        {
            null => $"{path.EscapeMarkup()} [dim](not created yet)[/]",
            0 => $"{path.EscapeMarkup()} [dim](empty — research notes, gathered files, and prototypes go here)[/]",
            1 => $"{path.EscapeMarkup()} [dim](1 file)[/]",
            int count => $"{path.EscapeMarkup()} [dim]({count} files)[/]",
        };
    }

    private static async Task<string> ProjectMarkupAsync(
        IQuerySession session, IdeaDetails idea, CancellationToken cancellationToken)
    {
        if (idea.ProjectId is not { } projectId)
        {
            return "[dim]none — an idea may precede its project, or become one[/]";
        }

        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(projectId, cancellationToken);
        return project is null ? $"[dim]{projectId}[/]" : project.Name.EscapeMarkup();
    }

    private static async Task<string> OwnerMarkupAsync(
        IQuerySession session, Guid ownerId, CancellationToken cancellationToken)
    {
        OwnerDetails? owner = await session.LoadAsync<OwnerDetails>(ownerId, cancellationToken);
        return owner is null ? $"[dim]{ownerId}[/]" : owner.Name.EscapeMarkup();
    }

    /// <summary>What the idea became, or the one act it is waiting for.</summary>
    private static async Task AnnounceOutcomeAsync(
        IQuerySession session, IdeaDetails idea, CancellationToken cancellationToken)
    {
        string shortId = TaskListCommand.ShortId(idea.Id);
        if (idea.PromotedTaskId is { } taskId)
        {
            TaskDetails? task = await session.LoadAsync<TaskDetails>(taskId, cancellationToken);
            string taskShortId = TaskListCommand.ShortId(taskId);
            AnsiConsole.MarkupLine(
                $"\n[green]Promoted[/] [dim]{idea.PromotedAt?.ToLocalTime():g}[/] into task [dim]{taskShortId}[/] "
                + (task is null ? string.Empty : $"{ExternalText.OneLineMarkup(task.Objective)} [dim]({task.State.Value})[/]"));
            AnsiConsole.MarkupLine(
                $"[dim]Discovery ended there; refinement continues on the draft:[/] h9k task show {taskShortId}");
            return;
        }

        if (idea.State == IdeaState.Discarded)
        {
            AnsiConsole.MarkupLine(
                $"\n[dim]Discarded {idea.DiscardedAt?.ToLocalTime():g}:[/] {idea.DiscardReason.EscapeMarkup()}");
            AnsiConsole.MarkupLine("[dim]Kept on the record — if the thought comes back, that is a signal.[/]");
            return;
        }

        AnsiConsole.MarkupLine(
            $"\n[dim]In discovery — what is this? Sharpen it:[/] h9k idea revise {shortId} \"…\" "
            + "[dim]· when it has intent:[/] h9k idea promote " + shortId
            + (idea.ProjectId is null ? " --project <name>" : string.Empty));
    }

    private static string StateMarkup(IdeaState state) => state.Value switch
    {
        "Captured" => "[blue]Captured[/] [dim](in discovery)[/]",
        "Promoted" => "[green]Promoted[/]",
        "Discarded" => "[dim]Discarded[/]",
        _ => state.Value.EscapeMarkup(),
    };
}

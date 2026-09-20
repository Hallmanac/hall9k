using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
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
        header.AddRow("Scope", ScopeInput.Markup(idea.Scope));
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

        // Aggregated fresh from events rather than read off idea (IdeaDetails), the Inline
        // projection: a terminal idea's document is only ever rewritten when its stream gets a
        // new event, and a terminal idea never gets one, so a document an old build's
        // IdeaPromoted/IdeaDiscarded handler last wrote keeps the old field shape forever
        // (IdeaDetailsProjectionBackfill's own doc comment) until daemon backfill runs. The fresh
        // aggregate is what both the fan-out list and the outcome below are read from, so neither
        // one depends on that backfill having happened (independent post-PR review — AnnounceOutcome
        // previously read the possibly-stale idea document directly).
        IdeaAggregate live = await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No idea {ideaId}.");
        await WriteFanOutAsync(session, idea, live, cancellationToken);
        await WriteSpikesAsync(session, live, cancellationToken);
        AnnounceOutcome(idea, live);
        return ExitCodes.Ok;
    }

    /// <summary>
    /// The idea's own spikes, with their verdicts (task: a spike is a run, not a walk) — read off
    /// each fanned-out task's own live projection, joined the identical way
    /// <see cref="WriteFanOutAsync"/> joins the whole fan-out, so this can never disagree with
    /// <c>h9k task show</c> about what a spike's verdict currently is. Silent when the idea cut no
    /// spikes at all, since most ideas cut none.
    /// </summary>
    private static async Task WriteSpikesAsync(
        IQuerySession session, IdeaAggregate live, CancellationToken cancellationToken)
    {
        List<TaskDetails> spikes = [];
        foreach (Guid taskId in live.CutTaskIds)
        {
            if (await session.LoadAsync<TaskDetails>(taskId, cancellationToken) is { SpikeKind: var kind } details
                && kind != SpikeKind.Unknown)
            {
                spikes.Add(details);
            }
        }

        if (spikes.Count == 0)
        {
            return;
        }

        AnsiConsole.MarkupLine("\n[bold]Spikes[/]");
        foreach (TaskDetails spike in spikes.OrderBy(spike => spike.AddedAt))
        {
            string shortId = TaskListCommand.ShortId(spike.Id);
            string verdictMarkup = spike.SpikeVerdict.Value switch
            {
                "Met" => "[green]met[/]",
                "NotMet" => "[red]not-met[/]",
                "BudgetExhausted" => "[yellow]budget-exhausted[/]",
                _ => "[dim]not concluded yet[/]",
            };
            AnsiConsole.MarkupLine(
                $"  • {shortId} [dim]({spike.SpikeKind.Value.EscapeMarkup()})[/] "
                + $"{ExternalText.OneLineMarkup(spike.Objective)} — {verdictMarkup}");
        }
    }

    /// <summary>
    /// Every task this idea has fanned out into, off the fan-out ids the fresh aggregate carries
    /// (see <see cref="ExecuteAsync"/>'s own reasoning) rather than <see cref="IdeaDetails.CutTaskIds"/>.
    /// Once the id list is in hand, each task's live state is joined through the board's own status
    /// composition — the same seam <c>h9k epic show</c> joins on <c>EpicId</c> — so it can never
    /// disagree with <c>h9k status</c> or <c>h9k task show</c> about what a task currently is.
    /// </summary>
    private static async Task WriteFanOutAsync(
        IQuerySession session, IdeaDetails idea, IdeaAggregate live, CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> cutTaskIds = live.CutTaskIds;
        if (cutTaskIds.Count == 0)
        {
            AnsiConsole.MarkupLine(
                "\n[bold]Tasks[/] [dim]none cut yet. Any time discovery gives it intent:[/] "
                + $"h9k task add --from-idea {TaskListCommand.ShortId(idea.Id)} --objective \"…\"");
            return;
        }

        IReadOnlyList<TaskStatusRow> rows = await TaskStatusComposer.ComposeAllAsync(
            session, DateTimeOffset.UtcNow, cancellationToken);
        List<TaskStatusRow> fannedOut = [.. rows.Where(row => cutTaskIds.Contains(row.TaskId))];
        if (fannedOut.Count == 0)
        {
            // A cut this idea's own stream recorded named a task the board no longer carries — a
            // purged project destroys every task it owns regardless of what recorded it (AGENTS.md,
            // never guess at unobserved facts): said plainly rather than showing an empty section
            // that would read as no fan-out ever happened.
            AnsiConsole.MarkupLine(
                $"\n[bold]Tasks[/] [dim]{cutTaskIds.Count} cut on record, but none are on the "
                + "board any more — the project that held them was likely removed.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"\n[bold]Tasks[/] {TaskRollup.From(fannedOut).Summary()}");
        AnsiConsole.Write(ProjectShowCommand.TaskTable(
            [.. fannedOut.OrderByDescending(row => row.AddedAt)], AnsiConsole.Profile.Width, DateTimeOffset.UtcNow));
        if (fannedOut.Count < cutTaskIds.Count)
        {
            // Same reasoning as the all-missing case above, but for a fan-out sent to more than
            // one project where only some of them were purged: the visible rollup and table cover
            // only what is still on the board, so the gap is said out loud rather than left to
            // read as the whole fan-out (AGENTS.md, never guess at unobserved facts).
            AnsiConsole.MarkupLine(
                $"[dim]{cutTaskIds.Count - fannedOut.Count} more cut on record but no longer on "
                + "the board — the project(s) that held them were likely removed.[/]");
        }
    }

    /// <summary>
    /// The workspace is a plain directory on disk, so what it holds is counted when someone
    /// looks. An empty one is not a problem to report — it is an invitation.
    /// </summary>
    private static string WorkspaceMarkup(IdeaDetails idea)
    {
        // A workspace home replicated from a node on a different operating system is a fact about
        // that machine, not a directory on this one — resolving it here (IdeaPaths.ResolveDirectory
        // stats this host's own disk) would read or build a path from the wrong OS's syntax. Print
        // exactly what was recorded instead.
        if (idea.WorkspaceHome is { HasValue: true, IsNativeForm: false } foreign)
        {
            return $"{foreign.Value.EscapeMarkup()} [dim](recorded on another node's own operating system — not a directory here)[/]";
        }

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

    /// <summary>
    /// What the idea became, or the one act it is waiting for. Reads the reason and the date off
    /// the fresh aggregate, not <paramref name="idea"/> (see <see cref="ExecuteAsync"/>'s own
    /// reasoning).
    /// </summary>
    private static void AnnounceOutcome(IdeaDetails idea, IdeaAggregate live)
    {
        string shortId = TaskListCommand.ShortId(idea.Id);
        if (live.State == IdeaState.Concluded)
        {
            AnsiConsole.MarkupLine(
                $"\n[green]Concluded[/] [dim]{live.ConcludedAt?.ToLocalTime():g}:[/] "
                + (live.ConcludeReason.IsNotBlank() ? live.ConcludeReason.EscapeMarkup() : "[dim]no reason recorded[/]"));
            return;
        }

        if (live.State == IdeaState.Archived)
        {
            AnsiConsole.MarkupLine(
                $"\n[dim]Archived {live.ArchivedAt?.ToLocalTime():g}:[/] {live.ArchiveReason.EscapeMarkup()}");
            AnsiConsole.MarkupLine("[dim]Kept on the record — if the thought comes back, that is a signal.[/]");
            return;
        }

        AnsiConsole.MarkupLine(
            $"\n[dim]In discovery — what is this? Sharpen it:[/] h9k idea revise {shortId} \"…\" "
            + "[dim]· cut a task any time it has intent:[/] h9k task add --from-idea " + shortId
            + " --objective \"…\"" + (idea.ProjectId is null ? " --project <name>" : string.Empty)
            + "\n[dim]Done producing? Say so:[/] h9k idea conclude " + shortId + " --reason \"…\" "
            + "[dim]or[/] h9k idea archive " + shortId + " --reason \"…\"");
    }

    private static string StateMarkup(IdeaState state) => state.Value switch
    {
        "Captured" => "[blue]Captured[/] [dim](in discovery)[/]",
        "Concluded" => "[green]Concluded[/]",
        "Archived" => "[dim]Archived[/]",
        _ => state.Value.EscapeMarkup(),
    };
}

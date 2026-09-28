using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Trust;
using Hall9k.Domain.Infrastructure.Extensions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>Lists this node's own received messages — unread (received, not yet handled) by
/// default; <c>--all</c> includes ones already handled too (idea 202383dc, M1b); <c>--project</c>
/// narrows to one project's own copy (idea 202383dc, M2) — otherwise every eligible project's
/// received messages show together. The body column is a sixty-character taste of each note;
/// <c>h9k message show &lt;id&gt;</c> is where the whole of one is read.</summary>
public sealed class MessagesCommand : Hall9kAsyncCommand<MessagesCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--all")]
        [Description("Include already-handled messages alongside unread ones. Unread only by default.")]
        public bool All { get; init; }

        [CommandOption("--project <PROJECT>")]
        [Description(
            "Only this project's own received messages: its name, an unambiguous fragment of it, "
            + "or its id (h9k project list shows them all). Every project's messages show together "
            + "by default.")]
        public string? Project { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        Guid? projectId = null;
        if (settings.Project.IsNotBlank())
        {
            ProjectDetails project = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);
            projectId = project.Id;
        }

        // Idea 202383dc, item 5: MessageKind.MechanicalKindValues' own three kinds carry a JSON
        // payload for ClaimRequestWatchLoop alone, never user-visible prose — the same reason
        // every MessageKind.IsReplicationProtocol kind never reaches this list at all (none of
        // those is even stored as an ordinary MessageDetails; these three are, since
        // ClaimRequestWatchLoop reads them by querying MessageDetails directly rather than through
        // a second, bespoke cursor the way event replication does).
        IReadOnlyList<string> mechanicalKinds = MessageKind.MechanicalKindValues;
        IQueryable<MessageDetails> receivedQuery = session.Query<MessageDetails>()
            .Where(message => message.ReceivedAt != null && !mechanicalKinds.Contains(message.Kind));
        if (projectId is { } filterProjectId)
        {
            receivedQuery = receivedQuery.Where(message => message.ProjectId == filterProjectId);
        }

        IReadOnlyList<MessageDetails> shown = await (settings.All ? receivedQuery : receivedQuery.Where(message => message.HandledAt == null))
            .OrderBy(message => message.ReceivedAt)
            .ToListAsync(cancellationToken);

        if (shown.Count == 0)
        {
            AnsiConsole.MarkupLine(settings.All
                ? "[dim]No messages yet.[/]"
                : "[dim]No unread messages.[/] h9k messages --all shows every handled one too.");
            return ExitCodes.Ok;
        }

        Guid[] projectIds = [.. shown.Select(message => message.ProjectId).Where(id => id != Guid.Empty).Distinct()];
        IReadOnlyDictionary<Guid, ProjectMemberLabels> labelsByProject = projectIds.Length == 0
            ? new Dictionary<Guid, ProjectMemberLabels>()
            : (await session.LoadManyAsync<ProjectMemberLabels>(cancellationToken, projectIds))
                .ToDictionary(labels => labels.Id);
        // The identical single-owner-per-install lookup OrchestratorFeedReader.LabelLookupAsync
        // already relies on, so a node of this machine's own owner reads as a bare id here too
        // rather than naming "me" (independent pre-PR review, cycle 1, conformance lens, medium).
        OwnerDetails? owner = (await session.Query<OwnerDetails>().Take(1).ToListAsync(cancellationToken))
            .FirstOrDefault();

        Table table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Id");
        table.AddColumn("Sender");
        table.AddColumn("Project");
        table.AddColumn("At");
        table.AddColumn("About");
        table.AddColumn("Body");
        table.AddColumn("");

        foreach (MessageDetails message in shown)
        {
            string shortId = TaskListCommand.ShortId(message.Id);
            labelsByProject.TryGetValue(message.ProjectId, out ProjectMemberLabels? projectLabels);
            table.AddRow(
                shortId,
                SenderCell(message.FromNodeId, projectLabels, owner?.RootFingerprint),
                message.ProjectId == Guid.Empty ? "[dim]—[/]" : TaskListCommand.ShortId(message.ProjectId),
                message.SentAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? string.Empty,
                message.About is { Length: > 0 } about ? about.EscapeMarkup() : "[dim]—[/]",
                TaskListCommand.Truncate(message.Body ?? string.Empty, 60).EscapeMarkup(),
                message.HandledAt is not null
                    ? $"[dim]handled[/] · h9k message show {shortId}"
                    : $"h9k message show {shortId} · h9k message handle {shortId}");
        }

        AnsiConsole.Write(table);
        return ExitCodes.Ok;
    }

    /// <summary>
    /// The Sender column: the sending node's short id, with the owning member's own label appended
    /// in parentheses when this project's own projection knows one for a node that is not this
    /// machine's own owner's (task b7d8222e) — sanitized and bounded before it is escaped for
    /// markup (<see cref="ExternalText.OneLineMarkup"/>, <see cref="MemberLabelResolver.RenderLimit"/>),
    /// since a display name or a declared login is read from another member's own self-signed
    /// ledger file, not authored by this node, and can carry more than the square brackets
    /// Spectre's table cells would otherwise try to parse as its own markup (independent pre-PR
    /// review, cycle 1, both lenses, medium). Pure and database-free so it is a unit test rather
    /// than an integration one.
    /// </summary>
    internal static string SenderCell(Guid fromNodeId, ProjectMemberLabels? projectLabels, string? ownRootFingerprint = null)
    {
        string id = TaskListCommand.ShortId(fromNodeId);
        return MemberLabelResolver.LabelForNodeId(projectLabels, fromNodeId, ownRootFingerprint) is { } label
            ? $"{id} ({ExternalText.OneLineMarkup(RelayedText.Truncate(label, MemberLabelResolver.RenderLimit))})"
            : id;
    }
}

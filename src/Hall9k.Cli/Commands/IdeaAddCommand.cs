using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Capture: one command, one argument (Decisions Log #35). Everything else an idea might
/// eventually need — a project, a shape, criteria — is discovery's job, and demanding any of
/// it here would turn a thirty-second transaction into a commitment to think.
/// <para>
/// The one exception is a thought that already lives in a tracker. <c>--from-linear</c>,
/// <c>--from-jira</c>, and <c>--from-issue</c> read that item through the importer task adoption uses and
/// capture it as an idea in discovery, recording the link on the idea's stream in the same transaction.
/// A Linear issue in Triage is the case this exists for: it has not been accepted into planned work, so
/// it is not a task, but it is not lost either.
/// </para>
/// </summary>
public sealed class IdeaAddCommand : Hall9kAsyncCommand<IdeaAddCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[TEXT]")]
        [Description(
            "The thought, in whatever words you had it in. This is the whole of capture: quote it "
            + "and you are done. Sharpen it later with h9k idea revise as discovery tells you what "
            + "it actually is. Optional only with --from-linear, --from-jira, or --from-issue, where it "
            + "defaults to the item's title folded to one line with any closing keyword defused; when "
            + "you give it, it wins")]
        public string? Text { get; init; }

        [CommandOption("--project <PROJECT>")]
        [Description(
            "Optional, and only when you already know: the project's name, an unambiguous fragment "
            + "of it, or its id. An idea may precede its project — or become one — so leaving this "
            + "off records an honest absence rather than a guess. Set it later with h9k idea move")]
        public string? Project { get; init; }

        [CommandOption("--from-linear <IDENTIFIER-OR-URL>")]
        [Description(
            "Capture a Linear issue as an idea: the identifier (ENG-123) or the issue's URL, read through the "
            + "registered Linear connection (h9k connection add linear). Linear's triage state is accepted "
            + "here, which task adoption refuses, because an issue still in Triage is not planned work yet; "
            + "backlog, unstarted, and started are accepted too, and completed, canceled, and duplicate are "
            + "refused with the state named. The link is recorded on the idea as linear:ENG-123 with the title, "
            + "state, and time observed (never re-checked), and the issue's title, description, URL, and state "
            + "are written into the idea's workspace as tracker-item.md, quoted as source material rather "
            + "than instruction. The first task cut from the idea (h9k task add --from-idea) adopts the "
            + "issue afresh. Needs --project, because the project must have Linear as its tracker "
            + "(h9k project set --linear); cannot be combined with --from-jira or --from-issue")]
        public string? FromLinear { get; init; }

        [CommandOption("--from-jira <KEY-OR-URL>")]
        [Description(
            "Capture a Jira card as an idea: the key (PROJ-123) or the card's URL, read through the registered "
            + "Jira connection (h9k connection add jira). The link and the workspace file work as for "
            + "--from-linear. Only a card whose status category is open is captured; a done card is refused "
            + "with its status named. Needs --project, and is refused on a project whose tracker is Linear; "
            + "cannot be combined with --from-linear or --from-issue")]
        public string? FromJira { get; init; }

        [CommandOption("--from-issue <NUMBER-OR-URL>")]
        [Description(
            "Capture a GitHub issue as an idea: the number (42 or #42), the owner/repo#42 shorthand, or the "
            + "issue URL, read through the gh CLI from the project's repository. The link and the workspace "
            + "file work as for --from-linear. Only an open issue is captured; a closed one is refused with "
            + "its state named. Needs --project, and is refused on a project whose tracker is Linear; cannot "
            + "be combined with --from-linear or --from-jira")]
        public string? FromIssue { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        RefuseLinkInDispatchedSession(settings, Environment.GetEnvironmentVariable);
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        // The importer is built only when a tracker item is being captured: a plain capture is the
        // thirty-second transaction this command exists to stay, and reads no connection at all.
        WorkItemImporter? importer = ChooseLink(settings) is null
            ? null
            : await WorkItemConnections.ImporterAsync(
                session, cancellationToken, processRunner: new ProjectScopedGitHubRunner(store).Runner);
        return await RunAsync(
            session, settings, importer, new GitLedger(new ConsoleWorktreeLogger<GitLedger>()), cancellationToken);
    }

    /// <summary>
    /// A dispatched run's own session may capture a raw idea (the lightest act this platform has, which is
    /// why <c>idea add</c> is allowed there), but a linked capture is not that: it reads a tracker with the
    /// owner's credentials and records a link that decides what a later cut adopts. The classification of
    /// commands sees only a command's type, not its options, so this is the option-level half of it.
    /// </summary>
    internal static void RefuseLinkInDispatchedSession(Settings settings, Func<string, string?> environmentVariable)
    {
        if (ChooseLink(settings) is { } link
            && environmentVariable(DispatchedRunEnvironment.RunIdVariable) is { Length: > 0 } runId)
        {
            throw new DomainBusinessRuleException(
                $"h9k idea add {link.Option} is refused: this process is run {runId}, a dispatched session, and a "
                + "linked capture reads a tracker with the owner's credentials and decides what a later cut adopts. "
                + "The orchestrator or a person owns this call; capture the thought without the link "
                + "(h9k idea add \"…\") or state the need in your closing summary.");
        }
    }

    /// <summary>
    /// Testable core, on the <see cref="IdeaPromoteCommand"/> precedent: the importer and the ledger are the
    /// seams, so the capture and its refusals are pinned against recorded responses and a fake ledger with
    /// no tracker call. <paramref name="importer"/> is read only when a --from option is given.
    /// </summary>
    internal static async Task<int> RunAsync(
        IDocumentSession session, Settings settings, WorkItemImporter? importer, ILedger ledger,
        CancellationToken cancellationToken)
    {
        TaskAddCommand.AdoptionSource? link = ChooseLink(settings);
        if (link is null && settings.Text.IsBlank())
        {
            throw new DomainValidationException(
                "An idea is the thought itself, so there has to be one: h9k idea add \"<what you were thinking>\". "
                + "Nothing else is required. A tracker item can stand in for the words: h9k idea add "
                + "--from-linear ENG-123 --project <name> (or --from-jira, --from-issue).");
        }

        // A linked idea is the one capture that needs a project: the link has to pass the project's
        // tracker rule, and which tracker that is lives on the project. Said before anything is read, so
        // nobody waits on a tracker to hear it.
        if (link is not null && settings.Project.IsBlank())
        {
            throw new DomainValidationException(
                $"{link.Option} records a link to {link.Article} {link.Noun} on the idea, and the link has to pass "
                + "the tracker rule of the project it belongs to, so the capture needs one: pass --project <name>. "
                + "An idea with no tracker link needs none.");
        }

        ProjectDetails? project = settings.Project.IsNotBlank()
            ? await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken)
            : null;
        if (project?.PurgeAt is { } purgeDeadline)
        {
            // Same reasoning as h9k task add's own refusal: the sweep re-queries ownership at
            // fire time, so an idea assigned to a project after scheduling is destroyed along
            // with everything else rather than orphaned — refused here so new work does not
            // accrue on a project already scheduled for destruction.
            throw new DomainValidationException(
                $"Project '{project.Name}' is scheduled for permanent deletion at "
                + $"{purgeDeadline.ToLocalTime():g}, so it cannot take a new idea. Cancel the purge "
                + $"first: h9k project cancel-purge {project.Name}");
        }

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        ImportedWorkItem? item = null;
        if (link is not null && project is not null)
        {
            item = await ReadLinkedItemAsync(
                session, settings, link, project, importer, ledger, context, cancellationToken);
        }

        // Whether the workspace starts life under the project's home is decided right here,
        // once, from what is actually on disk at this instant — never re-derived later, so a
        // home gained after capture cannot silently redirect the read path away from a
        // workspace a human may already have dropped files into (IdeaPaths).
        ProjectHome workspaceHome = project is not null
            && project.HomeDirectory.HasValue
            && Directory.Exists(project.HomeDirectory.Value)
                ? project.HomeDirectory
                : ProjectHome.None;

        DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
        Guid ideaId = DomainId.New();
        string text = settings.Text.IsNotBlank()
            ? settings.Text
            : TitleAsText(item, link);
        IdeaCaptured captured = IdeaDecider.Capture(
            ideaId, context.OwnerId, text, project?.Id, capturedAt, workspaceHome);

        // The link rides in the capture's own transaction: an idea recorded as captured from an item but
        // saved without the link, or the reverse, would leave a guard and a provenance that disagree.
        IdeaWorkItemLinked? linked = item is null
            ? null
            : IdeaDecider.LinkWorkItem(
                ideaId, item.Reference, item.Title, item.Status.ToString(), item.ObservedAt, capturedAt, context.OwnerId);
        object[] events = linked is null ? [captured] : [captured, linked];
        session.Events.StartStream<IdeaAggregate>(ideaId, events);
        await session.SaveChangesAsync(cancellationToken);

        // The workspace is made real the moment the idea exists: a directory you have to create
        // before you can use it is a directory nobody drops a file into. No doorbell — nothing
        // dispatches from an idea, by design.
        string ideaDirectory = IdeaPaths.ResolveDirectory(
            workspaceHome, ProjectHomePaths.EntryDirectoryName(ideaId, captured.Text), ideaId);
        string workspace = IdeaPaths.EnsureWorkspace(ideaDirectory);

        // A home-resident idea's directory has to carry its identity marker from the moment this
        // command creates it, not just from the first render sweep: a revise that changes the
        // slug before that first sweep ever runs would otherwise leave the sweep unable to
        // recognise this directory as the idea's own, and it would build a fresh, empty one at the
        // new name instead of moving this one (HomeEntryLookup.FindExisting requires the marker).
        if (workspaceHome.HasValue)
        {
            HomeEntryLookup.EnsureIdentityMarker(ideaDirectory, ideaId);
        }

        // The item as source material for discovery: the title, description, URL, and observed state
        // under the same non-instruction framing an adopted task's context carries, so a session that
        // reads the workspace reads text the item's author wrote as quoted, never as direction.
        if (item is not null)
        {
            await File.WriteAllTextAsync(
                Path.Combine(workspace, IdeaPaths.LinkedItemFileName), WorkItemContext.Compose(item), cancellationToken);
        }

        string shortId = TaskListCommand.ShortId(ideaId);
        AnsiConsole.MarkupLine(
            $"[blue]Idea captured[/] {TaskListCommand.Truncate(captured.Text, 72).EscapeMarkup()} [dim]({shortId})[/]");
        AnsiConsole.MarkupLine(project is null
            ? "[dim]  project:[/] none yet [dim]— set one when you know it:[/] "
              + $"h9k idea move {shortId} <project>"
            : $"[dim]  project:[/] {project.Name.EscapeMarkup()}");
        if (item is not null)
        {
            AnsiConsole.MarkupLine(
                $"[dim]  linked:[/] {item.Reference.ToString().EscapeMarkup()} "
                + $"[dim]({ExternalText.OneLineMarkup(item.Status.ToString())} when read at {item.ObservedStamp}; "
                + "Hall9k took one reading and does not watch the item afterwards)[/]");
        }

        AnsiConsole.MarkupLine($"[dim]  workspace:[/] {workspace.EscapeMarkup()}");
        if (item is not null)
        {
            AnsiConsole.MarkupLine(
                $"[dim]  {IdeaPaths.LinkedItemFileName}:[/] the item's title, description, and state, quoted as source material");
        }

        AnsiConsole.MarkupLine(
            "[dim]Discovery is what happens next: research it, gather files into the workspace, "
            + "prototype. When it has intent, it is a task — cut as many as discovery produces:[/]");
        AnsiConsole.MarkupLine($"  h9k task add --from-idea {shortId} --objective \"…\""
            + (project is null ? " --project <name>" : string.Empty));
        if (item is not null)
        {
            AnsiConsole.MarkupLine(
                "[dim]The first cut adopts the item afresh, and with --unlinked cuts a task that carries no link.[/]");
        }

        AnsiConsole.MarkupLine(
            "[dim]Done producing? Say so:[/] h9k idea conclude " + shortId + " --reason \"…\" "
            + "[dim]or[/] h9k idea archive " + shortId + " --reason \"…\"");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// The item this capture links, read through the importer task adoption uses and held to every rule a
    /// task adopting it would meet: the project's one tracker, the states adoption accepts plus Linear's
    /// triage, and the guards for an item already spoken for (an idea in discovery here, a task here, or a
    /// record in the project's ledger). Everything is judged before anything is written.
    /// </summary>
    private static async Task<ImportedWorkItem> ReadLinkedItemAsync(
        IDocumentSession session, Settings settings, TaskAddCommand.AdoptionSource link, ProjectDetails project,
        WorkItemImporter? importer, ILedger ledger, BootstrapContext context, CancellationToken cancellationToken)
    {
        ProjectTracker.RefuseLinkAgainstTracker(
            link.Provider, project.LinearTeam, project.Name, $"h9k idea add {link.Option}");

        WorkItemImporter reader = importer
            ?? throw new InvalidOperationException($"{link.Option} reads a tracker, so the capture needs an importer.");

        // A state the read refuses is given the remedy that belongs to a capture: the tracker has to be
        // changed, or the thought captured without the link.
        ImportedWorkItem item = await reader.ImportForIdeaAsync(
            new WorkItemImportRequest(link.Provider, link.Reference, project.RepositoryPath),
            $"Reopen it and capture again, or capture the thought without the link: h9k idea add \"<the thought>\" --project {project.Name}",
            cancellationToken);

        await IdeaWorkItemGuard.RefuseSecondIdeaAsync(session, item, cancellationToken);
        string? ownerRootFingerprint = await OwnerRootFingerprintResolver.ResolveAsync(
            session, context.OwnerId, cancellationToken);
        await TaskAddCommand.RefuseHeldItemAsync(
            session, ledger, project, epicId: null, context.NodeId, ownerRootFingerprint, item,
            checkLedgerRecord: true, cancellationToken);

        if (settings.Text.IsBlank() && TaskAddCommand.ObjectiveSeed(item.Title).IsBlank())
        {
            throw new DomainValidationException(
                $"{ExternalText.OneLine(item.Reference.ToString())} has no title Hall9k can seed an idea from "
                + $"(the source reported '{ExternalText.OneLine(item.Title)}'). Say what it is yourself: "
                + $"h9k idea add \"<the thought>\" {link.Option} <ref> --project <name>");
        }

        return item;
    }

    /// <summary>The item's title as the idea's words: folded to one line, closing keywords defused (the task objective seed's own rule).</summary>
    private static string TitleAsText(ImportedWorkItem? item, TaskAddCommand.AdoptionSource? link) =>
        item is null
            ? throw new InvalidOperationException($"{link?.Option} read no item to take a title from.")
            : TaskAddCommand.ObjectiveSeed(item.Title);

    /// <summary>
    /// The one tracker option given, or null when there is none. Two together are refused rather than
    /// ranked: an idea carries one link, and picking a winner would silently drop the other.
    /// </summary>
    internal static TaskAddCommand.AdoptionSource? ChooseLink(Settings settings)
    {
        TaskAddCommand.AdoptionSource[] named =
        [
            .. new[]
            {
                new TaskAddCommand.AdoptionSource(WorkItemProvider.Linear, settings.FromLinear ?? string.Empty, "--from-linear", "issue"),
                new TaskAddCommand.AdoptionSource(WorkItemProvider.Jira, settings.FromJira ?? string.Empty, "--from-jira", "card"),
                new TaskAddCommand.AdoptionSource(WorkItemProvider.GitHub, settings.FromIssue ?? string.Empty, "--from-issue", "issue"),
            }.Where(source => source.Reference.IsNotBlank()),
        ];

        return named switch
        {
            [] => null,
            [var only] => only,
            _ => throw new DomainValidationException(
                $"{string.Join(" and ", named.Select(source => source.Option))} each name a different item, and an "
                + "idea carries one link; pass one. Capture the other as its own idea if both are real."),
        };
    }
}

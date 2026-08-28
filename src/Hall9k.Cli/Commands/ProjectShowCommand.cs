using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;

namespace Hall9k.Cli.Commands;

public sealed class ProjectShowCommand : Hall9kAsyncCommand<ProjectShowCommand.Settings>
{
    /// <summary>How many of the project's tasks the pane lists before pointing at h9k task list.</summary>
    private const int RecentTasks = 5;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PROJECT>")]
        [Description("Project name, an unambiguous fragment of it, or the full id (h9k project list shows them all)")]
        public string Project { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        ProjectDetails project = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);
        ConnectionDetails? connection = await session.LoadAsync<ConnectionDetails>(project.ConnectionId, cancellationToken);
        OwnerDetails? owner = await session.LoadAsync<OwnerDetails>(project.OwnerId, cancellationToken);

        AnsiConsole.Write(Registration(project, connection, owner));
        AnsiConsole.MarkupLine("\n[bold]Settings[/] [dim](change them with h9k project set "
            + $"{project.Name.EscapeMarkup()} …)[/]");
        AnsiConsole.Write(SettingsPane(project));

        IReadOnlyList<TaskStatusRow> rows = await TaskStatusComposer.ComposeAllAsync(
            session, DateTimeOffset.UtcNow, cancellationToken);
        WriteTasks(project, [.. rows.Where(row => row.ProjectId == project.Id)]);
        return ExitCodes.Ok;
    }

    private static Table Registration(ProjectDetails project, ConnectionDetails? connection, OwnerDetails? owner)
    {
        Table table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumns("k", "v");
        table.AddRow("[bold]Project[/]", $"[bold]{project.Name.EscapeMarkup()}[/]");
        table.AddRow("Id", $"[dim]{project.Id}[/]");
        // The home leads, because it is the answer to "where do I go to work on this" — the
        // repository path is one thing inside it. A project with none says so and names the
        // command that ends that state, rather than leaving a blank row to interpret.
        table.AddRow("Home", project.HomeDirectory.HasValue
            ? project.HomeDirectory.Value.EscapeMarkup()
              + (Directory.Exists(project.HomeDirectory.Value)
                  ? string.Empty
                  : $" [yellow](not created here — h9k project init {project.Name.EscapeMarkup()})[/]")
            : $"[dim]none recorded — create one: h9k project init {project.Name.EscapeMarkup()}[/]");
        table.AddRow("Repository", project.RepositoryPath.EscapeMarkup());
        table.AddRow("Remote", project.RepositoryUrl is { } url
            ? url.ToString().EscapeMarkup()
            : "[dim]none recorded[/]");
        table.AddRow("Base branch", project.BaseBranch.EscapeMarkup());

        // A project binds to a connection, never to "the machine's GitHub" (PLAN.md §10),
        // so name the binding — and say plainly when the connection is not readable here
        // rather than dressing the id up as an account.
        table.AddRow("Connection", connection is null
            ? $"[dim]id {project.ConnectionId} (no connection document found)[/]"
            : $"{connection.Provider.Value.EscapeMarkup()} · {connection.ExternalAccountId.EscapeMarkup()} "
              + $"[dim]({connection.CredentialReference.EscapeMarkup()})[/]");
        table.AddRow("Owner", owner is null
            ? $"[dim]id {project.OwnerId} (no owner document found)[/]"
            : owner.Name.EscapeMarkup());
        table.AddRow("Registered", $"[dim]{project.RegisteredAt.ToLocalTime():g}[/]");
        return table;
    }

    private static Table SettingsPane(ProjectDetails project)
    {
        Table table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumns("k", "v");
        table.AddRow("Skip permissions", project.SkipPermissions
            ? "[yellow]yes[/] [dim]— agents run with --dangerously-skip-permissions (log #9)[/]"
            : "[dim]no — agents stop for every permission prompt, which a detached run cannot answer (log #9)[/]");
        table.AddRow("Max parallel agents", project.MaxParallelAgents.ToString());
        table.AddRow("Commit style", project.CommitStyle == CommitStyle.Unknown
            ? "[dim]platform default — DaemonOptions.DefaultCommitStyle, narrative unless configured otherwise (log #26)[/]"
            : project.CommitStyle.Value.EscapeMarkup());
        table.AddRow("Re-request review", ReviewRerequestOption.Describe(
            project.ReviewRerequest,
            "after a fix follow-up pushes, closeout asks this pull request's reviewers for another pass, "
            + "capped by DaemonOptions.MaxReviewRerequestsAfterFixes (log #62)",
            "the owner preference decides (h9k owner show), else the node default "
            + "(DaemonOptions.DefaultReviewRerequest, off)"));
        table.AddRow("Verify gates", project.VerifyCommands.Count == 0
            ? $"[dim]none — add one: h9k project set {project.Name.EscapeMarkup()} --verify \"test=dotnet test\"[/]"
            : string.Join("\n", project.VerifyCommands.Select(gate =>
                $"{gate.Name.EscapeMarkup()} [dim]→[/] {gate.Command.EscapeMarkup()}")));
        table.AddRow("Jira board", project.JiraProjectKey.HasValue
            ? $"{project.JiraProjectKey.Value.EscapeMarkup()} [dim]— new cards are filed here; a reported "
              + "card key is checked against it[/]"
            : $"[dim]none bound — bind one: h9k project set {project.Name.EscapeMarkup()} --jira PROJ[/]");
        table.AddRow("Backlog policy", BacklogPolicyRow(project));
        table.AddRow("Context links", project.ContextLinks.Count == 0
            ? $"[dim]none — add one: h9k project set {project.Name.EscapeMarkup()} --link \"jira=https://…\"[/]"
            : string.Join("\n", project.ContextLinks.Select(link =>
                $"{link.Name.EscapeMarkup()} [dim]→[/] {link.Url.ToString().EscapeMarkup()}")));
        table.AddRow("Settings changed", project.SettingsChangedAt is { } changedAt
            ? $"[dim]{changedAt.ToLocalTime():g}[/]"
            : "[dim]never — still the registration defaults[/]");
        return table;
    }

    /// <summary>
    /// What the policy means today, said in the same words the option's own help does — a
    /// project bound to a policy nobody has looked at in months should not require re-reading
    /// h9k project set --help to remember what publishing a task will do.
    /// </summary>
    private static string BacklogPolicyRow(ProjectDetails project)
    {
        string routing = project.BacklogRoutingGuidance.IsNotBlank()
            ? $" [dim](routing: {project.BacklogRoutingGuidance.EscapeMarkup()})[/]"
            : string.Empty;

        if (project.BacklogPolicy == BacklogPolicy.GitHubIssues)
        {
            return "github-issues [dim]— publishing authors a GitHub issue and links it, verified "
                + $"read-back included[/]{routing}";
        }

        if (project.BacklogPolicy == BacklogPolicy.Jira)
        {
            return "jira [dim]— publishing dispatches the same agent-mediated push h9k task "
                + $"push-to-jira does[/]{routing}";
        }

        return $"[dim]none — publishing tracks nothing externally; set one: h9k project set "
            + $"{project.Name.EscapeMarkup()} --backlog github-issues|jira[/]{routing}";
    }

    private static void WriteTasks(ProjectDetails project, IReadOnlyList<TaskStatusRow> rows)
    {
        string name = project.Name.EscapeMarkup();
        if (rows.Count == 0)
        {
            AnsiConsole.MarkupLine(
                $"\n[bold]Tasks[/] [dim]none yet. Queue one:[/] h9k task add --project {name} "
                + "--objective \"…\" --criteria \"…\"");
            return;
        }

        AnsiConsole.MarkupLine($"\n[bold]Tasks[/] {TaskRollup.From(rows).Summary()}");

        // Newest first: h9k project show is an orientation pane, and the freshest tasks
        // are what the person asking "what is this project up to" wants first.
        List<TaskStatusRow> newest = [.. rows.OrderByDescending(row => row.AddedAt).Take(RecentTasks)];

        AnsiConsole.Write(TaskTable(newest, AnsiConsole.Profile.Width, DateTimeOffset.UtcNow));

        int held = rows.Count - newest.Count;
        AnsiConsole.MarkupLine(held > 0
            ? $"[dim]Showing the {newest.Count} newest of {rows.Count}; {held} held back — all of them:[/] "
              + $"h9k task list --project {name} --all"
            : $"[dim]All {rows.Count} of this project's tasks. Filter them with:[/] "
              + $"h9k task list --project {name} --state <state>");
    }

    /// <summary>
    /// The pane's task list: the same columns h9k task list shows for one project, with the
    /// objective truncated to exactly the width the fixed columns leave it so a row never wraps,
    /// and each row's summary line underneath it. Built apart from the query so the layout can be
    /// rendered and measured.
    /// </summary>
    internal static IRenderable TaskTable(IReadOnlyList<TaskStatusRow> rows, int consoleWidth, DateTimeOffset now)
    {
        string[] ages = [.. rows.Select(row => $"[dim]{row.AgeMarkup(now)}[/]")];
        return TaskRowLayout.Render(
            rows,
            [
                new TaskColumn("Id", [.. rows.Select(row => row.IdMarkup)]),
                new TaskColumn("Status", [.. rows.Select(row => row.StateMarkup)]),
                new TaskColumn("Type", [.. rows.Select(row => row.TypeMarkup)]),
            ],
            [
                new TaskColumn("Attention", [.. rows.Select(row => row.AttentionMarkup)]),
                new TaskColumn("Added", ages),
                new TaskColumn("PR", [.. rows.Select(row => row.PullRequestMarkup)]),
            ],
            [.. rows.Select(row => row.SummaryMarkup)],
            consoleWidth,
            headers: true);
    }
}

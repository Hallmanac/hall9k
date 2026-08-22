using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Every external account this install can reach, with where its credential lives and which
/// projects bind to it (PLAN.md §10). It reads only what is recorded and calls nothing: a list
/// that tested each connection would take seconds and, worse, would make "h9k connection list"
/// a thing that can fail because somebody's VPN is down.
/// </summary>
public sealed class ConnectionListCommand : Hall9kAsyncCommand<ConnectionListCommand.Settings>
{
    public sealed class Settings : CommandSettings;

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        IReadOnlyList<ConnectionDetails> connections = await session.Query<ConnectionDetails>()
            .OrderBy(connection => connection.RegisteredAt)
            .ToListAsync(cancellationToken);
        if (connections.Count == 0)
        {
            AnsiConsole.MarkupLine(
                "[dim]No connections registered. One is created for GitHub the first time h9k writes "
                + "anything; add Jira with:[/] h9k connection add jira --site https://your-org.atlassian.net "
                + "--email you@example.com");
            return ExitCodes.Ok;
        }

        IReadOnlyList<ProjectDetails> projects = await session.Query<ProjectDetails>()
            .ToListAsync(cancellationToken);

        Table table = new Table().Border(TableBorder.Rounded);
        table.AddColumn(new TableColumn("Provider").NoWrap());
        table.AddColumn("Account");
        table.AddColumn("Site");
        table.AddColumn("Credential");
        table.AddColumn(new TableColumn("Projects").NoWrap());
        foreach (ConnectionDetails connection in connections)
        {
            int bound = BoundProjects(connection, projects);
            table.AddRow(
                connection.Provider.Value.EscapeMarkup(),
                ExternalText.OneLineMarkup(connection.ExternalAccountId),
                connection.SiteUrl is { } site
                    ? site.ToString().EscapeMarkup()
                    : "[dim]—[/]",
                $"[dim]{connection.CredentialReference.EscapeMarkup()}[/]",
                bound == 0 ? "[dim]none[/]" : bound.ToString());
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            "[dim]A credential column names where the secret lives, never the secret (PLAN.md §10). "
            + "Rotating a Jira token is h9k connection add jira again.[/]");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// How many projects route work through this connection, which is a different field per
    /// provider because the two are bound by different acts. A project's GitHub connection is the
    /// one it was registered against, recorded on the project as its connection id; its Jira
    /// binding is the board set with <c>h9k project set --jira</c>, and nothing writes a Jira
    /// connection into the connection id at all — an install supports one Jira account
    /// (WorkItemConnections.FindJiraConnectionAsync refuses to choose between two), so a project
    /// with a board bound is a project routing its cards through this connection.
    /// <para>
    /// Origin incident (2026-08-22): the third cycle of this branch's pre-PR review found the
    /// count reading connection ids alone, so a Jira connection with three boards bound to it
    /// rendered "none" under a column that promises how many projects bind to it — which reads as
    /// an account nobody uses and is the one question this column exists to answer.
    /// </para>
    /// </summary>
    private static int BoundProjects(ConnectionDetails connection, IReadOnlyList<ProjectDetails> projects) =>
        connection.Provider == WorkItemProvider.Jira
            ? projects.Count(project => project.JiraProjectKey.HasValue)
            : projects.Count(project => project.ConnectionId == connection.Id);
}

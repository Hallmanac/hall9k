using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;

namespace Hall9k.Cli.Diagnostics;

/// <summary>
/// <c>h9k doctor</c>'s own probe for Jira writes (Brian's design, 2026-08-28; the probe's own
/// transport moved off the Atlassian CLI (twg) onto hall9k's own REST client, Decisions Log #114):
/// only worth running once a project actually tracks its backlog in Jira, since an install with no
/// such project has nothing to write there. Unlike the twg era, there is no separate machine-wide
/// login to distinguish from a missing binary — the registered connection's API token is the whole
/// story, so this narrows to "is a connection registered, and does Jira accept its credential."
/// </summary>
internal static class JiraDoctor
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        List<ProjectDetails> jiraProjects = [.. (await session.Query<ProjectDetails>().ToListAsync(cancellationToken))
            .Where(project => project.BacklogPolicy == BacklogPolicy.Jira)];

        if (jiraProjects.Count == 0)
        {
            return;
        }

        string names = string.Join(", ", jiraProjects.Select(project => project.Name));
        AnsiConsole.MarkupLine("[bold]Jira writes[/]");

        ConnectionDetails? connection;
        try
        {
            // Caught rather than left to propagate: this is one check among several DoctorCommand
            // runs, and a thrown DomainConflictException (two Jira connections registered, and
            // nothing says which) would abort the whole doctor run instead of just reporting this
            // one thing as unconfirmable.
            connection = await WorkItemConnections.FindJiraConnectionAsync(session, cancellationToken);
        }
        catch (DomainConflictException exception)
        {
            AnsiConsole.MarkupLine($"[yellow]  Could not confirm the Jira connection is usable[/] — {exception.Message.EscapeMarkup()}");
            return;
        }

        if (connection?.SiteUrl is null)
        {
            AnsiConsole.MarkupLine(
                $"[red]  No Jira connection is registered[/], and {names.EscapeMarkup()} track their "
                + "backlog in Jira — every write to it will be refused until one is. Register one: "
                + "[bold]h9k connection add jira --site https://your-org.atlassian.net --email "
                + "you@example.com[/]");
            return;
        }

        JiraAuthProbeResult probe;
        try
        {
            // Caught the same way the connection lookup above is: WorkItemConnections.Account
            // throws a DomainValidationException for a connection with no site recorded or a
            // credential reference that no longer parses, and ProbeAuthenticationAsync's own
            // executor call can hit the identical shape resolving the credential itself (a
            // DomainValidationException, an unset environment variable; a DomainNotFoundException,
            // a deleted credential file) — neither is a rejected credential Jira itself answered
            // about, so neither should abort the whole doctor run rather than report this one
            // check as unconfirmable.
            probe = await new JiraWriteExecutor(WorkItemConnections.Account(connection))
                .ProbeAuthenticationAsync(cancellationToken);
        }
        catch (DomainException exception)
        {
            AnsiConsole.MarkupLine($"[yellow]  Could not confirm the Jira connection is usable[/] — {exception.Message.EscapeMarkup()}");
            return;
        }

        switch (probe)
        {
            case JiraAuthProbeResult.Authenticated:
                AnsiConsole.MarkupLine(
                    $"[green]  The registered Jira connection is authenticated.[/] "
                    + $"[dim]({names.EscapeMarkup()} track their backlog in Jira.)[/]");
                break;

            case JiraAuthProbeResult.AuthFailure:
                AnsiConsole.MarkupLine(
                    $"[yellow]  Jira rejected the registered credentials[/] — {names.EscapeMarkup()} track "
                    + "their backlog in Jira, and writes to it will be recorded pending until this is "
                    + "fixed. The API token may have been revoked or rotated: create a fresh one at "
                    + "https://id.atlassian.com/manage-profile/security/api-tokens and register it again "
                    + "with [bold]h9k connection add jira[/]; any pending writes retry automatically "
                    + "once that succeeds.");
                break;

            default:
                AnsiConsole.MarkupLine(
                    "[yellow]  Could not confirm the Jira connection is authenticated[/] — a probe search "
                    + "answered with something other than success or a recognisable auth refusal. Run "
                    + "h9k connection list to check the registered connection by hand.");
                break;
        }
    }
}

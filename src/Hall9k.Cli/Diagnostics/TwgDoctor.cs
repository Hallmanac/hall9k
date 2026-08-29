using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Marten;
using Spectre.Console;

namespace Hall9k.Cli.Diagnostics;

/// <summary>
/// <c>h9k doctor</c>'s own probe for twg (Brian's design, 2026-08-28): only worth running once a
/// project actually tracks its backlog in Jira, since an install with no such project has nothing
/// to write there. Distinguishes a missing binary from an expired login, because the fix is
/// different for each — installing twg is not the same problem as re-authenticating one already
/// installed — and teaches <c>twg login</c> as the remedy for the latter, the way every other
/// check this doctor runs teaches a fix rather than only naming a failure.
/// </summary>
internal static class TwgDoctor
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
        AnsiConsole.MarkupLine("[bold]twg (Jira writes)[/]");

        Uri? site = (await WorkItemConnections.FindJiraConnectionAsync(session, cancellationToken))?.SiteUrl;
        TwgAuthProbeResult probe = await new TwgJiraExecutor(site: site)
            .ProbeAuthenticationAsync(Environment.CurrentDirectory, cancellationToken);

        switch (probe)
        {
            case TwgAuthProbeResult.Authenticated:
                AnsiConsole.MarkupLine(
                    $"[green]  twg is installed and authenticated.[/] "
                    + $"[dim]({names.EscapeMarkup()} track their backlog in Jira.)[/]");
                break;

            case TwgAuthProbeResult.MissingBinary:
                AnsiConsole.MarkupLine(
                    $"[red]  twg is not installed[/], and {names.EscapeMarkup()} track their backlog in "
                    + "Jira — every write to it will fail until it is. Install the twg (Teamwork Graph) "
                    + "CLI, then run [bold]twg login[/].");
                break;

            case TwgAuthProbeResult.AuthExpired:
                AnsiConsole.MarkupLine(
                    $"[yellow]  twg is installed but not authenticated[/] (its login expires "
                    + $"periodically) — {names.EscapeMarkup()} track their backlog in Jira, and writes to "
                    + "it will be recorded pending until this is fixed. Run [bold]twg login[/] in your "
                    + "own terminal (a browser-based login twg cannot do unattended); any pending writes "
                    + "retry automatically once it succeeds.");
                break;

            default:
                AnsiConsole.MarkupLine(
                    "[yellow]  Could not confirm twg is authenticated[/] — a probe search answered with "
                    + "something other than success or a recognisable auth refusal. Run "
                    + "'twg jira workitem query --jql \"<something>\" --output json --output-summary stats' "
                    + "by hand to see what it says.");
                break;
        }
    }
}

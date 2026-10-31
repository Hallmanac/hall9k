using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Text;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;

namespace Hall9k.Cli.Diagnostics;

/// <summary>What the Linear probe came to, told apart the way a human has to act on it.</summary>
internal enum LinearProbeOutcome
{
    /// <summary>Linear accepted the registered key. It says nothing about what the key may do.</summary>
    Accepted,

    /// <summary>This install has no Linear connection at all.</summary>
    NoConnection,

    /// <summary>Linear refused the key.</summary>
    Rejected,

    /// <summary>The probe could not confirm either: an outage, a rate limit, two connections, a proxy, a key this machine cannot read.</summary>
    Unconfirmed,
}

/// <summary>A probe's outcome and the sentence that explains it, when there is something to explain.</summary>
internal sealed record LinearProbe(LinearProbeOutcome Outcome, string Detail);

/// <summary>
/// <c>h9k doctor</c>'s probe for the Linear connection, shaped like <see cref="JiraDoctor"/>: only worth
/// running once a project has a Linear team bound, since an install with no such project has nothing
/// to read from Linear. It tells a missing connection, a key Linear rejected, and a probe that could
/// not confirm either apart, and no failure here stops the rest of the doctor run.
/// <para>
/// A pass proves only that Linear accepts the key. A key limited by permission or by team passes the
/// viewer read and can still be refused on a later read or write, and nothing the API exposes lets a
/// probe prove otherwise.
/// </para>
/// </summary>
internal static class LinearDoctor
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        List<ProjectDetails> linearProjects = [.. (await session.Query<ProjectDetails>().ToListAsync(cancellationToken))
            .Where(project => ProjectTracker.IsLinear(project.LinearTeam))];

        if (linearProjects.Count == 0)
        {
            return;
        }

        string names = string.Join(", ", linearProjects.Select(project => project.Name));
        AnsiConsole.MarkupLine("[bold]Linear[/]");

        // Before the probe, and apart from it: this reads only the projects already loaded, so it
        // needs no network and is not lost to a failure of the connection check below.
        foreach (string warning in BranchTemplateWarnings(linearProjects))
        {
            AnsiConsole.MarkupLine(warning);
        }

        LinearProbe probe = await ProbeAsync(
            async () => await WorkItemConnections.TryLinearProviderAsync(session, cancellationToken), cancellationToken);
        switch (probe.Outcome)
        {
            case LinearProbeOutcome.Accepted:
                AnsiConsole.MarkupLine(
                    "[green]  Linear accepts the registered key.[/] "
                    + $"[dim]({names.EscapeMarkup()} track their work in Linear. A key limited by permission or "
                    + "by team can still be refused on a later read or write.)[/]");
                break;

            case LinearProbeOutcome.NoConnection:
                AnsiConsole.MarkupLine(
                    $"[red]  No Linear connection is registered[/], and {names.EscapeMarkup()} track their work in "
                    + "Linear, so every Linear read is refused until one is. Register one: "
                    + "[bold]h9k connection add linear[/]");
                break;

            case LinearProbeOutcome.Rejected:
                AnsiConsole.MarkupLine(
                    $"[yellow]  Linear rejected the registered key[/], and {names.EscapeMarkup()} track their work "
                    + $"in Linear. {probe.Detail.EscapeMarkup()} Create a fresh personal API key in Linear and "
                    + "register it again with [bold]h9k connection add linear[/].");
                break;

            default:
                AnsiConsole.MarkupLine(
                    "[yellow]  Could not confirm the Linear connection is usable[/], so it is neither known to "
                    + $"work nor known to be rejected. {probe.Detail.EscapeMarkup()} Run h9k connection list to "
                    + "check the registered connection by hand.");
                break;
        }
    }

    /// <summary>
    /// One warning for each Linear-tracked project whose branch template carries no <c>{key}</c>
    /// token. Hall9k writes no magic word into a pull request for the task's own issue, so Linear
    /// links the pull request through the identifier in the branch name, and the default template
    /// (<c>task/{shortid}-{slug}</c>) carries none. Unlike the warning <c>h9k project set</c> gives at
    /// bind time, this one sees a template changed afterwards, <c>--branch-template none</c> included.
    /// It reads the project's settings alone and makes no network call.
    /// </summary>
    internal static IReadOnlyList<string> BranchTemplateWarnings(IEnumerable<ProjectDetails> linearProjects) =>
        [.. linearProjects
            .Where(project => !project.BranchNameTemplate.UsesToken(BranchNameTemplate.KeyToken))
            .Select(project =>
                $"[yellow]  {project.Name.EscapeMarkup()}'s branch template '{project.BranchNameTemplate.Value.EscapeMarkup()}' "
                + "has no {key}[/], and Linear links a pull request to its issue through the identifier in the "
                + "branch name, because Hall9k writes no magic word for the task's own issue. Branches cut from "
                + "this template will not be linked. To cut branches Linear can follow: "
                + $"[bold]h9k project set {project.Name.EscapeMarkup()} --branch-template \"{{key}}-{{slug}}\"[/]")];

    /// <summary>
    /// The probe itself, apart from the printing and the store so it can be driven with a fixture requester. Never
    /// throws a domain refusal: every way the connection can fail to be probed is an outcome, so one
    /// bad connection record cannot abort the doctor run.
    /// </summary>
    internal static async Task<LinearProbe> ProbeAsync(
        Func<Task<LinearWorkItemProvider?>> openProvider, CancellationToken cancellationToken)
    {
        LinearWorkItemProvider? provider;
        try
        {
            provider = await openProvider();
        }
        catch (DomainException exception)
        {
            // Two connections left by overlapping runs, or one recorded with no workspace: a fact about
            // the record rather than about Linear, so it is reported and not mistaken for a rejection.
            return new LinearProbe(LinearProbeOutcome.Unconfirmed, RelayedText.OneLine(exception.Message));
        }

        if (provider is null)
        {
            return new LinearProbe(LinearProbeOutcome.NoConnection, string.Empty);
        }

        LinearViewerRead read = await provider.ReadViewerAsync(cancellationToken);
        return read switch
        {
            { Viewer: not null } => new LinearProbe(LinearProbeOutcome.Accepted, string.Empty),
            // The vault could not produce the key (an unset environment variable, a missing file), so
            // Linear was never asked: neither known to work nor known to be rejected.
            { KeyUnavailable: true } =>
                new LinearProbe(LinearProbeOutcome.Unconfirmed, RelayedText.OneLine(read.Failure?.Message ?? string.Empty)),
            { FailureKind: TrackerReadFailure.Credentials } =>
                new LinearProbe(LinearProbeOutcome.Rejected, RelayedText.OneLine(read.Failure?.Message ?? string.Empty)),
            _ => new LinearProbe(LinearProbeOutcome.Unconfirmed, RelayedText.OneLine(read.Failure?.Message ?? string.Empty)),
        };
    }
}

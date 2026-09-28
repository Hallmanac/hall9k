using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Verification;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// This node's own operator vets a project's current verify gate set (security review idea
/// 6be68ee2, process-injection finding 1, the local half): a gate command is a shell string this
/// node runs unsupervised at every verification, and this is the one door that trust ever comes
/// through — no interactive confirm anywhere else in this feature ever substitutes for a human
/// having read exactly this output and run exactly this command. Prints the full current gate list
/// and a line-by-line diff against whatever this node last accepted, then records the fingerprint
/// of exactly what it printed — refusing rather than recording a stale review if the project's own
/// gate set changed while this command was running.
/// </summary>
public sealed class ProjectAcceptGatesCommand : Hall9kAsyncCommand<ProjectAcceptGatesCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PROJECT>")]
        [Description(
            "Project whose current verify gate set to accept: its name, an unambiguous fragment of "
            + "it, or its full id.")]
        public string Project { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        ProjectDetails details = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        IReadOnlyList<VerifyCommand> printed = details.VerifyCommands;
        GateSetAcceptance.Decision decision = GateSetAcceptance.Decide(details.AcceptedVerifyCommands, printed);

        AnsiConsole.MarkupLineInterpolated($"[bold]{details.Name}[/] — current verify gate set:");
        if (printed.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim](no gates configured)[/]");
        }
        else
        {
            foreach (string line in GateSetAcceptanceDisplay.FormatGateList(printed))
            {
                AnsiConsole.MarkupLine($"  {line.EscapeMarkup()}");
            }
        }

        if (decision.Diff.Count == 0)
        {
            // decision.Diff is only ever empty when decision.Proceed is true (GateSetAcceptance.Decide's
            // own contract: a fingerprint mismatch always produces at least one diff line), so this
            // branch never has anything to say about a moved set — only which flavor of "nothing to
            // diff" applies: a genuine prior acceptance that still matches, or nothing recorded yet
            // because there was never anything here to accept.
            AnsiConsole.MarkupLine(
                details.AcceptedVerifyCommands is not null
                    ? "[dim]Unchanged from what this node already accepts.[/]"
                    : "[dim](nothing recorded yet on this node; recording this now)[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[bold]Diff against this node's last accepted set:[/]");
            foreach (string line in GateSetAcceptanceDisplay.FormatDiffLines(decision.Diff))
            {
                AnsiConsole.MarkupLine($"  {line.EscapeMarkup()}");
            }
        }

        // Re-read fresh, immediately before the append — never the `details` this command already
        // printed from, which is exactly the stale value GateSetAcceptance.CanRecordAcceptance
        // exists to catch a change against (another h9k project set --verify, or a replicated
        // change, landing while this operator was reading the output above).
        ProjectDetails? currentAtCommitTime = await session.LoadAsync<ProjectDetails>(details.Id, cancellationToken);
        if (currentAtCommitTime is null)
        {
            throw new DomainNotFoundException($"Project '{settings.Project}' no longer exists.");
        }

        if (!GateSetAcceptance.CanRecordAcceptance(printed, currentAtCommitTime.VerifyCommands))
        {
            throw new DomainConflictException(
                $"Project '{details.Name}'s verify gate set changed while this command was running — "
                + $"nothing was accepted. Run h9k project accept-gates {details.Name} again to review the "
                + "current set.");
        }

        DateTimeOffset acceptedAt = DateTimeOffset.UtcNow;
        session.Events.Append(
            details.Id, new ProjectGateSetAccepted(details.Id, printed, context.OwnerId, acceptedAt));
        await session.SaveChangesAsync(cancellationToken);

        AnsiConsole.MarkupLineInterpolated(
            $"[green]Accepted {printed.Count} gate(s) for '{details.Name}' on this node.[/]");
        return ExitCodes.Ok;
    }
}

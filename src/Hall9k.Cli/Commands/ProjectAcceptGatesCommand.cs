using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Text;
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

        // RelayedText.Printable, the same discipline GateSetAcceptanceDisplay already applies to
        // every gate this command prints below (independent pre-PR review, cycle 1, conformance
        // lens, low): a project's own name is replicated (ProjectRenamed is ProjectScoped) with no
        // restriction on its characters, so an escape sequence or a lone carriage return in a
        // teammate's rename must not be able to garble this command's own header, refusal, or
        // success line on the very screen a human is reading to decide whether to accept a gate set.
        string printableName = RelayedText.Printable(details.Name);

        AnsiConsole.MarkupLineInterpolated($"[bold]{printableName}[/] — current verify gate set:");
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
            // own contract: a fingerprint mismatch always produces at least one diff line) — except
            // an empty current list, which Decide always proceeds on unconditionally, whatever this
            // node previously accepted (independent pre-PR review, cycle 1, both lenses, low): a
            // project whose gates were all removed by a replicated change would otherwise read as
            // "Unchanged" even when the accepted set was genuinely non-empty, so that case is named
            // on its own rather than folded into either of the other two.
            string message = printed.Count == 0 && details.AcceptedVerifyCommands is { Count: > 0 } removedGates
                ? $"[dim]All {removedGates.Count} previously accepted gate(s) are gone from the current set — recording the empty set now.[/]"
                : details.AcceptedVerifyCommands is not null
                    ? "[dim]Unchanged from what this node already accepts.[/]"
                    : "[dim](nothing recorded yet on this node; recording this now)[/]";
            AnsiConsole.MarkupLine(message);
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
                $"Project '{printableName}'s verify gate set changed while this command was running — "
                + $"nothing was accepted. Run h9k project accept-gates {printableName} again to review the "
                + "current set.");
        }

        DateTimeOffset acceptedAt = DateTimeOffset.UtcNow;
        session.Events.Append(
            details.Id, new ProjectGateSetAccepted(details.Id, printed, context.OwnerId, acceptedAt));
        await session.SaveChangesAsync(cancellationToken);

        AnsiConsole.MarkupLineInterpolated(
            $"[green]Accepted {printed.Count} gate(s) for '{printableName}' on this node.[/]");
        return ExitCodes.Ok;
    }
}

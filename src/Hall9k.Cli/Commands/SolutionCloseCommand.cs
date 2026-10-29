using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Closing a solution honestly: an explicit human act with a reason, the only way one ever closes.
/// Nothing closes a solution automatically, including every member project being archived. A closed
/// solution no longer holds its members, so each is free to join another.
/// </summary>
public sealed class SolutionCloseCommand : Hall9kAsyncCommand<SolutionCloseCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<SOLUTION>")]
        [Description("The solution: its name, or a prefix of its key (h9k solution list shows them)")]
        public string Solution { get; init; } = string.Empty;

        [CommandOption("--reason <REASON>")]
        [Description("Why this solution is done. Required: closing without a reason is exactly the automatic close this platform never does")]
        public string? Reason { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Reason.IsBlank())
        {
            throw new DomainValidationException(
                "A solution closes only by an explicit human act with a reason; nothing closes one automatically, "
                + "including every member being archived. Pass --reason \"<why>\".");
        }

        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        SolutionContext context = await SolutionContext.LoadAsync(session, cancellationToken);
        SolutionView view = SolutionRegistry.Match(context.Solutions, settings.Solution);

        SolutionMembership closed = SolutionDecider.Close(view.Membership, settings.Reason);
        SolutionDecider.RequireEveryMemberHeld(view.Membership, context.Projects, context.RootFingerprintOf, "close");
        SolutionDecider.RequireConsistent(view, context.Projects, context.RootFingerprintOf, "close");

        await SolutionWriter.WriteAsync(session, SolutionWriter.Rewrite(closed, context, removed: []), cancellationToken);

        AnsiConsole.MarkupLineInterpolated($"[dim]Solution '{closed.Name}' closed:[/] {closed.CloseReason}");

        string home = SolutionHomePaths.For(closed.Name, closed.Key);
        if (Directory.Exists(home))
        {
            AnsiConsole.MarkupLineInterpolated($"[dim]Its home at {home} was left on disk; remove it by hand if you no longer want it.[/]");
        }

        return ExitCodes.Ok;
    }
}

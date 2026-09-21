using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// One lesson in full (idea d805fd8b, piece 1): the statement, where it applies, its whole
/// provenance, and its retirement when it has one. Provenance is the half a reader actually
/// weighs a lesson by — a claim from an unattended run reads differently from one a human typed
/// — so it is rendered rather than summarised away.
/// </summary>
public sealed class LearningShowCommand : Hall9kAsyncCommand<LearningShowCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<LESSON>")]
        [Description("The lesson: its id, or an unambiguous fragment of one")]
        public string Learning { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        Guid id = await LearningIdResolver.ResolveAsync(session, settings.Learning, cancellationToken);
        LearningDetails learning = await session.LoadAsync<LearningDetails>(id, cancellationToken)
            ?? throw new DomainNotFoundException($"No lesson {id}.");

        await WriteAsync(session, learning, cancellationToken);
        return ExitCodes.Ok;
    }

    /// <summary>The rendering itself, so an integration test can drive it against a real store without a command app.</summary>
    internal static async Task WriteAsync(
        IQuerySession session, LearningDetails learning, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine($"[bold]{learning.Statement.EscapeMarkup()}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[dim]Id[/]           {learning.Id} [dim](cite it as {DomainId.Short(learning.Id)})[/]");
        AnsiConsole.MarkupLine($"[dim]Scope[/]        {await ScopeLineAsync(session, learning, cancellationToken)}");
        AnsiConsole.MarkupLine($"[dim]Status[/]       {StatusLine(learning)}");
        AnsiConsole.MarkupLine($"[dim]Recorded[/]     {learning.RecordedAt.ToLocalTime():g}");

        string ownerLabel = learning.Provenance is { } provenance
            ? await KnowledgeRecordRendering.OwnerLabelAsync(session, provenance.RecordedByOwnerId, cancellationToken)
            : string.Empty;
        KnowledgeRecordRendering.Write(learning.Provenance, ownerLabel);

        if (learning.RetireReason is { } reason)
        {
            AnsiConsole.MarkupLine($"[dim]Why it ended[/] {reason.EscapeMarkup()}");
        }
    }

    private static async Task<string> ScopeLineAsync(
        IQuerySession session, LearningDetails learning, CancellationToken cancellationToken) =>
        learning.Scope == KnowledgeScope.Owner
            ? $"owner — {(await KnowledgeRecordRendering.OwnerLabelAsync(session, learning.ScopeId, cancellationToken)).EscapeMarkup()}"
            : learning.Scope == KnowledgeScope.Project
                ? $"project — {(await KnowledgeRecordRendering.ProjectLabelAsync(session, learning.ScopeId, cancellationToken)).EscapeMarkup()}"
                : "[dim]unrecorded[/]";

    private static string StatusLine(LearningDetails learning) =>
        learning.Status == LearningStatus.Retired
            ? $"[yellow]retired[/]{(learning.RetiredAt is { } when ? $" on {when.ToLocalTime():g}" : string.Empty)}"
            : learning.Status == LearningStatus.Active
                ? "[green]active[/]"
                : "[dim]unrecorded[/]";
}

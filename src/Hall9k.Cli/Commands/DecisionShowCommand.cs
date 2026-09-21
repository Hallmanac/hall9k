using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Decision;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// One decision in full (idea d805fd8b, piece 1): the statement, where it binds, the incident
/// behind it, its whole provenance, and both directions of supersession — what it replaced, and
/// what replaced it. Both directions come off documents loaded by id, because each is recorded
/// as its own fact on its own stream rather than derived by scanning the other's.
/// </summary>
public sealed class DecisionShowCommand : Hall9kAsyncCommand<DecisionShowCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<DECISION>")]
        [Description("The decision: its id, or an unambiguous fragment of one")]
        public string Decision { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        Guid id = await DecisionIdResolver.ResolveAsync(session, settings.Decision, cancellationToken);
        DecisionDetails decision = await session.LoadAsync<DecisionDetails>(id, cancellationToken)
            ?? throw new DomainNotFoundException($"No decision {id}.");

        await WriteAsync(session, decision, cancellationToken);
        return ExitCodes.Ok;
    }

    /// <summary>The rendering itself, so an integration test can drive it against a real store without a command app.</summary>
    internal static async Task WriteAsync(
        IQuerySession session, DecisionDetails decision, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine($"[bold]{decision.Statement.EscapeMarkup()}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[dim]Id[/]           {decision.Id} [dim](cite it as {DomainId.Short(decision.Id)})[/]");
        AnsiConsole.MarkupLine($"[dim]Scope[/]        {await ScopeLineAsync(session, decision, cancellationToken)}");
        AnsiConsole.MarkupLine($"[dim]Status[/]       {StatusLine(decision)}");
        AnsiConsole.MarkupLine($"[dim]Recorded[/]     {decision.RecordedAt.ToLocalTime():g}");
        AnsiConsole.MarkupLine(decision.OriginIncident is { } incident
            ? $"[dim]Origin[/]       {incident.EscapeMarkup()}"
            : "[dim]Origin[/]       [dim]none recorded[/]");

        string ownerLabel = decision.Provenance is { } provenance
            ? await KnowledgeRecordRendering.OwnerLabelAsync(session, provenance.RecordedByOwnerId, cancellationToken)
            : string.Empty;
        KnowledgeRecordRendering.Write(decision.Provenance, ownerLabel);

        await WriteRelatedAsync(session, "Supersedes", decision.Supersedes, cancellationToken);
        await WriteRelatedAsync(
            session, "Replaced by",
            decision.SupersededByDecisionId is { } by ? [by] : [],
            cancellationToken);

        if (decision.SupersedeReason is { } reason)
        {
            AnsiConsole.MarkupLine($"[dim]Why it ended[/] {reason.EscapeMarkup()}");
        }
    }

    private static async Task<string> ScopeLineAsync(
        IQuerySession session, DecisionDetails decision, CancellationToken cancellationToken) =>
        decision.Scope == KnowledgeScope.Owner
            ? $"owner — {(await KnowledgeRecordRendering.OwnerLabelAsync(session, decision.ScopeId, cancellationToken)).EscapeMarkup()}"
            : decision.Scope == KnowledgeScope.Project
                ? $"project — {(await KnowledgeRecordRendering.ProjectLabelAsync(session, decision.ScopeId, cancellationToken)).EscapeMarkup()}"
                : "[dim]unrecorded[/]";

    private static string StatusLine(DecisionDetails decision) =>
        decision.Status == DecisionStatus.Superseded
            ? $"[yellow]superseded[/]{(decision.SupersededAt is { } when ? $" on {when.ToLocalTime():g}" : string.Empty)}"
            : decision.Status == DecisionStatus.Recorded
                ? "[green]binding[/]"
                : "[dim]unrecorded[/]";

    /// <summary>
    /// A related decision is named by id and quoted by statement where this node holds it, and by
    /// id alone where it does not. A decision whose own stream has not replicated here yet is a
    /// real, ordinary state, and inventing a statement for it would be the guess this platform's
    /// own audit rule forbids.
    /// </summary>
    private static async Task WriteRelatedAsync(
        IQuerySession session, string label, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return;
        }

        IReadOnlyList<DecisionDetails> loaded =
            await session.LoadManyAsync<DecisionDetails>(cancellationToken, [.. ids]);
        Dictionary<Guid, DecisionDetails> byId = loaded.ToDictionary(decision => decision.Id);

        foreach (Guid id in ids)
        {
            string text = byId.TryGetValue(id, out DecisionDetails? related)
                ? related.Statement.EscapeMarkup()
                : "[dim]not held on this node[/]";
            AnsiConsole.MarkupLine($"[dim]{label,-12}[/] {DomainId.Short(id)} {text}");
        }
    }
}

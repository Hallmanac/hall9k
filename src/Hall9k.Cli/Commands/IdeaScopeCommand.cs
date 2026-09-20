using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Sets an idea's own replication scope (idea 8c5993c5): private (this node only), fleet (every
/// node this owner runs), or team (every project member's own fleet). Settable on any idea, in any
/// state — refused only when the idea is already at that scope, or already team and asked to go
/// narrower, since team is one-way.
/// </summary>
public sealed class IdeaScopeCommand : Hall9kAsyncCommand<IdeaScopeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Idea id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandArgument(1, "<private|fleet|team>")]
        [Description(
            "private: never leaves this node. fleet: reaches every node this same owner runs. "
            + "team: reaches every project member's own fleet, one-way once set.")]
        public string Scope { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid ideaId = await IdeaIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        IdeaAggregate idea = await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No idea {ideaId}.");

        ReplicationScope scope = ScopeInput.Parse(settings.Scope);
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        IdeaScopeSet set = IdeaDecider.SetScope(idea, scope, DateTimeOffset.UtcNow, context.OwnerId);
        session.Events.Append(ideaId, set);
        await session.SaveChangesAsync(cancellationToken);

        string shortId = TaskListCommand.ShortId(ideaId);
        AnsiConsole.MarkupLine($"[green]Idea {shortId} is now {scope.Value.ToLowerInvariant()} scope.[/]");
        return ExitCodes.Ok;
    }
}

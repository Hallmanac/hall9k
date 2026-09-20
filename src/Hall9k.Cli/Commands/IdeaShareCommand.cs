using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Shares an idea with the team on the owner's own word (idea 8c5993c5) — sugar for
/// <c>h9k idea scope &lt;id&gt; team</c>, the one door onto team scope an idea has besides being cut
/// into a published task. Works on a captured idea in any state; a discovery walk can call this at
/// its own end. Sharing an idea already at team scope is a no-op success rather than a refusal.
/// </summary>
public sealed class IdeaShareCommand : Hall9kAsyncCommand<IdeaShareCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Idea id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid ideaId = await IdeaIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        IdeaAggregate idea = await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No idea {ideaId}.");

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        IdeaScopeSet? set = IdeaDecider.Share(idea, DateTimeOffset.UtcNow, context.OwnerId);
        if (set is not null)
        {
            session.Events.Append(ideaId, set);
        }

        // Saved unconditionally, not just on the widening branch: NodeBootstrap.EnsureAsync may
        // have staged a genesis OwnerRegistered/NodeRegistered/ConnectionRegistered on this same
        // session two lines above, and it never saves them itself — every other caller commits
        // (independent pre-PR review, idea 19489eff, cycle 11, adversarial, low). Skipping the save
        // on the idempotent no-op path silently dropped that staged write.
        await session.SaveChangesAsync(cancellationToken);

        string shortId = TaskListCommand.ShortId(ideaId);
        AnsiConsole.MarkupLine($"[green]Idea {shortId} is now shared with the team.[/]");
        return ExitCodes.Ok;
    }
}

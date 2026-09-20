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
/// Sets or clears an idea's private flag (idea 202383dc, M2a) — the identical idiom
/// <see cref="Hall9k.Cli.Commands.TaskSetPrivateCommand"/> uses for a task: while private, this
/// idea's own stream never rides an outbox to a teammate's node. Settable on any idea, in any
/// state.
/// </summary>
public sealed class IdeaSetPrivateCommand : Hall9kAsyncCommand<IdeaSetPrivateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Idea id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandArgument(1, "<on|off>")]
        [Description(
            "on/true to keep this idea's own events off every outbox until cleared; off/false to let "
            + "it replicate again on the daemon's next sweep")]
        public string Value { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid ideaId = await IdeaIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        IdeaAggregate idea = await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No idea {ideaId}.");

        bool isPrivate = ParseBool(settings.Value);
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        IdeaScopeSet set = IdeaDecider.SetPrivate(idea, isPrivate, DateTimeOffset.UtcNow, context.OwnerId);
        session.Events.Append(ideaId, set);
        await session.SaveChangesAsync(cancellationToken);

        string shortId = TaskListCommand.ShortId(ideaId);
        AnsiConsole.MarkupLine(isPrivate
            ? $"[green]Idea {shortId} is now private[/] — its events stay off every outbox until cleared."
            : $"[green]Idea {shortId} is no longer private[/] — it replicates on the daemon's next sweep.");
        return ExitCodes.Ok;
    }

    private static bool ParseBool(string value) => value.Trim().ToLowerInvariant() switch
    {
        "on" or "true" => true,
        "off" or "false" => false,
        _ => throw new DomainValidationException($"'{value}' is not on/true or off/false."),
    };
}

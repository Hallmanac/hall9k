using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

public sealed class OwnerSetCommand : Hall9kAsyncCommand<OwnerSetCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[OWNER]")]
        [Description(
            "Whose preferences to change: their name, an unambiguous fragment of it or their email, "
            + "or their full id. Omit it when this platform has exactly one owner — a convenience "
            + "offered only where it cannot be wrong (Decisions Log #34).")]
        public string? Owner { get; init; }

        [CommandOption("--rerequest-review <ON|OFF|DEFAULT>")]
        [Description(
            "Whether closeout asks a pull request's reviewers for another pass once a fix follow-up "
            + "has pushed, so whoever raised the findings countersigns that they were addressed "
            + "(Decisions Log #62). 'on' buys that countersignature and spends review quota for it; "
            + "'off' lets the pull request settle on the internal review, the in-thread replies, and "
            + "CI — the guards that already ran before the fixes were pushed. 'default' clears this "
            + "preference. A project setting outranks this one, and the node default "
            + "(DaemonOptions.DefaultReviewRerequest, off) sits under both. Bounded either way: "
            + "DaemonOptions.MaxReviewRerequestsAfterFixes caps the passes per task so review cannot "
            + "loop on its own refinements.")]
        public string? RerequestReview { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (settings.RerequestReview is null)
        {
            throw new DomainValidationException(
                "Nothing to change — pass --rerequest-review on|off|default. "
                + "h9k owner show prints the current preferences.");
        }

        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        // Registers this machine's owner if the database has never seen one, so the first
        // command a fresh install runs can be this one (every other writing command does the
        // same). Idempotent: an existing owner is found, not replaced.
        await NodeBootstrap.EnsureAsync(session, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        OwnerDetails details = await OwnerResolver.ResolveOrSoleAsync(session, settings.Owner, cancellationToken);
        OwnerAggregate owner =
            (await session.Events.AggregateStreamAsync<OwnerAggregate>(details.Id, token: cancellationToken))!;

        OwnerSettingsChanged changed = OwnerDecider.ChangeSettings(
            owner,
            Optional<ReviewRerequestPolicy>.Of(ReviewRerequestOption.Parse(settings.RerequestReview)),
            DateTimeOffset.UtcNow);

        session.Events.Append(details.Id, changed);
        await session.SaveChangesAsync(cancellationToken);

        AnsiConsole.MarkupLine($"[green]Owner '{details.Name.EscapeMarkup()}' settings updated.[/]");
        return ExitCodes.Ok;
    }
}

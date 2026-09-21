using System.ComponentModel;
using System.Globalization;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Extensions;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Asks this project's other members for history older than anything this node's own automatic
/// catch-up will ever ask for (task a56cf16e). The three node states differ, and only one of them
/// is served automatically: a brand-new node bootstraps the whole project the first time its sweep
/// sees no local history at all, and an established node gap-fills when a peer's outbox stalls
/// across a numeric hole. A node that is neither — joined a while ago, holds the retention
/// window's worth of history and work of its own — never asks for anything older again, because
/// <c>MessageSweepEngine.HasAnyLocalHistoryAsync</c> gates the bootstrap to a node with nothing at
/// all. This command is that node's lever, and the only one.
/// <para>
/// Answered under the explicit-ask rule (<c>EventCatchUpResponder</c>): a peer serves it from below
/// its own replication switch-on point, as it serves a brand-new node's own bootstrap, and unlike
/// an ordinary flush or a gap-fill, which are both still held above that point. Applied
/// idempotently by origin event id (<c>EventReplicationInbox.ApplyAsync</c>), so pulling over
/// streams this node already holds costs a re-read and changes nothing. Private tasks and ideas are
/// never served, however far back the pull reaches.
/// </para>
/// <para>
/// What a pull cannot repair is a stream this node holds only the TAIL of: a replicated event is
/// appended, never inserted, so the older half an answer carries is refused on arrival rather than
/// replayed behind the newer half (<c>EventReplicationInbox</c>). A whole stream this node holds
/// nothing of is the shape a pull actually fetches.
/// </para>
/// <para>
/// Touches no git and no network of its own: it queues an envelope in this node's store and the
/// daemon's next message sweep is what actually sends it.
/// </para>
/// </summary>
public sealed class ProjectPullCommand : Hall9kAsyncCommand<ProjectPullCommand.Settings>
{
    /// <summary>The <c>--since</c> word that means "no lower bound at all".</summary>
    private const string AllSentinel = "all";

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PROJECT>")]
        [Description("Project name, an unambiguous fragment of it, or its id.")]
        public string Project { get; init; } = string.Empty;

        [CommandOption("--since <GLOBAL-SEQUENCE|all>")]
        [Description(
            "How far back to ask. A global sequence number is read on the ANSWERING node, not this "
            + "one: every event it holds at or above that number, for this project, is served. The "
            + "word all asks for everything it holds. Required, because the two are very different "
            + "asks and neither is a safe default: a peer's whole history can be large, and a "
            + "bound you can name (from that node's own diagnosis, or from a sequence in a log) is "
            + "the cheaper question. No h9k command prints a node's own global sequence today.")]
        public string Since { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        return await RunAsync(session, settings, cancellationToken);
    }

    /// <summary>The whole command body, with its session handed in rather than opened through
    /// <see cref="CliStore.Open()"/> — this codebase's CLI commands have no other test seam.</summary>
    internal static async Task<int> RunAsync(
        IDocumentSession session, Settings settings, CancellationToken cancellationToken)
    {
        long sinceGlobalSequence = ParseSince(settings.Since);
        ProjectDetails project = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        string? ownerRootFingerprint = await OwnerRootFingerprintResolver.ResolveAsync(
            session, context.OwnerId, cancellationToken);

        EventStreamCatchUp.RequestDisposition disposition = await EventStreamCatchUp.RequestProjectHistoryAsync(
            session, project.Id, sinceGlobalSequence, context.NodeId, ownerRootFingerprint, DateTimeOffset.UtcNow,
            cancellationToken);
        if (disposition == EventStreamCatchUp.RequestDisposition.NoOwnerRoot)
        {
            throw new DomainValidationException(EventStreamCatchUp.ProjectPullBlockedRefusal(project.Name));
        }

        await session.SaveChangesAsync(cancellationToken);

        string bound = sinceGlobalSequence == 0
            ? "everything they hold"
            : $"everything they hold from global sequence {sinceGlobalSequence}";
        string standing = (disposition, project.IsEligibleForMessaging()) switch
        {
            // A project named on the command line is resolved with no eligibility check of its own,
            // so this ask may have queued for one the sweep can never flush today (archived, or
            // with no repository yet) — "the daemon's next message sweep sends it" is an outright
            // false promise there, not merely an optimistic one. The same correction
            // MessageSendCommand and TaskHandoffCommand already carry, in their own words
            // (independent pre-PR review, cycle 1, adversarial lens, low).
            (EventStreamCatchUp.RequestDisposition.Queued, true) => "the daemon's next message sweep sends it",
            (EventStreamCatchUp.RequestDisposition.Queued, false) =>
                "it stays queued until this project is eligible for messaging (not archived, with a repository) — "
                + "the daemon's sweep cannot send it yet",
            (_, true) => "a request reaching at least that far back is already outstanding, so nothing new was queued",
            (_, false) =>
                "a request reaching at least that far back is already queued, and stays queued until this project "
                + "is eligible for messaging (not archived, with a repository)",
        };
        AnsiConsole.MarkupLineInterpolated(
            $"[blue]Asked[/] {project.Name}'s other members for {bound} [dim]({standing})[/].");
        AnsiConsole.MarkupLine(
            "[dim]Answers apply idempotently over streams this node already holds, and a private task or idea "
            + "is never served however far back the pull reaches. A stream this node holds only the tail of "
            + "stays as it is: an older event cannot be put in front of a newer one already here. "
            + "h9k status shows the request while it stands.[/]");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// <c>all</c> or a non-negative global sequence. Pure so the wording of both refusals is
    /// checkable without driving the whole command. <c>all</c> becomes 0 rather than a separate
    /// mode: the bound is a "at or above" test on the answering node's own sequence, and every real
    /// event's sequence is at least 1, so 0 already means "no bound".
    /// </summary>
    internal static long ParseSince(string? since)
    {
        if (since.IsBlank())
        {
            throw new DomainValidationException(
                "h9k project pull needs --since: a global sequence to ask from (read on the answering node), "
                + $"or the word {AllSentinel} for that node's whole history.");
        }

        if (since.Trim().Equals(AllSentinel, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (long.TryParse(since.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            && parsed >= 0)
        {
            return parsed;
        }

        throw new DomainValidationException(
            $"'{since}' is not a --since value: pass a non-negative global sequence number, or the word "
            + $"{AllSentinel} for the answering node's whole history.");
    }
}

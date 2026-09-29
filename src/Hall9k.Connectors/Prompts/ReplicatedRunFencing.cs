using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Marten;

namespace Hall9k.Connectors.Prompts;

/// <summary>
/// Marks the review-park resolutions and human-directed interactions on a task's runs that another
/// owner's node replicated here, so a prompt shows each labelled, capped and fenced
/// (<see cref="ReplicatedNote"/>) instead of as this owner's settled ruling or a human's standing
/// directive. Both the daemon's review passes and the <c>h9k pr review</c> briefing read the same
/// run projections, and both judge by the verified sender of the event itself.
/// <para>
/// The projection keeps no sender (a row written before one existed would read as native, the wrong
/// answer), so each projected entry is matched back to its event on the run streams by what the
/// event itself carries. Any foreign match counts, the fail-closed side of a coincidence nobody
/// expects. Nothing is read when there is nothing to judge, and the fleet only when some event was
/// replicated.
/// </para>
/// </summary>
public static class ReplicatedRunFencing
{
    /// <summary>The replicated (sender recorded) resolution and interaction events on <paramref name="runIds"/>, read once.</summary>
    public static async Task<ReplicatedRunEvents> ReadAsync(
        IQuerySession query, IReadOnlyList<Guid> runIds, CancellationToken cancellationToken)
    {
        List<(ReviewParkResolved Resolved, ReplicatedFrom From)> resolutions = [];
        List<(ExternalInteractionLogged Logged, ReplicatedFrom From)> interactions = [];
        foreach (Guid runId in runIds)
        {
            resolutions.AddRange((await ReplicatedSender.AllAsync<ReviewParkResolved>(query, runId, cancellationToken))
                .Where(entry => entry.From.SenderNodeId is not null));
            interactions.AddRange((await ReplicatedSender.AllAsync<ExternalInteractionLogged>(query, runId, cancellationToken))
                .Where(entry => entry.From.SenderNodeId is not null));
        }

        return new ReplicatedRunEvents(resolutions, interactions);
    }

    /// <summary>
    /// <paramref name="rulings"/> with every resolution a foreign node replicated marked
    /// (<see cref="ReviewParkResolution.ForeignNote"/>). <paramref name="localFleet"/> is asked for
    /// only when some event in <paramref name="replicated"/> matches a ruling, and a null answer
    /// fences.
    /// </summary>
    public static async ValueTask<IReadOnlyList<ReviewParkResolution>> FenceRulingsAsync(
        IReadOnlyList<ReviewParkResolution> rulings, ReplicatedRunEvents replicated,
        Func<CancellationToken, ValueTask<LocalFleet?>> localFleet, CancellationToken cancellationToken)
    {
        if (rulings.Count == 0 || replicated.Resolutions.Count == 0)
        {
            return rulings;
        }

        return FenceRulings(rulings, replicated.Resolutions, await localFleet(cancellationToken));
    }

    /// <summary>The interactions counterpart of <see cref="FenceRulingsAsync"/>.</summary>
    public static async ValueTask<IReadOnlyList<ExternalInteractionRecord>> FenceInteractionsAsync(
        IReadOnlyList<ExternalInteractionRecord> interactions, ReplicatedRunEvents replicated,
        Func<CancellationToken, ValueTask<LocalFleet?>> localFleet, CancellationToken cancellationToken)
    {
        if (interactions.Count == 0 || replicated.Interactions.Count == 0)
        {
            return interactions;
        }

        return FenceInteractions(interactions, replicated.Interactions, await localFleet(cancellationToken));
    }

    public static IReadOnlyList<ReviewParkResolution> FenceRulings(
        IReadOnlyList<ReviewParkResolution> rulings,
        IReadOnlyList<(ReviewParkResolved Resolved, ReplicatedFrom From)> replicated, LocalFleet? localFleet) =>
    [
        .. rulings.Select(ruling => ForeignSenderOf(
            replicated,
            entry => entry.Data.Verdict == ruling.Verdict
                && entry.Data.Reason == ruling.Reason
                && entry.Data.ResolvedAt == ruling.ResolvedAt,
            localFleet) is { SenderNodeId: { } sender } foreign
            ? ruling with
            {
                ForeignNote = ReplicatedNote.ForeignRuling(
                    ruling.Verdict == ReviewVerdict.MergeReady ? "merge-ready" : "needs-fixes",
                    ruling.Reason, sender, foreign.OriginNodeId, localFleet),
            }
            : ruling),
    ];

    public static IReadOnlyList<ExternalInteractionRecord> FenceInteractions(
        IReadOnlyList<ExternalInteractionRecord> interactions,
        IReadOnlyList<(ExternalInteractionLogged Logged, ReplicatedFrom From)> replicated, LocalFleet? localFleet) =>
    [
        .. interactions.Select(interaction => ForeignSenderOf(
            replicated,
            entry => entry.Data.LoggedAt == interaction.LoggedAt
                && entry.Data.Party == interaction.Party
                && entry.Data.Summary == interaction.Summary
                && entry.Data.HumanDirected == interaction.HumanDirected
                && entry.Data.Reason == interaction.Reason,
            localFleet) is { SenderNodeId: { } sender } foreign
            ? interaction with
            {
                ForeignNote = ReplicatedNote.ForeignInteraction(
                    interaction.Party, interaction.Summary, interaction.Reason, sender, foreign.OriginNodeId, localFleet),
            }
            : interaction),
    ];

    private static ReplicatedFrom? ForeignSenderOf<TEvent>(
        IReadOnlyList<(TEvent Data, ReplicatedFrom From)> replicated, Func<(TEvent Data, ReplicatedFrom From), bool> matches,
        LocalFleet? localFleet)
    {
        foreach ((TEvent Data, ReplicatedFrom From) entry in replicated)
        {
            if (matches(entry)
                && entry.From.SenderNodeId is { } sender
                && ReplicatedNote.IsForeign(sender, entry.From.OriginNodeId, localFleet))
            {
                return entry.From;
            }
        }

        return null;
    }
}

/// <summary>The events <see cref="ReplicatedRunFencing.ReadAsync"/> found with a recorded sender.</summary>
public sealed record ReplicatedRunEvents(
    IReadOnlyList<(ReviewParkResolved Resolved, ReplicatedFrom From)> Resolutions,
    IReadOnlyList<(ExternalInteractionLogged Logged, ReplicatedFrom From)> Interactions);

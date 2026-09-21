using Hall9k.Connectors.Replication;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Infrastructure.Ids;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The catch-up block <c>h9k status</c> prints: the asks still waiting on an answer, the ones a
/// peer declined recently enough to still explain a gap, and how many streams this node is holding
/// the tail of. Composed rather than printed directly, the same split
/// <see cref="ThroughputPane"/> already draws, so what a reader sees is assertable without a
/// terminal.
/// <para>
/// The declined lines are why this exists. Until they did, this pane showed outstanding requests
/// only, so the whole visible history of an ask was "waiting" and then nothing: a request a peer
/// had declined simply vanished from the pane, and a human whose task never arrived had no way to
/// tell an ask still in flight from one the fleet had already refused. Origin incident
/// (2026-09-19): Windows asked for one stream at 13:40, the Mac declined at 13:41, and at 23:36 a
/// second attempt was told an ask was still on its way, because nothing printed either fact.
/// </para>
/// </summary>
internal static class EventCatchUpPane
{
    /// <summary>
    /// How far back a closed-by-decline ask is still worth printing. A day, because that is the
    /// span over which a human is still asking "why is that task not here" about work they saw
    /// referenced this morning; older than that and the decline explains nothing they are
    /// currently looking at, while a pane that never forgot one would grow without bound.
    /// </summary>
    public static readonly TimeSpan DeclineWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// The whole block, in order: outstanding asks oldest first, then declines newest first (the
    /// most recent refusal is the one that explains what a human is looking at now), then the
    /// held-tail counts. Empty when there is nothing to say, which is the quiet-pane posture the
    /// rest of <c>h9k status</c> already follows.
    /// </summary>
    /// <param name="outstanding">Every request still waiting on an answer.</param>
    /// <param name="closed">
    /// Every request that has ended, however it ended — this method picks out the ones a decline
    /// ended, inside <see cref="DeclineWindow"/>, so that rule is stated here rather than in a
    /// query and can be asserted without a database.
    /// </param>
    /// <param name="heldTail">The node-wide held-tail counts (<see cref="HeldTailStreams.SummarizeAsync"/>).</param>
    /// <param name="now">This node's own clock, which the decline window is measured back from.</param>
    public static IReadOnlyList<string> ComposeLines(
        IReadOnlyList<EventCatchUpRequest> outstanding,
        IReadOnlyList<EventCatchUpRequest> closed,
        HeldTailSummary heldTail,
        DateTimeOffset now)
    {
        List<string> lines = [];

        foreach (EventCatchUpRequest request in outstanding.OrderBy(request => request.SentAt))
        {
            // A fleet reconcile (task 252bc5cf) is addressed to one sibling by node id and ranks
            // no candidates at all, so without its own arm it reads as a broadcast to the whole
            // project when it went to exactly one peer.
            string candidate = request switch
            {
                { CurrentCandidateNodeId: { } current } => $"asking {DomainId.Short(current)}",
                { ToNodeId: { } addressee } => $"asking {DomainId.Short(addressee)}",
                _ => "broadcast to the whole project",
            };
            lines.Add($"catch-up outstanding for {Describe(request)} — {candidate} (sent {request.SentAt:u})");
        }

        DateTimeOffset declinedSince = now - DeclineWindow;
        IEnumerable<(EventCatchUpRequest Request, EventCatchUpDecline Decline)> recentDeclines = closed
            .Where(request => request.ClosedByDecline is not null && request.ClosedByDecline.DeclinedAt >= declinedSince)
            .Select(request => (Request: request, Decline: request.ClosedByDecline!))
            .OrderByDescending(pair => pair.Decline.DeclinedAt);
        foreach ((EventCatchUpRequest request, EventCatchUpDecline decline) in recentDeclines)
        {
            lines.Add(
                $"catch-up for {Describe(request)} declined by {DomainId.Short(decline.DeclinedByNodeId)} "
                + $"at {decline.DeclinedAt:u} ({Printable(decline.Reason)}){OtherDeclinersClause(request, decline)}");
        }

        if (heldTail.StreamsHeldTailOnly > 0 || heldTail.StreamsGivenUp > 0)
        {
            lines.Add(
                $"catch-up holds {Streams(heldTail.StreamsHeldTailOnly)} tail-only, "
                + $"{heldTail.StreamsGivenUp} given up after {EventCatchUpCoordinator.MaxHeldTailAttempts} asks");
        }

        return lines;
    }

    /// <summary>What a request is asking for — shared by the outstanding and the declined lines so
    /// the two can never describe the same ask in different words.</summary>
    private static string Describe(EventCatchUpRequest request) => request switch
    {
        // A dependency ask nobody typed says whose dependency it is: the platform mints
        // it on its own when a pulled or adopted task names a blocked-by or stacked-on
        // id whose stream is not here (TaskDependencyCatchUp), and a bare stream id
        // would read as an ask this node cannot account for.
        { ForStreamId: { } streamId, ForDependencyOfTaskId: { } dependentTaskId } =>
            $"stream {DomainId.Short(streamId)}, a dependency of task {DomainId.Short(dependentTaskId)}",
        { ForStreamId: { } streamId } => $"stream {DomainId.Short(streamId)}",
        { ForOriginNodeId: { } originNodeId } => $"a gap from {DomainId.Short(originNodeId)} (since {request.SinceOriginSequence})",
        // The fleet-reconcile shape carries the identical bound h9k project pull --since all does,
        // so it has to be matched on ToNodeId BEFORE the bound arms below or it reads as a hand
        // pull nobody typed, broadcast to every member, when it went to one sibling by node id
        // (task 252bc5cf, independent pre-PR review, cycle 1, both lenses, low).
        { ToNodeId: not null } => "this project's whole history (a fleet reconcile)",
        { SinceGlobalSequence: 0 } => "this project's whole history (h9k project pull --since all)",
        { SinceGlobalSequence: { } sinceGlobalSequence } =>
            $"this project's history from global sequence {sinceGlobalSequence} (h9k project pull --since)",
        _ => "a brand-new node's own bootstrap",
    };

    /// <summary>The other members that also said they hold nothing for this ask, counted rather
    /// than listed: on a broadcast every member answers, and a human reading one name would take
    /// "one peer could not help" from what is actually "nobody could".</summary>
    private static string OtherDeclinersClause(EventCatchUpRequest request, EventCatchUpDecline closing)
    {
        int others = request.Declines.Count(decline => decline.DeclinedByNodeId != closing.DeclinedByNodeId);
        return others switch
        {
            0 => string.Empty,
            1 => ", and by 1 other member",
            _ => $", and by {others} other members",
        };
    }

    private static string Streams(int count) => count == 1 ? "1 stream" : $"{count} streams";

    /// <summary>
    /// The longest decline reason this pane prints. A reason is one clause explaining why a peer
    /// holds nothing; anything past this is not explanation, and a status line that wrapped for
    /// forty lines would bury every other pane under it.
    /// </summary>
    private const int MaxReasonLength = 200;

    /// <summary>
    /// A peer's own reason string, made safe to print. The text crossed the wire from another
    /// node, and the console renderer escapes Spectre markup but not control characters, so a
    /// newline or an ANSI escape sequence in it would break this pane's line structure or restyle
    /// everything printed after it (independent pre-PR review, cycle 1, adversarial lens, low).
    /// Control characters become spaces rather than being dropped, so a reason cannot be made to
    /// read as a different word than the one that was sent, and the whole is bounded at
    /// <see cref="MaxReasonLength"/>. The stored decline keeps the reason verbatim either way —
    /// this is a rule about printing, not about what was observed.
    /// </summary>
    private static string Printable(string reason)
    {
        // Never cut between the two halves of a surrogate pair: half an emoji is a character this
        // terminal cannot render at all, which is a worse line than one character shorter.
        int take = Math.Min(reason.Length, MaxReasonLength);
        if (take < reason.Length && char.IsHighSurrogate(reason[take - 1]))
        {
            take--;
        }

        string flattened = string.Create(
            take,
            reason,
            static (span, source) =>
            {
                for (int i = 0; i < span.Length; i++)
                {
                    span[i] = char.IsControl(source[i]) ? ' ' : source[i];
                }
            });
        return reason.Length > MaxReasonLength ? flattened + "…" : flattened;
    }
}

using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks.Events;
using JasperFx.Events;

namespace Hall9k.Domain.Features.Replication;

/// <summary>
/// What makes a local stream a PARTIAL replicated stream: its very first event is a copy of a
/// teammate's fact (it carries the replication origin headers this platform stamps on every applied
/// copy, <see cref="ReplicationEventHeaders"/>) and it is not that aggregate's own genesis
/// (<see cref="AggregateGenesisEventTypes"/>), so the story on this node starts in the middle, and
/// every document projected from it describes a task, idea, epic or run whose beginning this node
/// has never seen.
/// <para>
/// Read off the events alone, never off a projected document. The v0.10.20 repair asked the
/// document instead (a <c>TaskListItem</c> with no project id or no added-at, an
/// <c>IdeaDetails</c> with no state or no captured-at), which had two costs. It could only ever
/// find the two aggregates whose documents it knew to query, so a partial Run stream (a tail that
/// opens at <see cref="TokensRecorded"/>, leaving a <c>RunListItem</c> with an empty task id) and a
/// partial Epic stream were invisible to it whatever they held. And the tell itself is incidental
/// rather than structural: it works only while every field it names happens to be written by the
/// genesis event and nothing else, which is a fact about two projections' current shape, not about
/// the stream. Asking the events is the same question asked where it is actually answerable.
/// </para>
/// </summary>
public static class PartialReplicatedStreamRules
{
    /// <summary>
    /// Which aggregate an event type belongs to, by the namespace its own feature slice owns:
    /// the same tell <c>Hall9k.Cli.Commands.EventStreamCatchUp.ClassifyLocalHoldAsync</c> already
    /// uses to decide whether a stream is a task's at all. A namespace rather than a per-type list
    /// deliberately: a list of every non-genesis event type across four aggregates would silently
    /// stop covering a new one the moment it shipped, which is the exact class of miss this whole
    /// repair exists to clean up after.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, ReplicatedAggregate> AggregatesByEventNamespace =
        new Dictionary<string, ReplicatedAggregate>
        {
            [typeof(TaskAdded).Namespace!] = ReplicatedAggregate.Task,
            [typeof(IdeaCaptured).Namespace!] = ReplicatedAggregate.Idea,
            [typeof(EpicAdded).Namespace!] = ReplicatedAggregate.Epic,
            [typeof(RunDispatched).Namespace!] = ReplicatedAggregate.Run,
        };

    /// <summary>
    /// The aggregate <paramref name="eventType"/> belongs to, or <see cref="ReplicatedAggregate.Unknown"/>
    /// for every event type outside the four replicated aggregates' own slices.
    /// </summary>
    public static ReplicatedAggregate AggregateOf(Type eventType) =>
        eventType.Namespace is string eventNamespace
            ? AggregatesByEventNamespace.GetValueOrDefault(eventNamespace, ReplicatedAggregate.Unknown)
            : ReplicatedAggregate.Unknown;

    /// <summary>
    /// Whether this applied copy came from a teammate rather than from this node itself: an event
    /// this node appended natively carries no origin node header at all, which is precisely what
    /// tells a replicated event apart from a native one on the same stream;
    /// <c>Hall9k.Connectors.Replication.EventReplicationInbox</c>'s own origin high-water read
    /// makes the identical distinction, for the identical reason.
    /// </summary>
    public static bool CarriesReplicationOrigin(IEvent @event) =>
        @event.GetHeader(ReplicationEventHeaders.OriginNodeId) is string originNodeIdText
        && Guid.TryParse(originNodeIdText, out _);

    /// <summary>
    /// Whether <paramref name="first"/>, a stream's own version 1, makes that stream a partial
    /// replicated stream. The Project aggregate's per-install lifecycle events are named as an
    /// exclusion in their own right rather than left to fall out of
    /// <see cref="AggregateOf"/>: a teammate's <c>ProjectArchived</c> under a foreign project id is
    /// MEANT to start a stream that never carries a genesis
    /// (<see cref="ProjectStreamReplicationRules.IsProjectLifecycleEvent"/>'s own doc), so saying so
    /// here is what keeps a future move of either rule from quietly selecting a phantom this repair
    /// must never touch.
    /// </summary>
    public static bool IsPartialStreamHead(IEvent first) =>
        CarriesReplicationOrigin(first)
        && !ProjectStreamReplicationRules.IsProjectLifecycleEvent(first.EventType)
        && AggregateOf(first.EventType) != ReplicatedAggregate.Unknown
        && !AggregateGenesisEventTypes.IsGenesis(first.EventType);

    /// <summary>The set behind <see cref="IsDroppableNativeEvent"/>, whose own doc says what each
    /// door is and why dropping it is reasoned rather than guessed at.</summary>
    private static readonly IReadOnlySet<Type> DroppableNativeEventTypes = new HashSet<Type>
    {
        typeof(TaskDependencyCompleted),
        typeof(TaskDependencyFailed),
        typeof(TaskDependencyRecovered),
        typeof(TaskClaimed),
        typeof(TrackerAssignmentObserved),
        typeof(TaskRequeued),
        typeof(TaskHolderReleased),
    };

    /// <summary>
    /// Every native event this node's own dispatch loop can legitimately have appended onto a
    /// partial task stream it mistook for a real task, and which the repair drops rather than
    /// re-holds. An enumeration of this node's own writers rather than a structural guarantee, so
    /// each door is named with the code that reaches it and a door added later is refused rather
    /// than silently dropped: that is the conservative half of the never-guess rule (AGENTS.md).
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="TaskDependencyCompleted"/>, <see cref="TaskDependencyFailed"/> and
    /// <see cref="TaskDependencyRecovered"/> — everything
    /// <c>Hall9k.Domain.Features.Tasks.Handlers.TaskDependencyResolver</c> can write about a
    /// phantom that replayed into Blocked, through either of its entry points. All three are
    /// bookkeeping over the unmet set that the resolver re-decides from scratch on every pass
    /// ("dead NOW is the whole question", its own doc), so dropping one costs nothing the next
    /// dispatch cycle does not rewrite against the task's true history.
    /// </description></item>
    /// <item><description>
    /// <see cref="TaskClaimed"/> — the dispatcher claiming the phantom, which is the shape the
    /// 13d6b371 incident actually took — and <see cref="TrackerAssignmentObserved"/>, the claim
    /// gate's own evidence, which <c>Hall9k.Daemon.Dispatch.DispatchEngine.TryClaimAsync</c>
    /// appends in the same transaction as the claim it justified and never on its own. The
    /// evidence goes with the claim: left behind, it would attest to a claim no longer on the
    /// stream, and the next real claim reads the gate afresh and records its own.
    /// </description></item>
    /// <item><description>
    /// <see cref="TaskRequeued"/> and <see cref="TaskHolderReleased"/> — the lease sweep giving
    /// that claim back. Named as a PAIR because the sweep only ever writes them as one:
    /// <c>DispatchEngine.SweepExpiredLeasesAsync</c> appends both whenever the expiring lease and
    /// the task's holder are this node, and a native <see cref="TaskClaimed"/> always makes both
    /// true, so allowing the requeue alone left that allowance unreachable through the only door
    /// that produces it (independent pre-PR review, cycle 1, adversarial lens).
    /// </description></item>
    /// </list>
    /// <para>
    /// Dropped rather than held on purpose: re-holding them would replay a claim of a run that
    /// never started, once the genesis finally landed, and leave the task reading Working with no
    /// run left alive to conclude it. What is lost is this node's own local record of a claim that
    /// never ran; what is kept is a task that reads its true state and can be claimed again. Any
    /// OTHER native event is outside that reasoning, so a stream carrying one is left exactly as it
    /// is rather than guessed at (AGENTS.md's never-guess rule).
    /// </para>
    /// </summary>
    public static bool IsDroppableNativeEvent(Type eventType) =>
        DroppableNativeEventTypes.Contains(eventType);
}

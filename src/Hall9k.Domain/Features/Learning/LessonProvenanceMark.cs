using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Learning;

/// <summary>
/// Who recorded a lesson and where, as one mark a reader can act on (idea d805fd8b, piece 5;
/// backlog 55). A closed vocabulary, so a sealed record with static instances rather than an enum
/// (TASK-MODEL.md §8): the security review in idea 7e403b80 may well split or widen these, and a
/// fourth mark is then a static instance here plus a clause in <see cref="ReachesAPrompt"/>, never
/// a schema change.
/// <para>
/// This is what makes IDEA-learning-capture's live-at-once rule safe at the prompt seam rather
/// than only in <c>lessons.md</c>. A lesson is live the moment it lands, with no quarantine, so
/// the reader of an injected lesson has to be able to see what the platform actually observed
/// about where it came from. Every mark below is that observation and nothing further: none of
/// them is an inference about who typed the sentence.
/// </para>
/// <para>
/// Every instance below is a fact somebody observed, including
/// <see cref="AgentOnUnobservedNode"/>: an event appended before the origin stamping existed, or
/// one stamped while this install was still bootstrapping, genuinely has no readable recording
/// node, and that is a different thing from <see cref="Unknown"/>, which is a lesson whose stream
/// carries no provenance at all.
/// </para>
/// <para>
/// The one place this deviates from its sibling closed vocabularies (<see cref="KnowledgeScope"/>,
/// <see cref="LearningStatus"/>, <see cref="HumanAttendance"/>): no JsonConverter and no implicit
/// string conversion, because nothing persists a mark. It is derived at read time from a lesson's
/// provenance and the asking node's own identity, so there is no stored value to round-trip and a
/// converter here would be machinery that never runs.
/// </para>
/// </summary>
public sealed record LessonProvenanceMark
{
    /// <summary>
    /// The lesson names no run at all (<see cref="RecordedProvenance.FromShell"/>), and the mark
    /// says exactly that rather than "a person recorded it". Two callers produce this provenance
    /// and nothing on the event separates them: a human typing <c>h9k learn</c> at a shell, and an
    /// agent that records a lesson without naming its run through <c>--task</c>. Reading it as a
    /// person was a guess the platform's own default verb contradicted — a daemon-dispatched
    /// session carries no run-naming environment variable, so the bare verb the shipped templates
    /// once handed it recorded an unattended agent's claim and labelled it human-typed, which also
    /// walked it straight past the hold this vocabulary exists to enforce (independent pre-PR
    /// review, cycle 3, adversarial lens). The templates now name the task, so a dispatched
    /// session's own lesson lands under <see cref="AgentOnThisNode"/>; this mark is what is left
    /// for a recording that genuinely named no run.
    /// <para>
    /// Never inferred from attendance either: a run the platform saw an operator attached to is
    /// still an agent's own recording, and it is marked as one.
    /// </para>
    /// </summary>
    public static readonly LessonProvenanceMark NoRunNamed = new("NoRunNamed", "recorded with no run named");

    /// <summary>Recorded from a run on this node. Reaches a prompt: whatever wrote it, this install's own agent is what it was.</summary>
    public static readonly LessonProvenanceMark AgentOnThisNode = new("AgentOnThisNode", "recorded by an agent run on this node");

    /// <summary>
    /// Recorded from a run on another node of this owner's own fleet and replicated here by a node
    /// of that fleet. Reaches a prompt: the owner's own machines are the same trust boundary as
    /// this one, and the fleet is what the ledger chain vouches for, not a value the lesson wrote
    /// about itself.
    /// </summary>
    public static readonly LessonProvenanceMark AgentOnFleetNode =
        new("AgentOnFleetNode", "recorded by an agent run on another node of this owner's fleet");

    /// <summary>
    /// Recorded from a run on a different node and replicated here, where that node is not in this
    /// owner's fleet (or the fleet is not known). Rendered in <c>lessons.md</c> and held out of
    /// every prompt until idea 7e403b80's security review rules otherwise: a lesson rides into the
    /// instructions of every later session on the project, and nothing today authenticates the
    /// agent that wrote one on a machine this node does not control.
    /// </summary>
    public static readonly LessonProvenanceMark AgentOnAnotherNode = new("AgentOnAnotherNode", "recorded by an agent run on another node");

    /// <summary>
    /// Recorded from a run whose node could not be read off the event at all
    /// (<see cref="Hall9k.Domain.Infrastructure.Persistence.EventRecordingNode"/>). Held out of a
    /// prompt for the same reason as <see cref="AgentOnAnotherNode"/> and then some: a lesson that
    /// cannot be shown to have come from this node has not been shown to be safe, and treating an
    /// unreadable header as "ours" is exactly the guess the never-guess rule forbids.
    /// </summary>
    public static readonly LessonProvenanceMark AgentOnUnobservedNode =
        new("AgentOnUnobservedNode", "recorded by an agent run on a node nobody recorded");

    /// <summary>
    /// The verified sender of this lesson's event is outside this owner's fleet, or the fleet could
    /// not be read (fail closed), whatever run the lesson names. Held out of every prompt. The
    /// sender is the node that delivered the record to this one, stamped by the inbox, and not the
    /// origin node or the run the lesson claims: both of those are values the sender wrote, so a
    /// foreign lesson naming no run at all (which reads as <see cref="NoRunNamed"/> when native) or
    /// naming one of this owner's own nodes would otherwise walk straight past the hold.
    /// </summary>
    public static readonly LessonProvenanceMark ReplicatedFromOutsideFleet =
        new("ReplicatedFromOutsideFleet", "replicated from a node outside this owner's fleet");

    /// <summary>The lesson's stream carries no provenance to read. Serializes as an empty string, and reaches no prompt.</summary>
    public static readonly LessonProvenanceMark Unknown = new("", "recorded with no provenance at all");

    /// <summary>The whole vocabulary, in the order a reader meets the marks above.</summary>
    public static readonly IReadOnlyList<LessonProvenanceMark> All =
        [NoRunNamed, AgentOnThisNode, AgentOnFleetNode, AgentOnAnotherNode, AgentOnUnobservedNode, ReplicatedFromOutsideFleet, Unknown];

    /// <summary>
    /// Every mark <see cref="ReachesAPrompt"/> is false for, in that same order, which is the
    /// order a section's own held-back breakdown names them. Derived from <see cref="All"/> and
    /// the rule itself rather than listed a second time by hand: a fourth held mark is then still
    /// one edit to this file — the reason this is a vocabulary record and not an enum — and can
    /// never be counted by the composer while going unnamed by the section reporting it
    /// (idea d805fd8b, piece 5).
    /// </summary>
    public static readonly IReadOnlyList<LessonProvenanceMark> HeldFromPrompts =
        [.. All.Where(mark => !mark.ReachesAPrompt)];

    public string Value { get; }

    /// <summary>
    /// The words a rendered document or an injected prompt line uses for this mark, so the two
    /// cannot drift. Phrased as a participle clause in every instance above, because a section's
    /// held-back breakdown reads it straight after a count ("two lessons recorded by an agent run
    /// on another node") as well as inside a parenthetical.
    /// </summary>
    public string Label { get; }

    private LessonProvenanceMark(string value, string label)
    {
        Value = value;
        Label = label;
    }

    /// <summary>
    /// Whether a lesson carrying this mark is injected into a dispatched session's prompt. True
    /// for <see cref="NoRunNamed"/>, <see cref="AgentOnThisNode"/> and <see cref="AgentOnFleetNode"/>
    /// only, and only ever for a lesson whose verified sender is this node or in this owner's fleet
    /// (<see cref="Of"/> gives every other replicated lesson <see cref="ReplicatedFromOutsideFleet"/>
    /// before it reads the run at all): the light security
    /// pass Brian asked for while the distributed-team functionality is built, pending the
    /// in-depth review in idea 7e403b80. <see cref="NoRunNamed"/> sits on that side because it is
    /// the only mark a human's own lesson can carry, so holding it back would mean nothing a
    /// person ever typed reaches a prompt at all. The honest cost, stated rather than papered
    /// over: a run-less agent recording rides in beside those, which is why the mark's own words
    /// claim no person and why the templates name the task. Read by the composer alone; nothing
    /// filters <c>lessons.md</c> on it, which is the point: a held-back lesson is visible to
    /// anybody reading the file and to <c>h9k learn list</c>, it simply does not write itself into
    /// another session's instructions.
    /// </summary>
    public bool ReachesAPrompt => this == NoRunNamed || this == AgentOnThisNode || this == AgentOnFleetNode;

    /// <summary>
    /// The mark for one recorded lesson, from its provenance and the node the event was stamped
    /// with, against the node asking.
    /// </summary>
    /// <param name="thisNodeId">
    /// The asking node's own id, or <see cref="Guid.Empty"/> when this install does not know it
    /// yet. Empty never resolves to <see cref="AgentOnThisNode"/>: a node that cannot name itself
    /// cannot claim a lesson as its own, so every agent-recorded lesson reads as
    /// <see cref="AgentOnUnobservedNode"/> instead and none of them reach a prompt.
    /// </param>
    /// <param name="receivedFromNodeId">
    /// The verified sender (<see cref="Replication.ReplicatedSender"/>): null for a lesson this node
    /// recorded, a node id or <see cref="Guid.Empty"/> for a replicated one. A replicated lesson is
    /// only ever as trusted as its sender and the node it began on, so both are checked first,
    /// before any claim the lesson makes about itself (its run, its attendance).
    /// </param>
    /// <param name="localFleet">
    /// The asking owner's fleet, or null when it could not be read. Null holds every replicated
    /// lesson (fail closed) and leaves a native one unaffected.
    /// </param>
    public static LessonProvenanceMark Of(
        RecordedProvenance? provenance, Guid? recordedOnNodeId, Guid thisNodeId,
        Guid? receivedFromNodeId = null, IReadOnlySet<Guid>? localFleet = null) =>
        provenance switch
        {
            null => Unknown,
            _ when IsReplicatedFromOutsideFleet(receivedFromNodeId, recordedOnNodeId, localFleet) =>
                ReplicatedFromOutsideFleet,
            { RunId: null } => NoRunNamed,
            _ when recordedOnNodeId is not { } node || node == Guid.Empty || thisNodeId == Guid.Empty =>
                AgentOnUnobservedNode,
            _ when recordedOnNodeId == thisNodeId => AgentOnThisNode,
            _ when localFleet?.Contains(recordedOnNodeId.Value) is true => AgentOnFleetNode,
            _ => AgentOnAnotherNode,
        };

    /// <summary>
    /// Whether a lesson that was replicated here is not wholly the local fleet's. Two nodes have to
    /// be in the fleet, not one: the node that delivered it, and the node it says it began on
    /// (<paramref name="recordedOnNodeId"/>, which for a replicated event is the origin the sender
    /// wrote). A catch-up answer serves a node's replicated events as well as its own, so a lesson a
    /// teammate wrote arrives from one of your own nodes looking local unless the origin is checked
    /// too. An origin nobody could read is not in any fleet. Never true for a lesson this node
    /// recorded itself (<paramref name="receivedFromNodeId"/> null).
    /// </summary>
    private static bool IsReplicatedFromOutsideFleet(
        Guid? receivedFromNodeId, Guid? recordedOnNodeId, IReadOnlySet<Guid>? localFleet) =>
        receivedFromNodeId is { } sender
        && (localFleet is null
            || !localFleet.Contains(sender)
            || recordedOnNodeId is not { } origin
            || !localFleet.Contains(origin));

    public bool Equals(LessonProvenanceMark? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;
}

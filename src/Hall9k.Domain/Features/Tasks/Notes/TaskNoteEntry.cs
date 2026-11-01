using Hall9k.Domain.Features.Tasks.Events;

namespace Hall9k.Domain.Features.Tasks.Notes;

/// <summary>
/// One note on a task's projection: the event as recorded, and what the projection read off its
/// headers. The trust rule reads these three node ids and nothing the note says about itself.
/// </summary>
/// <param name="Note">The recorded event.</param>
/// <param name="RecordedOnNodeId">The node the note began on (<c>EventRecordingNode.Of</c>), or null when nobody observed one.</param>
/// <param name="ReceivedFromNodeId">The verified sender (<c>ReplicatedSender.Of</c>): null for a note this node recorded, a node id or <see cref="Guid.Empty"/> for a replicated one.</param>
/// <param name="OriginNodeId">The node the sender claims the note began on (<c>ReplicatedSender.OriginOf</c>), kept so a replicated note can name its author the way a replicated handoff note does.</param>
public sealed record TaskNoteEntry(
    TaskNoteRecorded Note,
    Guid? RecordedOnNodeId,
    Guid? ReceivedFromNodeId,
    Guid? OriginNodeId)
{
    /// <summary>True when another node delivered this note to this one.</summary>
    public bool IsReplicated => ReceivedFromNodeId is not null;
}

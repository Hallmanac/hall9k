namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// The publication session the daemon committed to is running, and this is which process it is.
/// The pid is paired with the process's own start time so a restarted daemon can tell a live
/// session from a dead one whose pid the operating system has since handed to something else.
/// <para>
/// It is separate from <see cref="WorkItemPublicationDispatched"/> because the two record facts
/// observed at different moments, and the platform does not write down a fact it has not observed
/// yet (AGENTS.md). The dispatch is decided and committed first, so nothing can create a card
/// without the stream already saying a session was dispatched; the process only exists afterwards,
/// so it is recorded afterwards. RunDispatched and RunProcessStarted split for the same reason.
/// </para>
/// <para>
/// Which means a task can legitimately sit dispatched with no process recorded beside it: the
/// daemon died in the window between the two. Adoption reads that as what it is — a session that
/// may or may not exist and that nobody can now ask about — and says so rather than guessing at
/// either answer.
/// </para>
/// </summary>
public sealed record WorkItemPublicationSessionStarted(
    Guid Id,
    Guid SessionId,
    int ProcessId,
    DateTimeOffset ProcessStartedAt);

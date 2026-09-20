namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// One read of a project's orchestrator feed: the items, how far a drain may move the cursor,
/// and whether the scan stopped at its own cap with more still waiting.
/// </summary>
/// <param name="DrainableThroughSequence">
/// The highest global sequence a drain may move the cursor to — every event at or below it has
/// been considered once, including the ones the filter rejected: re-classifying those on every
/// sweep would make the feed's cost grow with the log rather than with what is new.
/// <para>
/// Not simply the highest sequence the read looked at. A global sequence is taken inside the
/// writing transaction and becomes visible only when that transaction commits, so a lower
/// sequence can appear after a higher one already has, and a cursor parked on the higher number
/// would never show the lower event at all. The read therefore stops this frontier at the newest
/// event old enough that no lower-numbered write can still be in flight
/// (<see cref="OrchestratorFeedSelection.SettlingWindow"/>) — items younger than that are printed
/// like any other, and simply come back on the next read.
/// </para>
/// </param>
/// <param name="ScanWasCapped">
/// True when the scan filled its own cap rather than running out of log: there may well be more
/// past this read, and a drain followed by another read is what gets it. "May" rather than "is",
/// because a log that happens to hold exactly the cap's worth of new events fills it and has
/// nothing after it — a distinction this read cannot make without a second query, and not one
/// worth asserting either way. Said out loud rather than left silent, because the alternative is
/// a first-ever drain on a long-lived project quietly looking like the whole story.
/// </param>
public sealed record OrchestratorFeedRead(
    IReadOnlyList<OrchestratorFeedItem> Items,
    long DrainableThroughSequence,
    bool ScanWasCapped);

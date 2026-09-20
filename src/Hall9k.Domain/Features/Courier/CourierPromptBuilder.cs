namespace Hall9k.Domain.Features.Courier;

/// <summary>
/// The courier's own prompt (idea 89471598, piece 3): small on purpose, unlike every other
/// dispatched session on this platform — no recipe, no AGENTS.md, no repository context at all.
/// A courier's whole job is carrying the feed items it was handed into one message and reporting
/// whether that landed, so its prompt is the items themselves, the delivery instruction its own
/// adapter composed, and nothing else — a few thousand tokens rather than the ordinary dispatch's
/// tens of thousands.
/// </summary>
public static class CourierPromptBuilder
{
    /// <summary>
    /// The exact last line a courier's final message must carry when the send landed. The daemon
    /// reads this off the session's own terminal result — never off whether the feed cursor
    /// happens to advance, since that is the daemon's own doing once this marker is seen, not a
    /// fact the session could observe about itself.
    /// </summary>
    public const string DeliveredMarker = "COURIER-OUTCOME: delivered";

    /// <summary>The prefix of the last line a courier's final message must carry when the send did not land, followed by a one-line reason.</summary>
    public const string FailedMarkerPrefix = "COURIER-OUTCOME: failed";

    /// <summary>
    /// Joined with a plain <c>\n</c> throughout, never <see cref="Environment.NewLine"/>: this
    /// text is written to a file and read by a CLI, not rendered through a console that cares
    /// about the platform's own line ending, and a golden test pinning this output has to read
    /// the identical bytes on Windows and on Unix (this project's own CI runs both).
    /// </summary>
    public static string Build(string projectName, IReadOnlyList<string> feedLines, string deliveryInstruction) =>
        string.Join(
            '\n',
            [
                $"# Deliver the {projectName} orchestrator feed",
                "",
                $"The following items are waiting, undrained, in {projectName}'s orchestrator feed. They are "
                    + "already in the exact order and grouping `h9k orchestrator feed --drain` itself prints, so "
                    + "deliver them as one message, verbatim, changing nothing:",
                "",
                .. feedLines,
                "",
                deliveryInstruction,
                "",
                $"End your final message with exactly one line: `{DeliveredMarker}` if the send above "
                    + $"succeeded, or `{FailedMarkerPrefix} - <one-line reason>` if it did not. Nothing you do "
                    + "here drains the feed yourself — the daemon reads this line and drains it on your behalf "
                    + "once it sees delivered. Do not run any h9k command. Do not read any file outside this "
                    + "prompt. End your turn immediately after that line.",
                "",
            ]);
}

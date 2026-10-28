using Hall9k.Domain.Features.Tasks;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// What closeout found on a pull request it was told to leave alone (task: while a task's interactive
/// flag is on, closeout reports what it finds and changes nothing until the human says go). One value
/// holds every finding at once, so the run's record can say "unchanged" by comparing two of them:
/// <see cref="CloseoutReport.Key"/> is the canonical form, and an event is appended only when the key
/// differs from the last one recorded.
/// <para>
/// Nothing here quotes a reviewer. Checks are named by their own check names, reviews by their urls,
/// threads by counts of the triage's dispositions, and the stacked parent by the platform's own
/// sentence about it, so anything that renders the report (the status row, the feed) carries no
/// stranger's words.
/// </para>
/// </summary>
/// <param name="FailingChecks">The checks failing on the head, by name; empty when none were.</param>
/// <param name="Conflicting">Whether GitHub reads the branch as conflicting with its base.</param>
/// <param name="ChangesRequestedReviews">The urls of the human changes-requested reviews on the head.</param>
/// <param name="StackedParent">The platform's sentence about a stacked parent that moved or merged, or null.</param>
/// <param name="MergeHeld">Whether a pre-approved merge is waiting for the human because the flag is on.</param>
/// <param name="Threads">What the read-only triage made of the unresolved threads, or null when none would buy a lap.</param>
public sealed record CloseoutReport(
    IReadOnlyList<string> FailingChecks,
    bool Conflicting,
    IReadOnlyList<string> ChangesRequestedReviews,
    string? StackedParent,
    bool MergeHeld,
    CloseoutThreadReport? Threads)
{
    /// <summary>A report with nothing in it: what a clear pull request reports.</summary>
    public static readonly CloseoutReport Nothing = new([], false, [], null, false, null);

    /// <summary>Whether there is nothing to report, which is also what clears an earlier report.</summary>
    public bool IsEmpty() =>
        FailingChecks.Count == 0 && !Conflicting && ChangesRequestedReviews.Count == 0
        && StackedParent is null && !MergeHeld && Threads is null;

    /// <summary>
    /// The canonical form two reports are compared by: order-insensitive for the lists, and carrying
    /// every field the row renders. The thread part includes the triage status and counts, so a
    /// triage landing is a change, and a poll that sees the same report again is not.
    /// </summary>
    public string Key() => string.Join(
        '\n',
        $"checks:{string.Join(',', FailingChecks.Order(StringComparer.Ordinal))}",
        $"conflicting:{Conflicting}",
        $"changes-requested:{string.Join(',', ChangesRequestedReviews.Order(StringComparer.Ordinal))}",
        $"stacked:{StackedParent}",
        $"merge-held:{MergeHeld}",
        $"threads:{Threads?.Key()}");
}

/// <summary>
/// The thread half of a <see cref="CloseoutReport"/>: how many threads would have bought a lap, where
/// the read-only triage of them stands, and the counts of its dispositions. The report's prose stays
/// in <see cref="ReportPath"/>; only counts, never a word of a thread or of the triage's reasoning,
/// reach the stream.
/// </summary>
/// <param name="Status">Running, Assessed, Failed (the session wrote no usable report), or PastCap (no session ran).</param>
/// <param name="ThreadCount">How many unresolved threads the triage covers.</param>
/// <param name="Fix">Threads the triage classified as worth a fix; zero until it is assessed.</param>
/// <param name="Decline">Threads the triage classified as decline; zero until it is assessed.</param>
/// <param name="Route">Threads the triage classified as route; zero until it is assessed.</param>
/// <param name="ReportPath">The file the owner reads the triage in, for an assessed triage.</param>
/// <param name="Reason">Why there is no triage, in the daemon's own words, for a failed or skipped one.</param>
public sealed record CloseoutThreadReport(
    FeedbackEvaluationStatus Status,
    int ThreadCount,
    int Fix,
    int Decline,
    int Route,
    string? ReportPath,
    string? Reason)
{
    /// <summary>The canonical form <see cref="CloseoutReport.Key"/> compares.</summary>
    public string Key() => $"{Status.Value}/{ThreadCount}/{Fix}/{Decline}/{Route}/{ReportPath}/{Reason}";
}

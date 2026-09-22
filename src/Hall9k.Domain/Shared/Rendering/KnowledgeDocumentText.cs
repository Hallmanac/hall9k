using System.Globalization;
using System.Text;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Shared.Rendering;

/// <summary>
/// The formatting rules the decisions document and the lessons document both apply, in one place
/// (idea d805fd8b, piece 2). The two files are read side by side and a reader should not have to
/// notice which one they are in, and every rule here carries a determinism contract: the same
/// records must render to the same bytes on every node holding them, whatever that node's
/// operating system, culture, or time zone.
/// <para>
/// Shared rather than spelled out once per slice, unlike <c>DecisionDecider</c>'s and
/// <c>LearningDecider</c>'s own scope check. That one is a rule each slice owns an opinion about;
/// these are presentation mechanics whose whole value is that the two copies cannot disagree, and
/// a second copy of an <see cref="CultureInfo.InvariantCulture"/> format string is exactly the
/// shape that drifts without any build or test failure to catch it.
/// </para>
/// </summary>
internal static class KnowledgeDocumentText
{
    /// <summary>
    /// One line of the document. Written with an explicit <c>\n</c> rather than through
    /// <see cref="StringBuilder.AppendLine()"/>, which appends <see cref="Environment.NewLine"/>
    /// and would make a Windows node's render differ from a Linux node's byte for byte on every
    /// single line.
    /// </summary>
    public static void Line(StringBuilder document, string text = "") => document.Append(text).Append('\n');

    /// <summary>
    /// A recorded statement as one markdown paragraph: whatever line endings the recording host
    /// used collapsed to <c>\n</c>, so the same statement renders identically on a node that did
    /// not record it.
    /// </summary>
    public static string Statement(string? statement) =>
        (statement ?? string.Empty).ReplaceLineEndings("\n").Trim();

    /// <summary>
    /// Recorded text that has to sit inside one markdown list item: the line endings collapse to
    /// spaces rather than to <c>\n</c>, because a newline in the middle of a <c>- Origin
    /// incident:</c> bullet ends the bullet and leaves the rest of the sentence rendering as a
    /// paragraph of its own. Nothing stops a recorded incident from spanning lines (the deciders
    /// trim a statement, they do not reflow it), so the document is what has to be robust to it.
    /// The same reduction <c>IdeaDocumentRenderer</c> applies to a reason it puts in frontmatter.
    /// </summary>
    public static string SingleLine(string? text) =>
        (text ?? string.Empty).ReplaceLineEndings(" ").Trim();

    /// <summary>UTC, invariant, second precision: neither the host's time zone nor its culture reaches the file.</summary>
    public static string Timestamp(DateTimeOffset moment) =>
        moment.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Where a record came from, stated only as far as it was observed (AGENTS.md, never guess at
    /// unobserved facts). A record naming no run was typed at a shell and says so; one recorded
    /// from a run names the run and how that run's attendance was read, including the case where
    /// nothing could be read either way, which is its own honest answer rather than a silence a
    /// reader would fill in as "a human wrote this".
    /// </summary>
    public static string Provenance(RecordedProvenance? provenance) => provenance switch
    {
        null => string.Empty,
        { RunId: { } runId } => $", from run {DomainId.Short(runId)} ({Attendance(provenance.Attendance)})",
        _ => ", at a shell, outside any run",
    };

    /// <summary>A plural that reads like English rather than "1 decision(s)".</summary>
    public static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    /// <summary>
    /// Ids in the one order every node derives the same way. Compared as their own hex text rather
    /// than through <see cref="Guid.CompareTo(Guid)"/>, whose byte-group ordering is a .NET
    /// implementation detail nobody reading the rendered file would predict.
    /// </summary>
    public static IEnumerable<Guid> Ordered(IEnumerable<Guid> ids) =>
        ids.OrderBy(id => id.ToString("N"), StringComparer.Ordinal);

    private static string Attendance(HumanAttendance attendance) =>
        attendance == HumanAttendance.Attended
            ? "human attended"
            : attendance == HumanAttendance.Unattended
                ? "unattended"
                : "attendance not observed";
}

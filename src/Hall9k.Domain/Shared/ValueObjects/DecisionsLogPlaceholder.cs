using System.Text.RegularExpressions;

namespace Hall9k.Domain.Shared.ValueObjects;

/// <summary>
/// The placeholder-numbering convention PLAN.md §16's v0 Decisions Log entries use while a
/// branch is in flight (Decisions Log #162). A branch writing its own entry has
/// no way to know the log's true next number without racing every other branch reading the same
/// tail, so it cites its own task's short id instead
/// — unique by construction (<c>DomainId.Short</c>) — and the mechanical pre-final-pass rebase
/// step assigns the real number once the branch is current with its base. Shared by
/// <c>DecisionsLogNumberingGuardTests</c> (recognizing the convention so it does not fail a
/// build over an entry correctly waiting for its real number) and
/// <c>DecisionsLogRenumberer</c> (producing and consuming it) so neither can drift from the
/// other's idea of what a placeholder looks like.
/// </summary>
public static class DecisionsLogPlaceholder
{
    private const string Prefix = "PLACEHOLDER-";

    /// <summary>A task short id: eight lowercase hex characters (<c>DomainId.Short</c>).</summary>
    public static readonly Regex ShortIdPattern = new("^[0-9a-f]{8}$", RegexOptions.Compiled);

    /// <summary>
    /// Matches a placeholder entry's heading at the start of a line, e.g.
    /// <c>PLACEHOLDER-6df5f975. **Title.**</c> — the same <c>N. **</c> shape a real entry uses,
    /// with the placeholder token standing in for the number.
    /// </summary>
    public static readonly Regex EntryHeadingPattern = new(@"^PLACEHOLDER-([0-9a-f]{8})\. \*\*", RegexOptions.Compiled);

    /// <summary>The placeholder token a branch cites for its own task, e.g. <c>PLACEHOLDER-6df5f975</c>.</summary>
    public static string TokenFor(string taskShortId)
    {
        if (!ShortIdPattern.IsMatch(taskShortId))
        {
            throw new ArgumentException(
                $"'{taskShortId}' is not an eight-character lowercase hex short id", nameof(taskShortId));
        }

        return $"{Prefix}{taskShortId}";
    }

    /// <summary>
    /// The citation form of a task's placeholder, e.g. <c>#PLACEHOLDER-00000000</c> for short id
    /// <c>00000000</c> — deliberately not this file's own task's real short id: a live citation
    /// of it here would itself match the pattern this very convention's own renumbering pass
    /// sweeps for, and get silently rewritten by it the first time this task's branch is rebased
    /// (independent pre-PR review, cycle 1, conformance lens).
    /// </summary>
    public static string CitationFor(string taskShortId) => $"#{TokenFor(taskShortId)}";
}

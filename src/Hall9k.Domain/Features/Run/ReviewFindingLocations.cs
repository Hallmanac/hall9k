namespace Hall9k.Domain.Features.Run;

/// <summary>
/// Whether two stated finding locations name the same place (Decisions Log #62). A location is
/// whatever the reviewer wrote in its finding header — `src/Some/File.cs:123` by contract, but
/// the same line is also written `./src/Some/File.cs:123`, `src\Some\File.cs:123`, and
/// `File.cs:123` — so comparing the strings as they arrived reports one defect as several. Two
/// things depend on getting this right: routing a defect once per run, and the residual tally a
/// human reads to decide how much to trust a settled pull request.
/// <para>
/// What this deliberately does NOT do is treat a <i>different</i> line in the same file as the
/// same place. A routed defect whose line shifts, because a fix session edited above it, comes
/// back as a second draft bug task, and that is the chosen error rather than an oversight: a
/// duplicate inert draft is visible and a human discards it in a moment, while collapsing by
/// file alone would silently swallow a second, genuinely different defect in a file this run
/// had already routed one from. Same file and same stated line is an observation; same file and
/// some other line is a guess, and the guess is the one that loses a defect. A file named with
/// no line at all is the same guess wearing a file name, so it is no more a place than a
/// finding the reviewer left unplaced entirely.
/// </para>
/// </summary>
public static class ReviewFindingLocations
{
    /// <summary>
    /// Whether two locations name the same place: the same stated line, in paths where one is a
    /// trailing run of segments of the other (`Legacy.cs:40` is the `src/Legacy.cs:40` a
    /// shorter-handed reviewer meant). A location with no stated line names nowhere and matches
    /// nothing, including another location naming the same file — a finding the reviewer never
    /// placed, whether it named no file or only a file, cannot be shown to be one already
    /// recorded, so it is treated as new rather than folded into a defect it may have nothing to
    /// do with. Two out-of-scope defects reported as `src/Legacy.cs` with no line are two
    /// defects until something says otherwise.
    /// </summary>
    public static bool SamePlace(string? left, string? right)
    {
        if (left.IsBlank() || right.IsBlank())
        {
            return false;
        }

        (string leftPath, string leftAnchor) = Split(left);
        (string rightPath, string rightAnchor) = Split(right);
        return leftAnchor.IsNotBlank()
            && string.Equals(leftAnchor, rightAnchor, StringComparison.OrdinalIgnoreCase)
            && SamePath(leftPath, rightPath);
    }

    /// <summary>
    /// A location split into the file it names and the anchor after it — the line, or a range,
    /// or a line and column, kept as the reviewer wrote it. The anchor starts at the first
    /// colon followed by a digit, which is what keeps a Windows drive letter (`C:/src/A.cs:9`)
    /// part of the path rather than read as a line number.
    /// </summary>
    private static (string Path, string Anchor) Split(string location)
    {
        string stated = location.Trim().Replace('\\', '/');
        int anchor = AnchorStart(stated);
        return anchor < 0
            ? (stated, string.Empty)
            : (stated[..anchor], stated[(anchor + 1)..].Trim());
    }

    private static int AnchorStart(string stated)
    {
        for (int index = stated.IndexOf(':'); index >= 0; index = stated.IndexOf(':', index + 1))
        {
            if (char.IsAsciiDigit(stated[(index + 1)..].TrimStart().FirstOrDefault()))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Whether one path is the other read from the end: every segment they share must match, so
    /// `src/Legacy.cs` is `Legacy.cs` more fully stated, while `tests/Legacy.cs` is not.
    /// </summary>
    private static bool SamePath(string left, string right)
    {
        string[] leftSegments = Segments(left);
        string[] rightSegments = Segments(right);
        if (leftSegments.Length == 0 || rightSegments.Length == 0)
        {
            return false;
        }

        int shared = Math.Min(leftSegments.Length, rightSegments.Length);
        for (int depth = 1; depth <= shared; depth++)
        {
            if (!string.Equals(leftSegments[^depth], rightSegments[^depth], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static string[] Segments(string path) =>
        [.. path
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(segment => segment != ".")];
}

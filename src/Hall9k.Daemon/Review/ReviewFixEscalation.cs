using Hall9k.Domain.Features.Run;

namespace Hall9k.Daemon.Review;

/// <summary>
/// Whether a fix session is dispatching over the same findings its immediately preceding fix
/// round already tried (task: a second fix round over the same findings). Origin (2026-08-25):
/// task 60 generation 2's Sonnet fix session responded to the UnixProcessManager.Spawn start-time
/// race by restructuring the flaky test rather than fixing Spawn — the gate caught it because
/// three sibling parity tests hit the same race, costing a full extra generation. Ruled with
/// Brian 2026-08-26: rather than moving every fix session to a stronger model (parked pending
/// post-chain measurement), escalate only this narrow recurring surface, where the dodge was
/// actually observed.
/// <para>
/// Conservative by design: a location has to be an observed match
/// (<see cref="ReviewFindingLocations.SamePlace"/>), or the human's own needs-fixes reason has to
/// literally contain a previous round's stated location, before this counts as a repeat. When in
/// doubt, this returns null — a false negative costs one more cheap round; a false positive
/// silently inflates quota burn on every ordinary review cycle that happens to touch a file twice
/// for unrelated reasons.
/// </para>
/// </summary>
public static class ReviewFixEscalation
{
    /// <summary>
    /// Why this round escalates, or null when it is either a first round (no previous round to
    /// repeat — <paramref name="previousLocations"/> empty) or a round over genuinely fresh
    /// findings. <paramref name="previousLocations"/> is the immediately preceding fix round's
    /// own finding locations, never the whole run's history: a round that clears the repeated
    /// defect and moves on to something new de-escalates on its own the very next time this is
    /// asked, with no separate reset step anywhere in the loop.
    /// <para>
    /// Two independent signals, either enough on its own: <paramref name="currentLocations"/>
    /// naming a place <see cref="ReviewFindingLocations.SamePlace"/> already matched last round
    /// (an automated reviewer still finding it), or <paramref name="humanFindings"/> — a human's
    /// <c>h9k review resolve --needs-fixes</c> reason — literally naming one of the previous
    /// round's own locations (a human restating what an automated round already tried and failed
    /// to clear, most often after a dispute or a capped-track park). The human check is a plain
    /// substring match rather than anything smarter: this codebase's other free-text "did this
    /// restate known content" checks (<c>ReviewVerdictValidation</c>) are already a long history
    /// of narrow, literal vocabulary rather than a semantic read, and the conservative default
    /// here is a missed restatement, never an invented one.
    /// </para>
    /// </summary>
    public static string? Reason(
        IReadOnlyList<string> previousLocations, IReadOnlyList<string> currentLocations, string? humanFindings)
    {
        if (previousLocations.Count == 0)
        {
            return null;
        }

        List<string> repeated = [.. currentLocations
            .Where(current => previousLocations.Any(previous => ReviewFindingLocations.SamePlace(previous, current)))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (repeated.Count > 0)
        {
            return "repeat round over the same findings as the previous fix round "
                + $"({string.Join(", ", repeated)})";
        }

        string? restated = humanFindings.IsNotBlank()
            ? previousLocations.FirstOrDefault(previous => ContainsLocation(humanFindings, previous))
            : null;
        return restated is null
            ? null
            : "repeat round — the human's needs-fixes verdict restates the previous fix round's "
                + $"finding at {restated}";
    }

    /// <summary>
    /// Whether <paramref name="text"/> literally names <paramref name="location"/> — a plain
    /// substring match, but bounded on both sides so <paramref name="location"/> can only match a
    /// whole path-and-anchor token in <paramref name="text"/>, never a fragment of a longer one.
    /// The right side rejects a following digit, so a shorter line number in
    /// <paramref name="location"/> cannot match as a prefix of a longer, unrelated one
    /// (<c>src/Auth.cs:4</c> must not match inside <c>src/Auth.cs:42</c>), and rejects a following
    /// <c>:</c>-then-digit, so a bare file with no stated line cannot match a more specific
    /// <c>file:line</c> naming a different place (<c>src/Foo.cs</c> must not match inside
    /// <c>src/Foo.cs:120</c> — <see cref="ReviewFindingLocations.SamePlace"/> already refuses that
    /// pair). The left side rejects a preceding path character, so <paramref name="location"/>
    /// cannot match as the tail of a longer, unrelated filename (<c>Engine.cs:512</c> must not
    /// match inside <c>ReviewEngine.cs:512</c> — those are different places by the same rule).
    /// </summary>
    private static bool ContainsLocation(string text, string location)
    {
        for (int start = 0; ; )
        {
            int index = text.IndexOf(location, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            if (IsBoundaryBefore(text, index) && IsBoundaryAfter(text, index + location.Length))
            {
                return true;
            }

            start = index + 1;
        }
    }

    private static bool IsBoundaryBefore(string text, int matchStart) =>
        matchStart == 0 || !IsPathCharacter(text[matchStart - 1]);

    private static bool IsBoundaryAfter(string text, int matchEnd)
    {
        if (matchEnd >= text.Length)
        {
            return true;
        }

        if (text[matchEnd] == ':' && matchEnd + 1 < text.Length && char.IsAsciiDigit(text[matchEnd + 1]))
        {
            return false;
        }

        return !IsPathCharacter(text[matchEnd]);
    }

    /// <summary>
    /// A character that continues a path or filename token rather than closing one off — letters,
    /// digits and the punctuation ordinary filenames use (<c>.</c>, <c>_</c>, <c>-</c>). A path
    /// separator (<c>/</c>) is deliberately not one of these: <see cref="ReviewFindingLocations.SamePlace"/>
    /// already treats a shorter path as the same place as a longer one it is a trailing run of, so
    /// matching right after a separator has to stay open.
    /// </summary>
    private static bool IsPathCharacter(char character) =>
        char.IsLetterOrDigit(character) || character is '.' or '_' or '-';
}

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
    /// substring match, but bounded on the right so a shorter line number stated in
    /// <paramref name="location"/> cannot match as a prefix of a longer, unrelated one already
    /// present in <paramref name="text"/> (<c>src/Auth.cs:4</c> must not match inside
    /// <c>src/Auth.cs:42</c>). No left boundary is needed: <paramref name="location"/> always
    /// starts mid-path or at a path separator, never mid-digit.
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

            int after = index + location.Length;
            if (after >= text.Length || !char.IsAsciiDigit(text[after]))
            {
                return true;
            }

            start = index + 1;
        }
    }
}

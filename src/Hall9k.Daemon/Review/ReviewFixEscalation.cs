using System.Text.RegularExpressions;
using Hall9k.Domain.Features.Run;

namespace Hall9k.Daemon.Review;

/// <summary>
/// Whether a fix session is dispatching over the same findings an earlier fix round already
/// tried (task: a second fix round over the same findings). Origin (2026-08-25):
/// task 60 generation 2's Sonnet fix session responded to the UnixProcessManager.Spawn start-time
/// race by restructuring the flaky test rather than fixing Spawn — the gate caught it because
/// three sibling parity tests hit the same race, costing a full extra generation. Ruled with
/// Brian 2026-08-26: rather than moving every fix session to a stronger model (parked pending
/// post-chain measurement), escalate only this narrow recurring surface, where the dodge was
/// actually observed.
/// <para>
/// Conservative by design: a location has to be an observed match
/// (<see cref="ReviewFindingLocations.SamePlace"/>), or the human's own needs-fixes reason has to
/// literally contain a previous round's stated location with defect language attributed to it,
/// before this counts as a repeat. When in doubt, this returns null — a false negative costs one
/// more cheap round; a false positive silently inflates quota burn on every ordinary review cycle
/// that happens to touch a file twice for unrelated reasons.
/// </para>
/// <para>
/// Cycle-5 review (independent pre-PR pass, conformance finding): a bare literal-substring match
/// of a previous location anywhere in the human's reason escalated even when that mention
/// explicitly dismissed the location ("`src/Auth.cs:42` is fine as written — the real gap is the
/// missing timeout in `src/Http.cs:88`."), because the only question asked was whether the string
/// appeared, never in what sense. The scan now requires <see cref="ReviewVerdictValidation.DefectLanguagePattern"/>
/// vocabulary to be attributed to the matched location specifically — nearer to it than to any
/// other location-shaped token in the same text — rather than merely present anywhere in the
/// reason (see <see cref="RestatesLocation"/>).
/// </para>
/// </summary>
public static partial class ReviewFixEscalation
{
    /// <summary>
    /// Why this round escalates, or null when it is either a first round (no previous round to
    /// repeat — <paramref name="previousLocations"/> empty) or a round over genuinely fresh
    /// findings. <paramref name="previousLocations"/> is an earlier fix round's own finding
    /// locations — usually the immediately preceding round, but a human-findings round dispatched
    /// in between leaves it unchanged rather than replacing it (see
    /// <see cref="RunAggregate.LastFixRoundFindingLocations"/>'s own doc for why), so it can lag
    /// by more than one round — never the whole run's history: a round that clears the repeated
    /// defect and moves on to something new de-escalates on its own the very next time this is
    /// asked, with no separate reset step anywhere in the loop.
    /// <para>
    /// Two independent signals, either enough on its own: <paramref name="currentLocations"/>
    /// naming a place <see cref="ReviewFindingLocations.SamePlace"/> already matched last round
    /// (an automated reviewer still finding it), or <paramref name="humanFindings"/> — a human's
    /// <c>h9k review resolve --needs-fixes</c> reason — literally naming one of the previous
    /// round's own locations (a human restating what an automated round already tried and failed
    /// to clear, most often after a dispute or a capped-track park). Only a previous location
    /// that <see cref="ReviewFindingLocations.HasAnchor"/> is a candidate for that scan, the same
    /// restriction the automated signal above gets for free through <c>SamePlace</c>: a lineless
    /// location names nowhere, so a human reason that happens to mention its bare file name is
    /// not evidence of anything being restated. The human check is a literal substring match for
    /// the location, plus a proximity read for whether defect language actually belongs to it
    /// (see <see cref="RestatesLocation"/>) — narrower than a semantic read, the same discipline
    /// this codebase's other free-text "did this restate known content" checks
    /// (<c>ReviewVerdictValidation</c>) already apply. The proximity read's actual bound: defect
    /// credit is only ever pulled away from the candidate location by a NEARER, DIFFERENT
    /// location in the same reason, so a reason naming a single location escalates on any defect
    /// vocabulary anywhere in it — a negation included ("X is not a defect; what is missing is
    /// test coverage" still reads as restating X, because no proximity or sentence-scope rule can
    /// separate a negation sitting in the same clause as the location it negates). The scan can
    /// therefore invent a restatement in that one shape; the cost is one review-model fix round,
    /// and the alternative — parsing negation — is a semantic read this check deliberately is not.
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
            return "repeat round over the same findings as an earlier fix round "
                + $"({string.Join(", ", repeated)})";
        }

        string? restated = humanFindings.IsNotBlank()
            ? previousLocations
                .Where(ReviewFindingLocations.HasAnchor)
                .FirstOrDefault(previous => RestatesLocation(humanFindings, previous))
            : null;
        return restated is null
            ? null
            : "repeat round — the human's needs-fixes verdict restates an earlier fix round's "
                + $"finding at {restated}";
    }

    /// <summary>
    /// Whether <paramref name="location"/> is both named in <paramref name="humanFindings"/> and
    /// has defect language attributed to it there, rather than merely mentioned (cycle-5 review):
    /// a human dismissing a previous round's location ("`src/Auth.cs:42` is fine as written — the
    /// real gap is the missing timeout in `src/Http.cs:88`.") names it without restating it, and a
    /// bare substring match cannot tell the two apart. This can, because the dismissal's own
    /// defect word ("missing") sits far closer to the different location it actually describes
    /// (`src/Http.cs:88`) than to the dismissed one — attribution is nearest-wins: for every
    /// <see cref="ReviewVerdictValidation.DefectLanguagePattern"/> match in the text, whichever
    /// location-shaped span is closest by character distance — an occurrence of
    /// <paramref name="location"/> itself, or any other location-shaped token
    /// <see cref="LooseLocationTokenPattern"/> finds — earns the credit, and a defect word
    /// strictly closer to a genuinely different location earns none for
    /// <paramref name="location"/>. A tie earns neither, the same conservative default as
    /// everywhere else in this file.
    /// <para>
    /// This still does not need a defect word to sit in the same sentence, let alone the same
    /// clause, as <paramref name="location"/> — "Still broken — fix `src/Auth.cs:42`." attributes
    /// "broken" to the location three words later across an em dash, exactly the shape a human
    /// restating a prior finding actually writes, and the only thing that would ever pull that
    /// credit away is a nearer, different location competing for it.
    /// </para>
    /// </summary>
    private static bool RestatesLocation(string humanFindings, string location)
    {
        List<(int Start, int End)> occurrences = [.. LocationOccurrences(humanFindings, location)
            .Select(start => (start, start + location.Length))];
        if (occurrences.Count == 0)
        {
            return false;
        }

        List<(int Start, int End)> otherLocations = [.. LooseLocationTokenPattern().Matches(humanFindings)
            .Select(match => (match.Index, match.Index + match.Length))
            .Where(span => !occurrences.Any(occurrence => Overlaps(span, occurrence)))];

        foreach (Match defect in ReviewVerdictValidation.DefectLanguagePattern().Matches(humanFindings))
        {
            int distanceToLocation = DistanceToNearestSpan(defect.Index, occurrences);
            int distanceToOtherLocation = otherLocations.Count == 0
                ? int.MaxValue
                : DistanceToNearestSpan(defect.Index, otherLocations);
            if (distanceToLocation < distanceToOtherLocation)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A loose, deliberately over-inclusive "something location-shaped" reader, used only to find
    /// competing locations a defect word might belong to instead of the candidate
    /// <see cref="RestatesLocation"/> is testing — never to decide whether two locations are the
    /// same place (<see cref="ReviewFindingLocations.SamePlace"/> already owns that). Over-matching
    /// here (crediting a bare symbol reference as "a location") only ever costs a missed
    /// escalation, the safe direction this whole file defaults to; under-matching would risk
    /// attributing a dismissal's defect language to the location it was dismissing, which is the
    /// false positive this check exists to close. A real extension needs at least two letters, the
    /// same floor <c>ReviewVerdictValidation.LocationPattern</c> uses, so an incidental "e.g." or
    /// "i.e." in a human's prose is not read as a competing location.
    /// </summary>
    [GeneratedRegex(@"[\w./\\-]+\.[A-Za-z]{2,10}(?::\d+(?:[:-]\d+)?)?")]
    private static partial Regex LooseLocationTokenPattern();

    /// <summary>
    /// How far <paramref name="position"/> sits from the nearest of <paramref name="spans"/> —
    /// zero when it falls inside one.
    /// </summary>
    private static int DistanceToNearestSpan(int position, IReadOnlyList<(int Start, int End)> spans)
    {
        int nearest = int.MaxValue;
        foreach ((int start, int end) in spans)
        {
            int distance = position >= start && position < end
                ? 0
                : Math.Min(Math.Abs(position - start), Math.Abs(position - end));
            nearest = Math.Min(nearest, distance);
        }

        return nearest;
    }

    private static bool Overlaps((int Start, int End) first, (int Start, int End) second) =>
        first.Start < second.End && second.Start < first.End;

    /// <summary>
    /// Every boundary-safe occurrence of <paramref name="location"/> in <paramref name="text"/> —
    /// a plain substring match, but bounded on both sides so <paramref name="location"/> can only
    /// match a whole path-and-anchor token in <paramref name="text"/>, never a fragment of a
    /// longer one, and yielding every match rather than stopping at the first so
    /// <see cref="RestatesLocation"/> can test proximity around each one. The right side rejects a
    /// following digit, so a shorter line number in <paramref name="location"/> cannot match as a
    /// prefix of a longer, unrelated one (<c>src/Auth.cs:4</c> must not match inside
    /// <c>src/Auth.cs:42</c>). It also rejects a following <c>:</c>-then-digit or
    /// <c>-</c>-then-digit, so a single stated line cannot match as a prefix of a more specific
    /// anchor naming a different place — a line-and-column (<c>src/Foo.cs:12</c> must not match
    /// inside <c>src/Foo.cs:12:34</c>) or a range (<c>src/Foo.cs:40</c> must not match inside
    /// <c>src/Foo.cs:40-52</c>) — <see cref="ReviewFindingLocations.SamePlace"/> already refuses
    /// both pairs. Only a digit — never <c>.</c>, <c>_</c> or a bare <c>-</c> — closes off the
    /// right side: every candidate here already passed
    /// <see cref="ReviewFindingLocations.HasAnchor"/>, so it always ends in the anchor's own
    /// digits, and a human's sentence ending right after it (<c>"fix src/Auth.cs:42."</c>) is a
    /// match, not a rejected fragment. The left side rejects a preceding path character, so
    /// <paramref name="location"/> cannot match as the tail of a longer, unrelated filename
    /// (<c>Engine.cs:512</c> must not match inside <c>ReviewEngine.cs:512</c> — those are
    /// different places by the same rule).
    /// </summary>
    private static IEnumerable<int> LocationOccurrences(string text, string location)
    {
        for (int start = 0; ; )
        {
            int index = text.IndexOf(location, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                yield break;
            }

            if (IsBoundaryBefore(text, index) && IsBoundaryAfter(text, index + location.Length))
            {
                yield return index;
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

        if (text[matchEnd] is ':' or '-' && matchEnd + 1 < text.Length && char.IsAsciiDigit(text[matchEnd + 1]))
        {
            return false;
        }

        return !char.IsAsciiDigit(text[matchEnd]);
    }

    /// <summary>
    /// A character that continues a path or filename token rather than closing one off — letters,
    /// digits and the punctuation ordinary filenames use (<c>.</c>, <c>_</c>, <c>-</c>). A path
    /// separator (<c>/</c>) is deliberately not one of these: <see cref="ReviewFindingLocations.SamePlace"/>
    /// already treats a shorter path as the same place as a longer one it is a trailing run of, so
    /// matching right after a separator has to stay open. Used only for the left boundary — the
    /// right boundary (<see cref="IsBoundaryAfter"/>) has its own, narrower rule.
    /// </summary>
    private static bool IsPathCharacter(char character) =>
        char.IsLetterOrDigit(character) || character is '.' or '_' or '-';
}

using System.Text.RegularExpressions;

namespace Hall9k.Domain.Features.Tasks.Notes;

/// <summary>A note the session chose to keep, with the claim it wrote for it.</summary>
/// <param name="NoteId">The offered note the line named.</param>
/// <param name="Claim">The text after the colon, trimmed and otherwise exactly as written; may be blank.</param>
public sealed record NoteKeepRequest(Guid NoteId, string Claim);

/// <summary>What the daemon made of a promotion session's answer.</summary>
/// <param name="Usable">False when any line was neither blank, NONE, nor a well-formed keep line, or when NONE shared the answer with another line, or when nothing was said at all.</param>
/// <param name="Keeps">The offered notes the answer named, in the answer's order, each once.</param>
/// <param name="IgnoredLineCount">How many keep lines named a note that was not offered, named more than one, or repeated one already read.</param>
public sealed record NotePromotionReading(bool Usable, IReadOnlyList<NoteKeepRequest> Keeps, int IgnoredLineCount)
{
    public static readonly NotePromotionReading NotUsable = new(false, [], 0);
}

/// <summary>
/// Reads a promotion session's answer, strictly and without keeping any of it beyond the claims. The
/// answer is NONE, or one or more lines of the form <c>KEEP &lt;id&gt;: &lt;claim&gt;</c>; blank lines are
/// ignored and any other line makes the whole answer unusable, because a chatty answer is one the
/// session did not follow the prompt for. A claim is never cut or edited beyond trimming.
/// </summary>
public static partial class NotePromotionAnswer
{
    /// <summary>The word that opens a keep line. Declared once here, injected into the prompt, and listed in <c>PromptContractTokens</c>.</summary>
    public const string KeepMarker = "KEEP";

    /// <summary>The whole answer when no note should be kept. Declared once here like <see cref="KeepMarker"/>.</summary>
    public const string NoneMarker = "NONE";

    [GeneratedRegex("^KEEP[ \\t]+([0-9a-fA-F]{8}):(.*)$")]
    private static partial Regex KeepLine();

    /// <summary>
    /// Reads <paramref name="answer"/> against <paramref name="offered"/>, the notes the session was
    /// shown. A keep line is ignored and counted when no offered note ends in its id, when two do
    /// (the short id then names no single note), or when it repeats an id already read.
    /// </summary>
    public static NotePromotionReading Read(string? answer, IReadOnlyList<OfferedNote> offered)
    {
        string[] lines =
        [
            .. (answer ?? string.Empty)
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0),
        ];
        if (lines.Length == 0)
        {
            return NotePromotionReading.NotUsable;
        }

        if (lines.Contains(NoneMarker, StringComparer.Ordinal))
        {
            return lines.Length == 1
                ? new NotePromotionReading(true, [], 0)
                : NotePromotionReading.NotUsable;
        }

        Match[] matches = [.. lines.Select(line => KeepLine().Match(line))];
        if (matches.Any(match => !match.Success))
        {
            return NotePromotionReading.NotUsable;
        }

        ILookup<string, OfferedNote> byShortId = offered.ToLookup(note => note.ShortId, StringComparer.OrdinalIgnoreCase);
        List<NoteKeepRequest> keeps = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        int ignored = 0;
        foreach (Match match in matches)
        {
            string id = match.Groups[1].Value;
            OfferedNote[] named = [.. byShortId[id]];
            if (!seen.Add(id) || named.Length != 1)
            {
                ignored++;
                continue;
            }

            keeps.Add(new NoteKeepRequest(named[0].NoteId, match.Groups[2].Value.Trim()));
        }

        return new NotePromotionReading(true, keeps, ignored);
    }
}

namespace Hall9k.Domain.Infrastructure;

/// <summary>
/// Orders two build version strings in <see cref="AssemblyInformationalVersion"/>'s own shape:
/// <c>major.minor.patch</c>, optionally followed by <c>-&lt;distance&gt;-g&lt;sha&gt;</c> (the
/// commit count and short hash <c>git describe</c> appends for a locally built install — see
/// <c>GitDescribedVersion</c>) and optionally <c>-dirty</c>, with any <c>+metadata</c> already
/// stripped. The numeric <c>major.minor.patch</c> decides first; when two versions share that
/// base, the commit distance decides, an exact-tag build (a release, or a local build with no
/// commits past the tag) reading as distance zero — this is what lets two different local builds
/// cut from the same tag, which share a numeric prefix but not a distance, compare as older and
/// newer rather than equal (independent pre-PR review, cycle 1, conformance lens: a give-up
/// marked on <c>0.10.32-2-gX</c> must lift once this node runs <c>0.10.32-6-gY</c>, not only once
/// the next tag is cut). A value either side cannot parse, or whose base matches but whose
/// distance also matches, never claims an order it cannot support: <see cref="IsOlderThan"/>
/// reads false rather than guessing.
/// </summary>
public static class BuildVersionOrdering
{
    public static bool IsOlderThan(string candidate, string than) =>
        TryParse(candidate, out ParsedVersion candidateVersion) && TryParse(than, out ParsedVersion thanVersion)
        && (candidateVersion.Base != thanVersion.Base
            ? candidateVersion.Base < thanVersion.Base
            : candidateVersion.Distance < thanVersion.Distance);

    private readonly record struct ParsedVersion(Version Base, int Distance);

    private static bool TryParse(string value, out ParsedVersion parsed)
    {
        parsed = default;
        string withoutDirty = value.EndsWith("-dirty", StringComparison.Ordinal)
            ? value[..^"-dirty".Length]
            : value;

        string[] parts = withoutDirty.Split('-', 3);
        if (!Version.TryParse(parts[0], out Version? baseVersion))
        {
            return false;
        }

        int distance = parts.Length == 3 && int.TryParse(parts[1], out int parsedDistance)
            && parts[2].StartsWith('g')
                ? parsedDistance
                : 0;
        parsed = new ParsedVersion(baseVersion, distance);
        return true;
    }
}

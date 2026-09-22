namespace Hall9k.Domain.Infrastructure;

/// <summary>
/// Orders two build version strings in <see cref="AssemblyInformationalVersion"/>'s own shape
/// (<c>major.minor.patch</c>, with an optional <c>-prerelease</c> suffix already stripped of any
/// <c>+metadata</c>) — numeric-only, since that is the whole of what this platform's own tags and
/// CI-stamped versions ever compare on. A value either side cannot parse never claims an order it
/// cannot support: <see cref="IsOlderThan"/> reads false rather than guessing.
/// </summary>
public static class BuildVersionOrdering
{
    public static bool IsOlderThan(string candidate, string than) =>
        TryParseNumericPrefix(candidate, out Version? candidateVersion)
        && TryParseNumericPrefix(than, out Version? thanVersion)
        && candidateVersion < thanVersion;

    private static bool TryParseNumericPrefix(string value, out Version? version)
    {
        string numericPart = value.Split('-', 2)[0];
        return Version.TryParse(numericPart, out version);
    }
}

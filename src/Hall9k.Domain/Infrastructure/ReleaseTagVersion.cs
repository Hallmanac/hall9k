namespace Hall9k.Domain.Infrastructure;

/// <summary>
/// Reads a GitHub release tag (<c>v0.13.1</c>) in <see cref="BuildVersionOrdering"/>'s own shape
/// (<c>0.13.1</c>), which has no leading <c>v</c>. The one place the platform compares a release
/// against the version a binary reports, so the daemon's notice, <c>h9k status</c>, and
/// <c>h9k doctor</c> never disagree about what is newer.
/// </summary>
public static class ReleaseTagVersion
{
    /// <summary>The tag without its leading <c>v</c>, as <see cref="BuildVersionOrdering"/> reads versions.</summary>
    public static string Bare(string tag) => tag.TrimStart('v');

    /// <summary>
    /// Whether <paramref name="tag"/> is a strictly newer release than <paramref name="runningVersion"/>.
    /// A local build past its tag (<c>0.13.0-5-gabc123</c>) is not behind the <c>0.13.0</c> release it
    /// was cut from, and a value either side cannot read is never newer, because
    /// <see cref="BuildVersionOrdering.IsOlderThan"/> claims no order it cannot support.
    /// </summary>
    public static bool IsNewerThan(string tag, string runningVersion) =>
        BuildVersionOrdering.IsOlderThan(runningVersion, Bare(tag));
}

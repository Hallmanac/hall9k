using Hall9k.Connectors.Processes;

namespace Hall9k.Connectors.Releases;

/// <summary>
/// One release check as the daemon's loop and <c>h9k doctor</c> see it: ask GitHub, now, what the
/// newest release for this machine is. A delegate so a test can answer without GitHub or git.
/// </summary>
public delegate Task<LatestReleaseResult> ReleaseCheck(CancellationToken cancellationToken);

/// <summary>The real <see cref="ReleaseCheck"/>: the shared lookup, against Hall9k's own repository, for this machine's platform.</summary>
public static class LiveReleaseCheck
{
    /// <summary>
    /// <paramref name="gh"/> is the ambient (no pinned account) runner, because the release comes
    /// from Hall9k's own repository, never a registered project's. <paramref name="workingDirectory"/>
    /// is where <c>gh</c> runs; <c>--repo</c> names the repository, so any existing directory serves.
    /// </summary>
    public static ReleaseCheck Create(ProcessRunner gh, string workingDirectory) =>
        cancellationToken => new LatestReleaseLookup(gh).ResolveForPlatformAsync(
            ReleasePlatform.DefaultRepository, ReleasePlatform.CurrentRid(), workingDirectory, cancellationToken);
}

using System.Runtime.InteropServices;

namespace Hall9k.Cli.Installation;

/// <summary>
/// The platform matrix backlog 42's release workflow builds and <c>h9k update</c> fetches
/// from: macOS arm64, Windows x64, and Linux x64 — the tower joins the shared-database future,
/// so Linux rides along rather than waiting for a release-engineering errand later. The RID
/// naming (<c>osx-arm64</c>, <c>win-x64</c>, <c>linux-x64</c>) matches .NET's own runtime
/// identifiers and, deliberately, the asset names <c>release.yml</c> publishes
/// (<c>hall9k-&lt;rid&gt;.tar.gz</c> / <c>.zip</c>), so this is the one place either side has to
/// agree with the other.
/// </summary>
public static class ReleasePlatform
{
    /// <summary>Where <c>h9k update</c> fetches releases from unless <c>--repo</c> overrides it.</summary>
    public const string DefaultRepository = "Hallmanac/hall9k";

    /// <summary>
    /// The current machine's release RID, or null when this platform is not one release.yml
    /// builds for — named rather than guessed, so the caller can refuse with the actual list.
    /// </summary>
    public static string? CurrentRid() => (RuntimeInformation.OSArchitecture, OperatingSystem.IsMacOS(), OperatingSystem.IsWindows(), OperatingSystem.IsLinux()) switch
    {
        (Architecture.Arm64, true, false, false) => "osx-arm64",
        (Architecture.X64, false, true, false) => "win-x64",
        (Architecture.X64, false, false, true) => "linux-x64",
        _ => null,
    };

    /// <summary>The archive extension release.yml packages this RID's assets with: a zip on
    /// Windows (no tar on a bare machine to unpack it with), a tarball everywhere else.</summary>
    public static string ArchiveExtension(string rid) => rid.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz";

    public static string ArchiveFileName(string rid) => $"hall9k-{rid}{ArchiveExtension(rid)}";
}

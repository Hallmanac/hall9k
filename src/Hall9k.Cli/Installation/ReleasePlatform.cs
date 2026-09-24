using System.Runtime.InteropServices;

namespace Hall9k.Cli.Installation;

/// <summary>
/// The platform matrix backlog 42's release workflow builds and <c>h9k update</c> fetches
/// from: macOS arm64, Windows x64, Windows arm64, and Linux x64 — the tower joins the
/// shared-database future, so Linux rides along rather than waiting for a release-engineering
/// errand later. The RID naming (<c>osx-arm64</c>, <c>win-x64</c>, <c>win-arm64</c>,
/// <c>linux-x64</c>) matches .NET's own runtime identifiers and, deliberately, the asset names
/// <c>release.yml</c> publishes (<c>hall9k-&lt;rid&gt;.tar.gz</c> / <c>.zip</c>), so this is the
/// one place either side has to agree with the other.
/// </summary>
public static class ReleasePlatform
{
    /// <summary>Where <c>h9k update</c> fetches releases from unless <c>--repo</c> overrides it.</summary>
    public const string DefaultRepository = "Hallmanac/hall9k";

    /// <summary>
    /// Every (architecture, OS) pair release.yml builds for, and the RID it publishes it under.
    /// The lookup and the refusal list <c>h9k update</c> prints both read this one table.
    /// </summary>
    private static readonly (Architecture Architecture, OSPlatform Os, string Rid)[] Platforms =
    [
        (Architecture.Arm64, OSPlatform.OSX, "osx-arm64"),
        (Architecture.X64, OSPlatform.Windows, "win-x64"),
        (Architecture.Arm64, OSPlatform.Windows, "win-arm64"),
        (Architecture.X64, OSPlatform.Linux, "linux-x64"),
    ];

    /// <summary>Every RID release.yml builds, in table order.</summary>
    public static IReadOnlyList<string> SupportedRids { get; } = [.. Platforms.Select(platform => platform.Rid)];

    /// <summary>
    /// The release RID for an (architecture, OS) pair, or null when release.yml builds nothing
    /// for it. A pure function of its arguments, so a test can ask about a machine other than
    /// the one it runs on.
    /// </summary>
    public static string? RidFor(Architecture architecture, OSPlatform os) => Platforms
        .FirstOrDefault(platform => platform.Architecture == architecture && platform.Os == os)
        .Rid;

    /// <summary>
    /// The current machine's release RID, or null when this platform is not one release.yml
    /// builds for — named rather than guessed, so the caller can refuse with the actual list.
    /// Keyed on <see cref="RuntimeInformation.OSArchitecture"/>, not the process architecture, so
    /// an x64 h9k emulated on ARM64 Windows fetches the win-arm64 release.
    /// </summary>
    public static string? CurrentRid() => RidFor(
        RuntimeInformation.OSArchitecture,
        Platforms.Select(platform => platform.Os).FirstOrDefault(RuntimeInformation.IsOSPlatform));

    /// <summary>The archive extension release.yml packages this RID's assets with: a zip on
    /// Windows (no tar on a bare machine to unpack it with), a tarball everywhere else.</summary>
    public static string ArchiveExtension(string rid) => rid.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz";

    public static string ArchiveFileName(string rid) => $"hall9k-{rid}{ArchiveExtension(rid)}";
}

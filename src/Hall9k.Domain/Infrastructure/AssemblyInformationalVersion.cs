using System.Reflection;

namespace Hall9k.Domain.Infrastructure;

/// <summary>
/// Reads the version identity actually embedded in an assembly at build time — shared by
/// <c>CliVersion</c> (h9k --version) and the daemon's own startup log line, since a locally
/// built install stamps both binaries with the same <c>-p:InformationalVersion</c> value
/// (<c>InstallCommand.ExecuteAsync</c>'s --repo branch) and a release payload stamps both
/// with <c>-p:Version</c> (release.yml) — either way, the SDK appends "+&lt;git sha&gt;"
/// build metadata to the informational version, which is stripped here rather than shown.
/// </summary>
public static class AssemblyInformationalVersion
{
    public static string Resolve(Assembly assembly)
    {
        // Deliberately degrade rather than throw: this can run on every process start, and a
        // packaging misconfiguration must not take h9k or h9kd down. CliVersionTests guards the
        // misconfiguration by rejecting this fallback value.
        string informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "0.0.0";

        int metadataStart = informational.IndexOf('+');
        return metadataStart < 0
            ? informational
            : informational[..metadataStart];
    }
}

using System.Reflection;

namespace Hall9k.Cli.Infrastructure;

public static class CliVersion
{
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        // Deliberately degrade rather than throw: this runs on every CLI invocation via
        // SetApplicationVersion, and a packaging misconfiguration must not take h9k down.
        // CliVersionTests guards the misconfiguration by rejecting the fallback value.
        string informational = typeof(CliVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "0.0.0";

        // The SDK appends "+<git sha>" build metadata to the informational version.
        int metadataStart = informational.IndexOf('+');
        return metadataStart < 0
            ? informational
            : informational[..metadataStart];
    }
}

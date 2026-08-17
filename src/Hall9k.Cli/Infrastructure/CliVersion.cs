using System.Reflection;

namespace Hall9k.Cli.Infrastructure;

public static class CliVersion
{
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
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

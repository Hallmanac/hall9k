using Hall9k.Domain.Infrastructure;

namespace Hall9k.Cli.Infrastructure;

public static class CliVersion
{
    public static string Current { get; } = AssemblyInformationalVersion.Resolve(typeof(CliVersion).Assembly);
}

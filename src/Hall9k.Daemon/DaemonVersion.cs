using System.Reflection;
using Hall9k.Domain.Infrastructure;

namespace Hall9k.Daemon;

/// <summary>The daemon's own counterpart to <c>Hall9k.Cli.Infrastructure.CliVersion</c> — read
/// from this assembly rather than the CLI's, so h9kd reports what h9kd itself was built from
/// even when h9k and h9kd were published from different checkouts.</summary>
public static class DaemonVersion
{
    public static string Current { get; } = AssemblyInformationalVersion.Resolve(Assembly.GetExecutingAssembly());
}

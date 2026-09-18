using Hall9k.Domain.Infrastructure.Persistence;

namespace Hall9k.Cli.Diagnostics;

/// <summary>
/// Where a doctor probe gets the connection string it is about to diagnose — the one seam that
/// decides which platform config file and which database a probe actually reads.
/// <para>
/// It exists because a probe that resolves its own connection string can only ever be exercised
/// against the machine's real one. <c>ToolDoctorTests</c>' unreachable-database case used to
/// arrange itself by setting <see cref="Hall9kDatabase.EnvironmentVariableName"/>, which is
/// process-wide: on 2026-09-17 11:15 EDT (run 01a0af3c) it failed with live rows from a reachable
/// local Postgres in the output it had captured, because process-wide state is not a test's to own
/// while the rest of the suite runs beside it. Handing the probe its source closes that for good —
/// under test, nothing anywhere in the resolution chain is consulted (PLAN.md §16 #220).
/// </para>
/// <para>
/// Three sources, one per way a connection string legitimately arrives: the process's own
/// precedence chain (<see cref="Process"/>, what every real invocation uses), one named config
/// file and nothing else (<see cref="PlatformConfigFile"/>), and a string the caller already holds
/// (<see cref="Configured"/>). Each defers to <see cref="Hall9kDatabase"/> for the answer rather
/// than assembling a <see cref="ConnectionStringResolution"/> of its own, so precedence and origin
/// stay defined in exactly one place.
/// </para>
/// </summary>
public sealed class ConnectionStringSource
{
    private readonly Func<ConnectionStringResolution> resolve;

    private ConnectionStringSource(Func<ConnectionStringResolution> resolve) => this.resolve = resolve;

    /// <summary>
    /// The full precedence chain <see cref="Hall9kDatabase.Resolve"/> documents — environment
    /// variable, platform config file, project override — which is what <c>h9k doctor</c> itself
    /// always wants, and the only source that reads the process environment or the user's home.
    /// </summary>
    public static ConnectionStringSource Process { get; } = new(() => Hall9kDatabase.Resolve());

    /// <summary>One named platform config file, read per <see cref="Hall9kDatabase.ResolveFromConfigFile"/>.</summary>
    public static ConnectionStringSource PlatformConfigFile(string configFilePath) =>
        new(() => Hall9kDatabase.ResolveFromConfigFile(configFilePath));

    /// <summary>
    /// A connection string the caller already has in hand, outranking every other tier exactly as
    /// the Aspire dev loop's own injected value does.
    /// </summary>
    public static ConnectionStringSource Configured(string connectionString) =>
        new(() => Hall9kDatabase.Resolve(connectionString));

    /// <summary>
    /// The general form the three above are conveniences over, for a caller that has to observe
    /// the resolution happening rather than only its answer — a test proving the doctor asked its
    /// injected source, and asked nothing else.
    /// </summary>
    public static ConnectionStringSource From(Func<ConnectionStringResolution> resolve) => new(resolve);

    /// <summary>What this source says the connection string is, and where it came from.</summary>
    public ConnectionStringResolution Resolve() => resolve();
}

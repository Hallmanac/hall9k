namespace Hall9k.Domain.Infrastructure.Persistence;

/// <summary>
/// Where a resolved connection string came from — printed in every doctor answer so a
/// reader knows which of the three homes (Decisions Log #73) to go fix, rather than
/// being told only that something is wrong.
/// </summary>
public enum ConnectionStringOrigin
{
    /// <summary>Nothing was configured anywhere in the precedence chain.</summary>
    None,

    /// <summary>Passed in by the caller (Aspire's dev-loop wiring), outranking everything else.</summary>
    Configured,

    /// <summary><see cref="Hall9kDatabase.EnvironmentVariableName"/>.</summary>
    EnvironmentVariable,

    /// <summary>
    /// <see cref="Hall9kDatabase.ConnectionStringOverrideForTests"/>, a flow-scoped override only
    /// a test can open (<c>Hall9k.Tests.TestSupport.ScopedTestHome</c>/<c>ScopedConnectionString</c>)
    /// — distinct from <see cref="EnvironmentVariable"/> because no environment variable was
    /// actually read to produce this value, and a diagnostic naming one would send a reader
    /// hunting for a variable that was never consulted. No production code ever sets this override,
    /// so this origin is never observed outside the test project.
    /// </summary>
    TestOverride,

    /// <summary><see cref="Hall9kDatabase.ConfigFile"/>.</summary>
    PlatformConfigFile,

    /// <summary>
    /// <see cref="Hall9kDatabase.ConfigFile"/> exists but is not valid JSON — distinct from
    /// <see cref="None"/> because the fix is repairing the file, not configuring a fresh one.
    /// </summary>
    PlatformConfigFileMalformed,

    /// <summary>
    /// <see cref="Hall9kDatabase.ConfigFile"/> exists and may well be valid JSON, but this process
    /// could not read it (permissions dropped by another account, an exclusive lock held
    /// elsewhere) — distinct from <see cref="PlatformConfigFileMalformed"/> because the remedy is
    /// fixing access to the file, not its syntax; reporting "not valid JSON" here would send an
    /// operator hunting for a typo in a file that has none.
    /// </summary>
    PlatformConfigFileUnreadable,

    /// <summary>A <see cref="Hall9kDatabase.ProjectOverrideFileName"/> found walking up from the working directory.</summary>
    ProjectOverride,
}

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

    /// <summary><see cref="Hall9kDatabase.ConfigFile"/>.</summary>
    PlatformConfigFile,

    /// <summary>
    /// <see cref="Hall9kDatabase.ConfigFile"/> exists but is not valid JSON — distinct from
    /// <see cref="None"/> because the fix is repairing the file, not configuring a fresh one.
    /// </summary>
    PlatformConfigFileMalformed,

    /// <summary>A <see cref="Hall9kDatabase.ProjectOverrideFileName"/> found walking up from the working directory.</summary>
    ProjectOverride,
}

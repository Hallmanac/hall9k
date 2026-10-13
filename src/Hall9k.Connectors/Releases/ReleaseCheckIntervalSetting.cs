using Hall9k.Domain.Infrastructure.Persistence;

namespace Hall9k.Connectors.Releases;

/// <summary>How the daemon's release loop asks how long to wait before its next check, so a test can supply any answer without a config file.</summary>
public delegate Task<TimeSpan> ReleaseCheckIntervalReader(CancellationToken cancellationToken);

/// <summary>
/// The one place the node's <see cref="OperatingSettings.ReleaseCheckIntervalHours"/> is turned
/// into a wait (idea 93c1d24d, card 2), read fresh from the config file the way
/// <see cref="ReleaseChannelSetting"/> is, so <c>h9k config set --release-check-interval-hours</c>
/// needs no daemon restart. Nothing here throws: a file that cannot be read or used leaves the
/// default, because a daemon that cannot parse its own settings must not stop checking for releases
/// (or start checking constantly).
/// </summary>
public static class ReleaseCheckIntervalSetting
{
    /// <summary>Reads the config file afresh and resolves the wait between two checks.</summary>
    public static async Task<TimeSpan> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(Hall9kDatabase.ConfigFile))
        {
            return Interpret(null);
        }

        ConfigFileReadResult read;
        try
        {
            read = await PlatformConfigFile.TryReadOperatingSettingsAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Interpret(null);
        }

        return read.Problem switch
        {
            { Consequence: ConfigFileProblemConsequence.DaemonSkipsFile or ConfigFileProblemConsequence.DaemonFailsToStart } =>
                Interpret(null),
            { } problem when problem.Message.Contains(nameof(OperatingSettings.ReleaseCheckIntervalHours), StringComparison.OrdinalIgnoreCase) =>
                Interpret(null),
            _ => Interpret(read.Settings.ReleaseCheckIntervalHours),
        };
    }

    /// <summary>
    /// The wait a configured value means: absent is the six-hour default, anything below one hour is
    /// one hour, and anything above a year is a year, so a hand-edited value can neither hammer GitHub
    /// nor overflow a <see cref="TimeSpan"/>. Pure, so <c>h9k config show</c> reports exactly what the
    /// loop acts on.
    /// </summary>
    public static TimeSpan Interpret(int? configuredHours) => TimeSpan.FromHours(
        Math.Clamp(
            configuredHours ?? OperatingSettings.DefaultReleaseCheckIntervalHours,
            OperatingSettings.MinimumReleaseCheckIntervalHours,
            OperatingSettings.MaximumReleaseCheckIntervalHours));
}

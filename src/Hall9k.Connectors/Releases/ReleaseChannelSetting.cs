using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Connectors.Releases;

/// <summary>
/// The channel the release lookup resolved, and the one-line warning to print or log when it had
/// to fall back to <see cref="ReleaseChannel.Cleared"/> because the setting could not be used.
/// </summary>
public sealed record ReleaseChannelResolution(ReleaseChannel Channel, string? Warning);

/// <summary>How <see cref="LatestReleaseLookup"/> asks for the node's channel, so a test can supply one without a config file.</summary>
public delegate Task<ReleaseChannelResolution> ReleaseChannelReader(CancellationToken cancellationToken);

/// <summary>
/// The one place the node's <see cref="OperatingSettings.ReleaseChannel"/> is turned into a
/// <see cref="ReleaseChannel"/> (Decisions Log 8c039759: read only in the shared lookup, so
/// <c>h9k update</c> and the release notice never disagree on a node). Cleared is the failure
/// direction on purpose: a node that cannot say what it opted into sees fewer releases, never
/// more. Nothing here throws, so an unusable config file can neither fail <c>h9k update</c> nor
/// stop the daemon from starting.
/// </summary>
public static class ReleaseChannelSetting
{
    /// <summary>Reads the config file afresh and resolves the channel, falling back to cleared with a warning when it cannot.</summary>
    public static async Task<ReleaseChannelResolution> ReadAsync(CancellationToken cancellationToken)
    {
        string configFile = Hall9kDatabase.ConfigFile;
        if (!File.Exists(configFile))
        {
            return Fallback($"no config file at {configFile}");
        }

        ConfigFileReadResult read;
        try
        {
            read = await PlatformConfigFile.TryReadOperatingSettingsAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Fallback($"could not read {configFile} ({exception.Message})");
        }

        return read.Problem switch
        {
            { Consequence: ConfigFileProblemConsequence.DaemonSkipsFile or ConfigFileProblemConsequence.DaemonFailsToStart } problem =>
                Fallback($"could not use {configFile} ({OneLine(problem.Message)})"),
            { } problem when problem.Message.Contains(nameof(OperatingSettings.ReleaseChannel), StringComparison.OrdinalIgnoreCase) =>
                Fallback($"the releaseChannel value in {configFile} is malformed ({OneLine(problem.Message)})"),
            _ => Interpret(read.Settings.ReleaseChannel, configFile),
        };
    }

    /// <summary>
    /// The channel a configured value means: absent is the cleared default, a recognized word is
    /// itself, anything else is cleared with a warning. Pure, so <c>h9k config show</c> reports
    /// exactly what the lookup would act on.
    /// </summary>
    public static ReleaseChannelResolution Interpret(string? configuredValue, string configFile)
    {
        if (configuredValue is null)
        {
            return new ReleaseChannelResolution(ReleaseChannel.Cleared, null);
        }

        ReleaseChannel channel = ReleaseChannel.FromInput(configuredValue);
        return channel.IsWellFormed
            ? new ReleaseChannelResolution(channel, null)
            : Fallback($"'{configuredValue}' in {configFile} is not cleared or all");
    }

    private static ReleaseChannelResolution Fallback(string reason) =>
        new(ReleaseChannel.Cleared, $"Release channel: {reason}; using cleared.");

    private static string OneLine(string message) => message.ReplaceLineEndings(" ").Trim();
}

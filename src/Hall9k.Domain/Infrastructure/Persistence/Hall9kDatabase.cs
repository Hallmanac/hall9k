using System.Text.Json;
using System.Text.Json.Serialization;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Domain.Infrastructure.Persistence;

/// <summary>
/// Where Hall9k's Postgres connection string comes from (Decisions Log #57, #73;
/// resolves §15 row 29). Hall9k requires a connection string and takes no position on
/// where Postgres runs, so there is no default to fall back to — an unconfigured install
/// resolves to <see cref="ConnectionStringResolution.NotConfigured"/> and the doctor
/// check (<c>h9k doctor</c>, and the check any other command runs when it hits an
/// unreachable database) is what teaches the fix, never a plausible-looking guess.
/// <para>
/// Precedence, highest first: the <paramref name="configured"/> parameter (Aspire's
/// dev-loop wiring, which injects a connection string directly rather than through any
/// of the homes below); the <see cref="EnvironmentVariableName"/> environment variable
/// (this shell, this invocation — the same mechanism <c>DaemonEnvironment</c> captures
/// for a launchd-started daemon); the platform config file (<see cref="ConfigFile"/>, a
/// durable per-machine setting written by <c>h9k doctor</c>'s start-offer or by hand);
/// and last, a per-project override file (<see cref="ProjectOverrideFileName"/>, found by
/// walking up from the working directory). The override is checked last, deliberately:
/// it is the one entry in this chain that can arrive already sitting in a repository
/// checkout, and a file nobody meant to commit should never silently outrank a
/// connection string the operator configured on purpose.
/// </para>
/// </summary>
public static class Hall9kDatabase
{
    /// <summary>
    /// What <c>h9k install</c>'s shipped Postgres definition (<see cref="PostgresRuntime"/>)
    /// stands up, and what the doctor's start-offer writes to <see cref="ConfigFile"/> when
    /// it provisions a fresh install. Never used as a silent fallback — only ever reached by
    /// an explicit, recorded configuration act.
    /// </summary>
    public const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=hall9k;Username=postgres;Password=hall9k";

    public const string EnvironmentVariableName = "HALL9K_CONNECTION_STRING";

    /// <summary>The platform config file: a durable per-machine setting under <c>~/.hall9k</c>.</summary>
    public static string ConfigFile => Path.Combine(PlatformPaths.Home, "config.json");

    /// <summary>
    /// A single-line file at a project's repository root naming its own connection string —
    /// discovered by walking up from the working directory the same way <c>h9k install</c>
    /// finds <c>Hall9k.slnx</c>. Filesystem-based rather than looked up by Hall9k's own
    /// registered Project, because resolving a registered project needs a database
    /// connection already, which is exactly what this file exists to supply.
    /// </summary>
    public const string ProjectOverrideFileName = ".hall9k-connection";

    /// <summary>
    /// Resolve the connection string this process should use, per the precedence documented
    /// on the type. <paramref name="configured"/> is the Aspire (or other explicit-config)
    /// value, when the caller has one; <paramref name="startDirectory"/> is where the
    /// per-project override search begins (default: the current directory).
    /// </summary>
    public static ConnectionStringResolution Resolve(string? configured = null, string? startDirectory = null)
    {
        if (configured is { Length: > 0 })
        {
            return new ConnectionStringResolution(configured, ConnectionStringOrigin.Configured, null);
        }

        if (Environment.GetEnvironmentVariable(EnvironmentVariableName) is { Length: > 0 } fromEnvironment)
        {
            return new ConnectionStringResolution(
                fromEnvironment, ConnectionStringOrigin.EnvironmentVariable, EnvironmentVariableName);
        }

        if (ReadConfigFile() is { Length: > 0 } fromConfigFile)
        {
            return new ConnectionStringResolution(fromConfigFile, ConnectionStringOrigin.PlatformConfigFile, ConfigFile);
        }

        if (FindProjectOverride(startDirectory ?? Directory.GetCurrentDirectory()) is { } projectOverride)
        {
            return new ConnectionStringResolution(
                projectOverride.Value, ConnectionStringOrigin.ProjectOverride, projectOverride.Path);
        }

        return ConnectionStringResolution.NotConfigured;
    }

    /// <summary>
    /// Write the connection string to the platform config file — the doctor's own action when
    /// an operator accepts the start-offer on a previously unconfigured install. Merges rather
    /// than overwrites, so a future key added to this file survives a connection-string write.
    /// </summary>
    public static async Task WriteConfiguredConnectionStringAsync(string connectionString, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(PlatformPaths.Home);
        PlatformConfigDocument document = File.Exists(ConfigFile)
            ? JsonSerializer.Deserialize<PlatformConfigDocument>(
                await File.ReadAllTextAsync(ConfigFile, cancellationToken), SerializerOptions) ?? new()
            : new();
        document.ConnectionString = connectionString;

        await File.WriteAllTextAsync(
            ConfigFile, JsonSerializer.Serialize(document, SerializerOptions), cancellationToken);
    }

    private static string? ReadConfigFile()
    {
        if (!File.Exists(ConfigFile))
        {
            return null;
        }

        try
        {
            PlatformConfigDocument? document =
                JsonSerializer.Deserialize<PlatformConfigDocument>(File.ReadAllText(ConfigFile), SerializerOptions);
            return document?.ConnectionString?.Trim() is { Length: > 0 } value ? value : null;
        }
        catch (JsonException)
        {
            // An unreadable config file is not this method's refusal to compose — the doctor
            // check surfaces malformed configuration honestly rather than this call silently
            // falling through to the next precedence tier as though nothing were configured.
            return null;
        }
    }

    /// <summary>Walk up from <paramref name="startDirectory"/> for a project override file, the way <c>h9k install</c> finds <c>Hall9k.slnx</c>.</summary>
    private static (string Value, string Path)? FindProjectOverride(string startDirectory)
    {
        DirectoryInfo? candidate = new(Path.GetFullPath(startDirectory));
        while (candidate is not null)
        {
            string path = Path.Combine(candidate.FullName, ProjectOverrideFileName);
            if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } value)
            {
                return (value, path);
            }

            candidate = candidate.Parent;
        }

        return null;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    /// <summary>
    /// One key of a config file this type does not own alone — <see cref="JsonExtensionData"/>
    /// round-trips whatever else is in the file, which is what makes a connection-string
    /// write a merge rather than an overwrite.
    /// </summary>
    private sealed class PlatformConfigDocument
    {
        [JsonPropertyName("connectionString")]
        public string? ConnectionString { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }
}

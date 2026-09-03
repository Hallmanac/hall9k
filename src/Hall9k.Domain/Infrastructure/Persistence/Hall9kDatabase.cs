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
/// durable per-machine setting written by <c>h9k doctor</c>'s start-offer, by <c>h9k
/// install</c> itself when nothing resolves yet (Decisions Log #118), or by hand);
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
    /// stands up. Written to <see cref="ConfigFile"/> by <c>h9k doctor</c>'s start-offer once
    /// an operator accepts it, and — since Decisions Log #118 — by <c>h9k install</c> itself,
    /// non-interactively, but only when nothing in the precedence chain resolves yet and
    /// nothing is already listening on the default port: the compose file install just wrote
    /// fully determines this string, so that write is a record of what install already
    /// provisioned rather than a guess. <see cref="Resolve"/> never reaches this constant on
    /// its own — every write of it is one of those two explicit, recorded acts.
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

        string? fromConfigFile = ReadConfigFile(out bool configFileMalformed, out bool configFileUnreadable);
        if (fromConfigFile is { Length: > 0 })
        {
            return new ConnectionStringResolution(fromConfigFile, ConnectionStringOrigin.PlatformConfigFile, ConfigFile);
        }

        if (configFileMalformed)
        {
            // A broken config file is not "nothing configured" — the fix is repairing
            // config.json, not writing a fresh connection string over the top of it, so this
            // stops here rather than falling through to the project override tier as though
            // the platform config file had never been touched.
            return new ConnectionStringResolution(null, ConnectionStringOrigin.PlatformConfigFileMalformed, ConfigFile);
        }

        if (configFileUnreadable)
        {
            // Distinct from the malformed case above: the file may be perfectly valid JSON that
            // this process simply could not open, so the remedy is access, not syntax, and
            // reporting it as "not valid JSON" would send an operator hunting for a typo that
            // is not there.
            return new ConnectionStringResolution(null, ConnectionStringOrigin.PlatformConfigFileUnreadable, ConfigFile);
        }

        if (FindProjectOverride(startDirectory ?? Directory.GetCurrentDirectory()) is { } projectOverride)
        {
            return new ConnectionStringResolution(
                projectOverride.Value, ConnectionStringOrigin.ProjectOverride, projectOverride.Path);
        }

        return ConnectionStringResolution.NotConfigured;
    }

    /// <summary>
    /// The connection string sitting in the platform config file — ignoring the
    /// higher-precedence environment variable that <see cref="Resolve"/> would return instead
    /// when it is set, which is the value an autostarted daemon (no shell, so no environment
    /// variable of its own) would actually resolve to — together with which of missing,
    /// malformed, unreadable, present-without-the-key, or supplied it is (each wants its own
    /// remedy text, cycle-6 review), so a caller writing a warning needs to tell them apart
    /// rather than reporting all four as "does not exist". Reads state and value in a single pass
    /// rather than as two separate calls, which would risk the file changing in between (a
    /// concurrent <c>h9k config set</c>, an editor save) and leaving the two answers disagreeing
    /// with each other (cycle-6 review).
    /// </summary>
    public static (ConfigFileConnectionStringState State, string? Value) ConnectionStringStateAndValueInConfigFile()
    {
        if (!File.Exists(ConfigFile))
        {
            return (ConfigFileConnectionStringState.Missing, null);
        }

        string? value = ReadConfigFile(out bool malformed, out bool unreadable);
        if (malformed)
        {
            return (ConfigFileConnectionStringState.Malformed, null);
        }

        if (unreadable)
        {
            return (ConfigFileConnectionStringState.Unreadable, null);
        }

        return value is { Length: > 0 }
            ? (ConfigFileConnectionStringState.Supplied, value)
            : (ConfigFileConnectionStringState.PresentWithoutConnectionString, null);
    }

    /// <summary>
    /// Write the connection string to the platform config file — the doctor's own action when
    /// an operator accepts the start-offer on a previously unconfigured install, and (Decisions
    /// Log #118) <c>h9k install</c>'s own action when nothing resolves yet and nothing is already
    /// listening on the default port. Merges rather than overwrites, so a future key added to
    /// this file survives a connection-string write.
    /// </summary>
    public static async Task WriteConfiguredConnectionStringAsync(string connectionString, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(PlatformPaths.Home);
        PlatformConfigDocument document = await ReadExistingDocumentAsync(cancellationToken);
        document.ConnectionString = connectionString;

        await AtomicFileWrite.WriteAllTextAsync(
            ConfigFile, JsonSerializer.Serialize(document, SerializerOptions), cancellationToken);
    }

    /// <summary>
    /// This is the doctor's own write, run only after an operator has explicitly accepted the
    /// start-offer — a config file broken beyond parsing is not a reason to crash mid-fix.
    /// Whatever else was in it is unrecoverable anyway (it could not be parsed), so a malformed
    /// file starts fresh rather than taking the write down with the read. A file that exists but
    /// cannot be read at all is a different case and is deliberately not caught here: unlike
    /// invalid JSON, an unreadable file's other keys (the operating-settings section among them)
    /// are not lost, just momentarily inaccessible, so collapsing it into "start fresh" would let
    /// the write silently overwrite them the moment the read failure clears. <see
    /// cref="IOException"/>/<see cref="UnauthorizedAccessException"/> propagate instead, exactly
    /// as they did before this file could tell "unreadable" apart from "malformed" at all
    /// (cycle-1 adversarial finding, `Hall9kDatabase.cs:185`).
    /// </summary>
    private static async Task<PlatformConfigDocument> ReadExistingDocumentAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ConfigFile))
        {
            return new();
        }

        try
        {
            return JsonSerializer.Deserialize<PlatformConfigDocument>(
                await File.ReadAllTextAsync(ConfigFile, cancellationToken), SerializerOptions) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    /// <summary>
    /// <paramref name="malformed"/> and <paramref name="unreadable"/> are each true only when the
    /// file exists but could not be read as a connection string, and name two different causes
    /// rather than one: <paramref name="malformed"/> for invalid JSON, <paramref name="unreadable"/>
    /// for an <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/> reading it (an
    /// existing file this process simply could not open, e.g. permissions dropped by another
    /// account or an exclusive lock held elsewhere) — kept apart because they call for different
    /// remedies (fix the JSON vs. fix access to the file), and both distinct from "absent", so a
    /// caller can tell "nothing configured" apart from "something is configured, but this file is
    /// broken". <see cref="PlatformConfigFile"/>'s own <c>ReadDocumentAsync</c> and
    /// <c>Hall9k.Daemon.PlatformConfigFileSource.Insert</c> guard two other readers of this file
    /// the identical way, distinguishing the same two causes rather than collapsing them. A third
    /// reader, <see cref="ReadExistingDocumentAsync"/> just above — called by
    /// <see cref="WriteConfiguredConnectionStringAsync"/>, both from the doctor's own start-offer
    /// write and from <c>h9k install</c>'s unconfigured-machine write (Decisions Log #118) — keeps
    /// the same distinction rather than collapsing it: a malformed file still starts fresh (its
    /// content is unrecoverable either way), but an unreadable one propagates the read exception
    /// instead of silently overwriting keys that are only momentarily inaccessible, not actually
    /// lost (cycle-1 adversarial finding, `Hall9kDatabase.cs:185`).
    /// </summary>
    private static string? ReadConfigFile(out bool malformed, out bool unreadable)
    {
        malformed = false;
        unreadable = false;
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
            malformed = true;
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            unreadable = true;
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

    // The exact leniency PlatformConfigFile.LenientDocumentOptions parses this same file with
    // (comments skipped, trailing commas allowed): a file h9k config show and h9k daemon status
    // call healthy must never fail this connection-string read, or a fresh install with a
    // commented config.json reports PlatformConfigFileMalformed and every database command —
    // including an autostarted daemon — dies with "No Hall9k connection string is configured."
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

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

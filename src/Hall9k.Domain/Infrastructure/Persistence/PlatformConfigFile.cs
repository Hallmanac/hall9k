using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Infrastructure.Persistence;

/// <summary>
/// Reads and writes the "hall9k" section of the platform config file — <see
/// cref="Hall9kDatabase.ConfigFile"/>, the same file the connection-string chain already reads,
/// carrying the daemon's durable operating settings (backlog 59) alongside the
/// <c>connectionString</c> key <see cref="Hall9kDatabase"/> owns. A whole-document
/// <see cref="JsonObject"/> merge, not a fixed round-trip through one POCO, so a write here can
/// never disturb <c>connectionString</c> or any other top-level key already in the file, the same
/// way <see cref="Hall9kDatabase.WriteConfiguredConnectionStringAsync"/> never disturbs this one.
/// </summary>
public static class PlatformConfigFile
{
    private const string SectionName = "hall9k";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // The daemon never sees this document through System.Text.Json: it binds the identical
        // "hall9k" section through Microsoft.Extensions.Configuration, where every JSON leaf is
        // already a string and ConfigurationBinder converts "4" and 4 identically. Without this,
        // a hand-quoted number (the single likeliest hand-edit mistake) is a value the daemon
        // runs on happily but this type rejects as malformed — the CLI would report a healthy,
        // in-force file as broken and ignored.
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// The effective operating settings this section holds, or every field unset when the
    /// section (or the file itself) does not exist yet — never a guessed value.
    /// </summary>
    public static async Task<OperatingSettings> ReadOperatingSettingsAsync(CancellationToken cancellationToken)
    {
        JsonObject document = await ReadDocumentAsync(cancellationToken);
        return DeserializeSection(document);
    }

    /// <summary>
    /// Read-modify-write under the section key: <paramref name="mutate"/> sees the settings as
    /// they stand today (defaults where nothing is configured) and changes only what it means
    /// to. Returns whether this call materialised the file — it did not already exist — so a
    /// caller like <c>h9k config set</c> can say so out loud rather than creating it silently
    /// (backlog 59's "created with defaults on first need, stated out loud").
    /// </summary>
    public static async Task<bool> WriteOperatingSettingsAsync(
        Action<OperatingSettings> mutate, CancellationToken cancellationToken)
    {
        bool created = !File.Exists(Hall9kDatabase.ConfigFile);
        Directory.CreateDirectory(PlatformPaths.Home);

        JsonObject document = await ReadDocumentAsync(cancellationToken);
        OperatingSettings settings = DeserializeSection(document);

        mutate(settings);

        document[SectionName] = JsonSerializer.SerializeToNode(settings, SerializerOptions);
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile, document.ToJsonString(SerializerOptions), cancellationToken);
        return created;
    }

    private static JsonObject? Section(JsonObject document) => document[SectionName] as JsonObject;

    /// <summary>
    /// Deserializes the "hall9k" section, the same way <see cref="ReadDocumentAsync"/> guards a
    /// file that fails to parse: a genuinely wrong shape (a scalar where <see
    /// cref="RoleModelSettings"/> belongs, a number string that still fails to parse) throws
    /// <see cref="JsonException"/> just as surely as a syntax error does, and every caller —
    /// <c>h9k config show</c>, <c>h9k daemon status</c>, <c>h9k config set</c> — needs the same
    /// diagnosable exception rather than a raw stack trace. A quoted number that does parse
    /// (<c>"4"</c>) is read as the number, not refused: the daemon binds this section through
    /// <c>IConfiguration</c>, where every JSON leaf is already a string, so the two must agree
    /// on what a hand-quoted number means. <see cref="OperatingSettings.ModelByRole"/> is
    /// normalized back to an empty instance here too: an explicit JSON <c>null</c> for that key
    /// deserializes to <c>null</c> since the property has a public setter, and every caller of
    /// this type dereferences it unconditionally.
    /// </summary>
    private static OperatingSettings DeserializeSection(JsonObject document)
    {
        if (Section(document) is not { } section)
        {
            return new();
        }

        OperatingSettings settings;
        try
        {
            settings = section.Deserialize<OperatingSettings>(SerializerOptions) ?? new();
        }
        catch (JsonException exception)
        {
            throw new DomainValidationException(
                $"The platform config file ({Hall9kDatabase.ConfigFile}) has a \"hall9k\" section, but a value "
                + $"there has the wrong shape ({exception.Message}). h9k config set merges into this same section, "
                + "so it cannot write until the existing value parses — fix it directly in a text editor first.");
        }

        settings.ModelByRole ??= new();
        return settings;
    }

    /// <summary>
    /// Throws rather than silently starting fresh: unlike the connection-string write (the
    /// doctor's own recovery action, run only after an operator explicitly confirms it), a
    /// merge write here has no confirmation step, so overwriting a broken file's other keys —
    /// possibly including <c>connectionString</c> — is never the quiet default.
    /// </summary>
    private static async Task<JsonObject> ReadDocumentAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(Hall9kDatabase.ConfigFile))
        {
            return new JsonObject();
        }

        string text = await File.ReadAllTextAsync(Hall9kDatabase.ConfigFile, cancellationToken);
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            throw new DomainValidationException(
                $"The platform config file ({Hall9kDatabase.ConfigFile}) exists but is not valid JSON. "
                + "Fix or delete it, then try again — a merge write cannot safely happen while it fails to parse.");
        }

        // JsonNode.Parse succeeds for any valid JSON, not just an object (an array, a string, a
        // bare number). Falling back to an empty JsonObject here would be the quiet overwrite
        // this method's own contract refuses for a syntax error — whatever the file held,
        // connectionString included, would vanish under the next write with no message at all.
        return parsed as JsonObject
            ?? throw new DomainValidationException(
                $"The platform config file ({Hall9kDatabase.ConfigFile}) exists but its top level is not a JSON "
                + "object — a merge write needs a { ... } document to add the \"hall9k\" section to. Fix or "
                + "delete it, then try again.");
    }
}

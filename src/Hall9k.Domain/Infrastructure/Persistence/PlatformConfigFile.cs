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

    /// <summary>
    /// The three concurrency leaves <c>Hall9k.Daemon.DaemonOptionsBinding.ResolverOwnedKeys</c>
    /// excludes from the daemon's own <c>ConfigurationBinder</c> call (Decisions Log #109's
    /// follow-up), named here independently by <see cref="OperatingSettings"/>'s own property
    /// names — Domain cannot reference the Daemon project, so this list and the daemon's own
    /// cannot share a single source, but a rename of either would fail to compile rather than
    /// silently drift apart.
    /// </summary>
    private static readonly string[] ResolverOwnedLeaves =
    [
        nameof(OperatingSettings.MaxConcurrentTaskRuns),
        nameof(OperatingSettings.SessionCapPerRun),
        nameof(OperatingSettings.MaxConcurrentAgentSessions),
    ];

    /// <summary>
    /// The exact leniency <c>Microsoft.Extensions.Configuration.Json</c>'s own
    /// <c>JsonConfigurationFileParser</c> parses this file with (comments skipped, trailing
    /// commas allowed) — shared with <see cref="Hall9k.Daemon.PlatformConfigFileSource"/>'s
    /// pre-parse guard so neither it nor this type's own duplicate-key check ever rejects a file
    /// the daemon's real parser would load fine. Origin: the cycle-2 pre-PR review found both
    /// <see cref="JsonDocument.Parse(string, JsonDocumentOptions)"/> calls here and the daemon's
    /// used the strict default options, refusing a commented or trailing-comma-terminated file
    /// as "not valid JSON" when the daemon's own parser would have loaded it without complaint.
    /// </summary>
    public static readonly JsonDocumentOptions LenientDocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

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
    /// Same read as <see cref="ReadOperatingSettingsAsync"/>, but for a caller that only wants to
    /// describe the file to an operator rather than merge a write into it (<c>h9k config show</c>,
    /// <c>h9k daemon status</c>): a failure is reported rather than thrown. A document-level
    /// failure (syntax error, or valid JSON whose top level is not an object) is exactly what
    /// <see cref="PlatformConfigFileSource"/>-shaped daemon startup code already guards and skips
    /// gracefully, running on environment variables and built-in defaults; a value-shape failure
    /// inside an otherwise well-formed document never crashes the daemon either
    /// (<c>Hall9k.Daemon.DaemonOptionsBinding.ResolverOwnedKeys</c> excludes every concurrency
    /// setting this section carries from the daemon's own <c>ConfigurationBinder</c> call,
    /// Decisions Log #109's follow-up), so the diagnosis recovers the same siblings rather than
    /// discarding the whole section — a healthy <c>maxConcurrentTaskRuns</c> sitting next to a
    /// malformed <c>maxConcurrentAgentSessions</c> must not be reported as skipped too.
    /// </summary>
    public static async Task<ConfigFileReadResult> TryReadOperatingSettingsAsync(CancellationToken cancellationToken)
    {
        JsonObject document;
        try
        {
            document = await ReadDocumentAsync(cancellationToken);
        }
        catch (DomainValidationException exception)
        {
            return ConfigFileReadResult.Failed(exception.Message);
        }

        try
        {
            OperatingSettings settings = DeserializeSectionCore(document);
            bool maxConcurrentAgentSessionsIsFabricatedZero = ApplyMaxConcurrentAgentSessionsBinderQuirk(document, settings);
            return ConfigFileReadResult.Ok(settings, maxConcurrentAgentSessionsIsFabricatedZero);
        }
        catch (JsonException exception)
        {
            return RecoverSectionIgnoring(document, exception);
        }
    }

    /// <summary>
    /// Re-deserializes the section with only the exact leaf named by <paramref name="exception"/>'s
    /// <see cref="JsonException.Path"/> removed, so every sibling — including a sibling nested
    /// inside the same object — still binds, the same tolerance <c>ConfigurationBinder</c> itself
    /// has for a leaf it cannot convert. <see cref="JsonException.Path"/> names the full walk to
    /// the mismatch (<c>$.modelByRole</c> for a top-level shape mismatch, <c>$.modelByRole.build</c>
    /// for one nested inside it), so removing only the last segment's container — rather than the
    /// first segment's whole object — is what keeps <c>review</c> reported when only <c>build</c>
    /// is malformed.
    /// </summary>
    private static ConfigFileReadResult RecoverSectionIgnoring(JsonObject document, JsonException exception)
    {
        if (Section(document) is not { } section)
        {
            return ConfigFileReadResult.SettingIgnored(new(), ShapeErrorMessage(exception), false);
        }

        JsonObject recovery = (JsonObject)section.DeepClone();
        string[] segments = exception.Path?.Split('.') is { Length: > 1 } parts ? parts[1..] : [];
        RemoveFailingLeaf(recovery, segments);
        bool affectsResolverOwnedKey = segments.Length > 0
            && ResolverOwnedLeaves.Contains(segments[0], StringComparer.OrdinalIgnoreCase);

        try
        {
            OperatingSettings settings = recovery.Deserialize<OperatingSettings>(SerializerOptions) ?? new();
            settings.ModelByRole ??= new();
            bool maxConcurrentAgentSessionsIsFabricatedZero = ApplyMaxConcurrentAgentSessionsBinderQuirk(document, settings);
            return ConfigFileReadResult.SettingIgnored(
                settings, ShapeErrorMessage(exception), maxConcurrentAgentSessionsIsFabricatedZero, affectsResolverOwnedKey);
        }
        catch (JsonException)
        {
            // A second malformed leaf beyond the one already being ignored: fall back to nothing
            // recovered rather than looping, the same conservative outcome as before this fix.
            // Neither leaf crashes ConfigurationBinder (see TryReadOperatingSettingsAsync's own
            // doc), so both malformed key orders converge on this same SettingIgnored verdict.
            return ConfigFileReadResult.SettingIgnored(new(), ShapeErrorMessage(exception), false);
        }
    }

    /// <summary>
    /// Walks <paramref name="segments"/> (for example <c>["modelByRole", "build"]</c>) into
    /// <paramref name="recovery"/> and removes only the final one, so a mismatch nested inside an
    /// object discards just that leaf rather than every sibling under it. Falls back to removing
    /// whatever can be named at the point the walk stops — a missing or non-object container along
    /// the way — which is the same conservative "drop the whole thing" outcome this replaces, but
    /// only for the shapes that still cannot be walked.
    /// </summary>
    private static void RemoveFailingLeaf(JsonObject recovery, string[] segments)
    {
        if (segments.Length == 0)
        {
            return;
        }

        JsonObject current = recovery;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            string? key = FindKeyIgnoringCase(current, segments[i]);
            if (key is null || current[key] is not JsonObject nested)
            {
                if (key is not null)
                {
                    current.Remove(key);
                }

                return;
            }

            current = nested;
        }

        if (FindKeyIgnoringCase(current, segments[^1]) is { } leafKey)
        {
            current.Remove(leafKey);
        }
    }

    private static string? FindKeyIgnoringCase(JsonObject obj, string name) =>
        obj.Select(property => property.Key)
            .FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <c>ConfigurationBinder</c> does not merely skip an explicit JSON <c>null</c> or an empty
    /// object <c>{}</c> at this one leaf the way it skips every other shape mismatch here: because
    /// <see cref="OperatingSettings.MaxConcurrentAgentSessions"/> mirrors a non-nullable
    /// <c>int</c> on the daemon's own <c>DaemonOptions</c>, there is no null to assign, so the
    /// binder's explicit-value handling resolves it to <see langword="default"/> — zero — rather
    /// than leaving the property untouched at its built-in default of three. Reporting "ignored,
    /// default (3) still applies" for either shape would tell an operator the daemon dispatches
    /// at full concurrency when it has in fact floored itself to exactly one running session —
    /// except <see cref="OperatingSettingsResolver.ResolveMaxConcurrentTaskRuns"/> reads this
    /// method's own return value separately and treats a fabricated zero as absent rather than
    /// converting it into a run ceiling of one, so the sub-1 warning does not fire for this shape;
    /// it fires only for a leaf that genuinely holds a configured zero. Every other
    /// object or array shape here — a non-empty object, a non-empty array — genuinely is left
    /// alone by the binder and must not be zeroed. Confirmed against the pinned binder version
    /// directly rather than inferred. Origin: cycle-7 pre-PR review. This is a description of what
    /// <c>ConfigurationBinder</c> would have done, kept for the sake of <c>h9k config show</c>'s
    /// own accuracy about the JSON shape, even though nothing binds this leaf through
    /// <c>ConfigurationBinder</c> any more — see <see cref="TryReadOperatingSettingsAsync"/>'s
    /// own doc.
    /// </summary>
    /// <returns>Whether the quirk fired — see <see cref="ConfigFileReadResult.MaxConcurrentAgentSessionsIsFabricatedZero"/>.</returns>
    private static bool ApplyMaxConcurrentAgentSessionsBinderQuirk(JsonObject document, OperatingSettings settings)
    {
        if (Section(document) is not { } section
            || FindKeyIgnoringCase(section, "maxConcurrentAgentSessions") is not { } key)
        {
            return false;
        }

        bool bindsToZero = section[key] switch
        {
            null => true,
            JsonObject { Count: 0 } => true,
            _ => false,
        };

        if (bindsToZero)
        {
            settings.MaxConcurrentAgentSessions = 0;
        }

        return bindsToZero;
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

        // Replace whatever key already names this section — never add a second one: the daemon
        // binds it through IConfiguration, where "hall9k" and "Hall9k" are the same key, so
        // leaving the existing casing in place and adding our own would hand
        // JsonConfigurationFileParser two keys that collide and throw at daemon startup.
        string sectionKey = ExistingSectionKey(document) ?? SectionName;
        document.Remove(sectionKey);
        document[sectionKey] = JsonSerializer.SerializeToNode(settings, SerializerOptions);
        await AtomicFileWrite.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile, document.ToJsonString(SerializerOptions), cancellationToken);
        return created;
    }

    /// <summary>
    /// Looked up case-insensitively, the same way the daemon finds this section through
    /// <c>IConfiguration</c> (every <c>Microsoft.Extensions.Configuration</c> key comparison is
    /// ordinal-ignore-case): an ordinal <see cref="JsonObject"/> indexer lookup would miss a
    /// hand-edited "Hall9k" and silently treat the section as absent.
    /// </summary>
    private static JsonObject? Section(JsonObject document) =>
        ExistingSectionKey(document) is { } key ? document[key] as JsonObject : null;

    private static string? ExistingSectionKey(JsonObject document)
    {
        foreach (KeyValuePair<string, JsonNode?> property in document)
        {
            if (string.Equals(property.Key, SectionName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Key;
            }
        }

        return null;
    }

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
        try
        {
            return DeserializeSectionCore(document);
        }
        catch (JsonException exception)
        {
            throw new DomainValidationException(ShapeErrorMessage(exception));
        }
    }

    /// <summary>
    /// The same deserialize <see cref="DeserializeSection"/> wraps into a <see
    /// cref="DomainValidationException"/> for the write path, left as a raw throw here so <see
    /// cref="TryReadOperatingSettingsAsync"/> can inspect <see cref="JsonException.Path"/> itself
    /// before deciding whether the mismatch is one the daemon's own <c>ConfigurationBinder</c>
    /// would actually crash on.
    /// </summary>
    private static OperatingSettings DeserializeSectionCore(JsonObject document)
    {
        if (Section(document) is not { } section)
        {
            return new();
        }

        OperatingSettings settings = section.Deserialize<OperatingSettings>(SerializerOptions) ?? new();
        settings.ModelByRole ??= new();
        return settings;
    }

    private static string ShapeErrorMessage(JsonException exception) =>
        $"The platform config file ({Hall9kDatabase.ConfigFile}) has a \"hall9k\" section, but a value "
        + $"there has the wrong shape ({exception.Message}). h9k config set merges into this same section, "
        + "so it cannot write until the existing value parses — fix it directly in a text editor first.";

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

        string text;
        try
        {
            text = await File.ReadAllTextAsync(Hall9kDatabase.ConfigFile, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The same case PlatformConfigFileSource guards on the daemon side (a file this
            // account cannot read — sudo-created, or chmod'd by another user on a shared box):
            // reported as a document-level failure rather than left to escape as a raw
            // exception, so a caller that only wants to describe the file (h9k config show,
            // h9k daemon status) degrades the same way the daemon itself does instead of dying
            // on an unhandled stack trace.
            throw new DomainValidationException(
                $"The platform config file ({Hall9kDatabase.ConfigFile}) could not be read "
                + $"({exception.Message}). Fix its permissions, then try again.");
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text, documentOptions: LenientDocumentOptions);
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
        JsonObject document = parsed as JsonObject
            ?? throw new DomainValidationException(
                $"The platform config file ({Hall9kDatabase.ConfigFile}) exists but its top level is not a JSON "
                + "object — a merge write needs a { ... } document to add the \"hall9k\" section to. Fix or "
                + "delete it, then try again.");

        // JsonObject collapses a key that repeats under a different case down to one entry
        // silently, so it cannot tell us this happened — check the raw text instead, with the
        // same JsonDocument-based walk PlatformConfigFileSource runs on the daemon side, so a
        // file shaped like this is diagnosed identically on both.
        using JsonDocument forDuplicateCheck = JsonDocument.Parse(text, LenientDocumentOptions);
        if (HasCaseInsensitiveDuplicateKeys(forDuplicateCheck.RootElement))
        {
            throw new DomainValidationException(
                $"The platform config file ({Hall9kDatabase.ConfigFile}) has a key that repeats under a "
                + "different case (for example \"hall9k\" and \"Hall9k\") — Microsoft.Extensions.Configuration.Json "
                + "treats keys case-insensitively and refuses to load a file shaped like this. Fix or delete it, "
                + "then try again.");
        }

        return document;
    }

    /// <summary>
    /// Whether any JSON object in <paramref name="element"/> repeats a property name under a
    /// different case — the shape <c>Microsoft.Extensions.Configuration.Json</c>'s own parser
    /// refuses outright (its keys are ordinal-ignore-case, so "Hall9k" and "hall9k" collide) with
    /// an unguarded <see cref="FormatException"/>. Shared with <see cref="PlatformConfigFileSource"/>
    /// so the CLI's read and the daemon's own pre-parse guard treat the exact same file the same way.
    /// </summary>
    public static bool HasCaseInsensitiveDuplicateKeys(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (!seen.Add(property.Name) || HasCaseInsensitiveDuplicateKeys(property.Value))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (HasCaseInsensitiveDuplicateKeys(item))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }
}

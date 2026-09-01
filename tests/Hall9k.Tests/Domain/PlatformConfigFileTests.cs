using System.Text.Json;
using FluentAssertions;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The "hall9k" section of the platform config file (backlog 59) is a merge target, never a
/// clobber: writing a setting here must never disturb <c>connectionString</c> (owned by
/// <see cref="Hall9kDatabase"/>) or a hand-edited key this feature does not model.
/// </summary>
[Collection("Hall9kHome")]
public sealed class PlatformConfigFileTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), $"h9k-cfg-{Path.GetRandomFileName()}");
    private readonly string? previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");
    private readonly string? previousConnectionString =
        Environment.GetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName);

    public PlatformConfigFileTests()
    {
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("HALL9K_HOME", home);
        // The connection string chain checks this before it ever reads the config file, so a
        // value already in this shell would otherwise outrank what this test just wrote there.
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", previousHome);
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, previousConnectionString);
        Directory.Delete(home, recursive: true);
    }

    [Fact]
    public async Task Reading_with_no_file_yet_returns_every_field_unset()
    {
        OperatingSettings settings = await PlatformConfigFile.ReadOperatingSettingsAsync(CancellationToken.None);

        settings.MaxConcurrentAgentSessions.Should().BeNull();
        settings.DefaultModel.Should().BeNull();
        settings.ModelByRole.Build.Should().BeNull();
    }

    [Fact]
    public async Task Writing_a_setting_creates_the_file_and_reports_that_it_did()
    {
        bool created = await PlatformConfigFile.WriteOperatingSettingsAsync(
            settings => settings.MaxConcurrentAgentSessions = 5, CancellationToken.None);

        created.Should().BeTrue("backlog 59 requires a missing file to be created with defaults on first need, stated out loud");
        File.Exists(Hall9kDatabase.ConfigFile).Should().BeTrue();
    }

    [Fact]
    public async Task Writing_a_second_setting_does_not_report_creation_again()
    {
        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.MaxConcurrentAgentSessions = 5, CancellationToken.None);

        bool created = await PlatformConfigFile.WriteOperatingSettingsAsync(
            s => s.DefaultModel = "sonnet", CancellationToken.None);

        created.Should().BeFalse();
    }

    [Fact]
    public async Task Writing_operating_settings_preserves_the_connection_string_key()
    {
        await Hall9kDatabase.WriteConfiguredConnectionStringAsync("keep-me", CancellationToken.None);

        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.MaxConcurrentAgentSessions = 4, CancellationToken.None);

        ConnectionStringResolution resolution = Hall9kDatabase.Resolve(startDirectory: home);
        resolution.Value.Should().Be("keep-me", "a daemon-settings write must never disturb the connection string that lives in the same file");
    }

    [Fact]
    public async Task Writing_a_known_setting_preserves_an_unrelated_hand_edited_key_in_the_same_section()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile,
            """{"hall9k": {"maxConcurrentAgentSessions": 3, "someFutureSetting": "keep-me"}}""");

        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.MaxConcurrentAgentSessions = 4, CancellationToken.None);

        string written = await File.ReadAllTextAsync(Hall9kDatabase.ConfigFile);
        written.Should().Contain("keep-me", "a hand-edited key this feature does not model still has to survive a CLI write");
    }

    [Fact]
    public async Task Round_tripping_model_by_role_keeps_every_configured_role()
    {
        await PlatformConfigFile.WriteOperatingSettingsAsync(s =>
        {
            s.ModelByRole.Review = "sonnet";
            s.ModelByRole.Fix = "haiku";
        }, CancellationToken.None);

        OperatingSettings settings = await PlatformConfigFile.ReadOperatingSettingsAsync(CancellationToken.None);

        settings.ModelByRole.Review.Should().Be("sonnet");
        settings.ModelByRole.Fix.Should().Be("haiku");
        settings.ModelByRole.Build.Should().BeNull("a role never set stays unset rather than being filled in");
    }

    [Fact]
    public async Task A_malformed_config_file_refuses_the_write_rather_than_silently_starting_fresh()
    {
        await File.WriteAllTextAsync(Hall9kDatabase.ConfigFile, "{ not valid json");

        Func<Task> write = () => PlatformConfigFile.WriteOperatingSettingsAsync(
            s => s.MaxConcurrentAgentSessions = 4, CancellationToken.None);

        await write.Should().ThrowAsync<DomainValidationException>(
            "a broken file needs repair, not a merge write that could silently drop whatever else it held");
    }

    [Fact]
    public async Task A_malformed_config_file_refuses_the_read_the_same_way()
    {
        await File.WriteAllTextAsync(Hall9kDatabase.ConfigFile, "{ not valid json");

        Func<Task> read = () => PlatformConfigFile.ReadOperatingSettingsAsync(CancellationToken.None);

        await read.Should().ThrowAsync<DomainValidationException>();
    }

    /// <summary>
    /// The daemon binds this same section through <c>IConfiguration</c>, where every JSON leaf is
    /// already a string, so a hand-quoted number is a value the daemon runs on fine. Origin: the
    /// pre-PR review of this branch found the CLI reporting a healthy, in-force file as broken and
    /// ignored because System.Text.Json's strict typing rejected the exact shape the daemon accepts.
    /// </summary>
    [Fact]
    public async Task A_quoted_number_reads_the_same_way_the_daemon_binds_it()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile, """{"hall9k": {"maxConcurrentAgentSessions": "4"}}""");

        OperatingSettings settings = await PlatformConfigFile.ReadOperatingSettingsAsync(CancellationToken.None);

        settings.MaxConcurrentAgentSessions.Should().Be(4);
    }

    [Fact]
    public async Task A_non_numeric_string_for_a_numeric_setting_is_still_a_diagnosable_exception()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile, """{"hall9k": {"maxConcurrentAgentSessions": "four"}}""");

        Func<Task> read = () => PlatformConfigFile.ReadOperatingSettingsAsync(CancellationToken.None);

        await read.Should().ThrowAsync<DomainValidationException>(
            "a string that still cannot convert to a number is genuinely the wrong shape, and the daemon's own "
            + "ConfigurationBinder fails to start on it too");
    }

    [Fact]
    public async Task A_config_file_whose_top_level_is_not_a_json_object_is_a_diagnosable_exception()
    {
        await File.WriteAllTextAsync(Hall9kDatabase.ConfigFile, "[1, 2, 3]");

        Func<Task> read = () => PlatformConfigFile.ReadOperatingSettingsAsync(CancellationToken.None);

        await read.Should().ThrowAsync<DomainValidationException>(
            "a merge write needs a { ... } document, and silently substituting an empty one would erase whatever "
            + "the file actually held on the next write");
    }

    [Fact]
    public async Task A_value_of_the_wrong_json_type_also_refuses_the_write()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile, """{"hall9k": {"modelByRole": "sonnet"}}""");

        Func<Task> write = () => PlatformConfigFile.WriteOperatingSettingsAsync(
            s => s.MaxConcurrentAgentSessions = 4, CancellationToken.None);

        await write.Should().ThrowAsync<DomainValidationException>();
    }

    [Fact]
    public async Task Writing_a_setting_leaves_no_temp_file_behind()
    {
        await PlatformConfigFile.WriteOperatingSettingsAsync(
            s => s.MaxConcurrentAgentSessions = 4, CancellationToken.None);

        Directory.GetFiles(home, "config.json.tmp-*").Should().BeEmpty(
            "the atomic write stages into a temp file and renames it away; nothing should remain named after it");
    }

    [Fact]
    public async Task An_explicit_null_model_by_role_reads_back_as_an_empty_instance_rather_than_null()
    {
        await File.WriteAllTextAsync(Hall9kDatabase.ConfigFile, """{"hall9k": {"modelByRole": null}}""");

        OperatingSettings settings = await PlatformConfigFile.ReadOperatingSettingsAsync(CancellationToken.None);

        settings.ModelByRole.Should().NotBeNull();
        settings.ModelByRole.Build.Should().BeNull();
    }

    /// <summary>
    /// The daemon binds this section through <c>IConfiguration</c>, where every key comparison is
    /// case-insensitive, so a hand-edit using the casing the env-var table and the daemon's own
    /// startup log print ("Hall9k") has to be read exactly like the canonical lowercase key.
    /// Origin: the cycle-4 pre-PR review found the CLI's <see cref="JsonObject"/> indexer lookup
    /// treating "Hall9k" as a different, absent section from "hall9k".
    /// </summary>
    [Fact]
    public async Task A_section_key_spelled_with_the_daemons_own_casing_still_reads()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile, """{"Hall9k": {"maxConcurrentAgentSessions": 6}}""");

        OperatingSettings settings = await PlatformConfigFile.ReadOperatingSettingsAsync(CancellationToken.None);

        settings.MaxConcurrentAgentSessions.Should().Be(6);
    }

    /// <summary>
    /// <c>JsonConfigurationFileParser</c> — what the daemon actually binds this file through —
    /// parses with comments skipped and trailing commas allowed, so this type's own read (and the
    /// merge write's duplicate-key check) must accept exactly what it accepts. Origin: the cycle-2
    /// pre-PR review found the strict default <see cref="JsonDocument.Parse(string)"/> /
    /// <see cref="JsonNode.Parse(string, JsonNodeOptions?, JsonDocumentOptions)"/> options used
    /// here rejecting a file the daemon loads fine as "not valid JSON", and refusing to write to it.
    /// </summary>
    [Fact]
    public async Task A_config_file_with_a_comment_and_a_trailing_comma_reads_and_can_be_written_to()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile,
            """
            {
                "hall9k": {
                    "maxConcurrentAgentSessions": 4, // laptop OOMs above 4
                },
            }
            """);

        OperatingSettings settings = await PlatformConfigFile.ReadOperatingSettingsAsync(CancellationToken.None);
        settings.MaxConcurrentAgentSessions.Should().Be(4);

        Func<Task> write = () => PlatformConfigFile.WriteOperatingSettingsAsync(
            s => s.DefaultModel = "sonnet", CancellationToken.None);
        await write.Should().NotThrowAsync("a file the daemon's own parser loads fine must not block a merge write");
    }

    /// <summary>
    /// Writing must replace whatever key already names the section rather than adding a second,
    /// differently-cased one beside it: <c>JsonConfigurationFileParser</c>'s keys are also
    /// case-insensitive, so two such keys is a duplicate-key <see cref="FormatException"/> at
    /// daemon startup, not two settings.
    /// </summary>
    [Fact]
    public async Task Writing_over_a_differently_cased_section_key_replaces_it_rather_than_duplicating_it()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile, """{"Hall9k": {"maxConcurrentAgentSessions": 6}}""");

        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.DefaultModel = "sonnet", CancellationToken.None);

        string written = await File.ReadAllTextAsync(Hall9kDatabase.ConfigFile);
        using JsonDocument document = JsonDocument.Parse(written);
        PlatformConfigFile.HasCaseInsensitiveDuplicateKeys(document.RootElement).Should().BeFalse(
            "the write must replace the existing (differently-cased) section key rather than add a second one");
    }

    /// <summary>
    /// A file already holding two case-variant section keys (from a hand-edit, or from the bug
    /// the previous two tests guard against) is exactly what crashes the real daemon with a raw
    /// duplicate-key <see cref="FormatException"/>; a merge write must refuse rather than silently
    /// picking one of the two.
    /// </summary>
    [Fact]
    public async Task A_config_file_with_a_case_variant_duplicate_key_is_a_diagnosable_exception()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile,
            """{"hall9k": {"maxConcurrentAgentSessions": 3}, "Hall9k": {"maxConcurrentAgentSessions": 6}}""");

        Func<Task> read = () => PlatformConfigFile.ReadOperatingSettingsAsync(CancellationToken.None);

        await read.Should().ThrowAsync<DomainValidationException>(
            "Microsoft.Extensions.Configuration.Json's keys are case-insensitive, so this file crashes the "
            + "daemon at startup rather than resolving to either section");
    }

    /// <summary>
    /// This shape does not actually crash the daemon: <c>ModelByRole</c> is a complex object, and
    /// <c>ConfigurationBinder</c> has no string-to-object conversion for it, so it silently binds
    /// no children rather than throwing the way it does for the numeric <c>MaxConcurrentAgentSessions</c>
    /// leaf. Origin: the cycle-4 pre-PR review found <see cref="PlatformConfigFile.TryReadOperatingSettingsAsync"/>
    /// reporting every shape mismatch as a startup crash, which is wrong for this one.
    /// </summary>
    [Fact]
    public async Task A_scalar_given_for_the_model_by_role_object_is_reported_as_not_crashing_the_daemon()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile, """{"hall9k": {"modelByRole": "sonnet"}}""");

        ConfigFileReadResult result = await PlatformConfigFile.TryReadOperatingSettingsAsync(CancellationToken.None);

        result.Problem.Should().NotBeNull();
        result.Problem!.Consequence.Should().Be(ConfigFileProblemConsequence.SettingIsIgnored,
            "ConfigurationBinder has no converter for this complex type, so it binds no children rather than throwing");
    }

    /// <summary>
    /// <see cref="JsonException.Path"/> carries the document's own casing, not the POCO's, so a
    /// hand-edit using the PascalCase names this project's own docs print for the property (not
    /// just the section key the sibling tests above cover) must still be recognised as the same
    /// leaf, whichever casing named it. No leaf in this section crashes the daemon any more
    /// (<c>DaemonOptionsBinding.ResolverOwnedKeys</c> excludes every concurrency setting from the
    /// daemon's own <c>ConfigurationBinder</c> call, Decisions Log #109's follow-up), so a malformed
    /// value here is recovered like any other shape mismatch instead. Origin: the cycle-1 pre-PR
    /// review found the ordinal comparison here missing this case; independent pre-PR review,
    /// cycle 1 of the concurrency-in-runs branch, found this test's own expectation stale once
    /// <c>maxConcurrentAgentSessions</c> stopped being bound at all.
    /// </summary>
    [Fact]
    public async Task A_pascal_cased_property_name_is_still_recognised_and_treated_as_ignored()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile, """{"hall9k": {"MaxConcurrentAgentSessions": "four"}}""");

        ConfigFileReadResult result = await PlatformConfigFile.TryReadOperatingSettingsAsync(CancellationToken.None);

        result.Problem.Should().NotBeNull();
        result.Problem!.Consequence.Should().Be(ConfigFileProblemConsequence.SettingIsIgnored,
            "ConfigurationBinder's own key comparison is case-insensitive too, so this value is recognised as "
            + "the same leaf whichever casing the file used, and never bound through ConfigurationBinder either way");
        result.Settings.MaxConcurrentAgentSessions.Should().BeNull(
            "the malformed leaf is removed and every sibling recovered, the same as the lowercase-keyed shape");
    }

    /// <summary>
    /// <c>JsonException.Path</c> for a mismatch nested inside <c>modelByRole</c> (<c>$.modelByRole.build</c>)
    /// names the whole walk to the leaf, not just the top-level property — removing the whole
    /// <c>modelByRole</c> object because the path merely starts with it would discard <c>review</c>
    /// too, even though <c>ConfigurationBinder</c> binds it fine. Origin: the cycle-3 pre-PR review
    /// found <see cref="PlatformConfigFile"/>'s recovery stripping the whole first path segment,
    /// so a sibling role the daemon does in fact bind was reported as unset.
    /// <c>build</c> is given an empty object here rather than a number: <c>JsonConfigurationFileParser</c>
    /// routes an object into nested keys instead of a leaf value, so the binder genuinely leaves it
    /// unset, unlike a number or boolean (see the sibling test just below, which covers that case).
    /// </summary>
    [Fact]
    public async Task A_shape_mismatch_nested_inside_model_by_role_only_discards_that_one_role()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile,
            """{"hall9k": {"modelByRole": {"build": {}, "review": "sonnet"}}}""");

        ConfigFileReadResult result = await PlatformConfigFile.TryReadOperatingSettingsAsync(CancellationToken.None);

        result.Problem.Should().NotBeNull();
        result.Problem!.Consequence.Should().Be(ConfigFileProblemConsequence.SettingIsIgnored);
        result.Settings.ModelByRole.Review.Should().Be(
            "sonnet", "ConfigurationBinder binds this sibling role fine even though build fails to convert");
        result.Settings.ModelByRole.Build.Should().BeNull();
    }

    /// <summary>
    /// <c>JsonConfigurationFileParser</c> stringifies every JSON leaf before <c>ConfigurationBinder</c>
    /// ever sees it, so a hand-quoted number here is a value the daemon binds and spawns on — not
    /// one it silently ignores. Reporting it as ignored (falling back to a healthy default) would
    /// hide that every session in this role is about to be spawned with <c>--model 3</c>. Origin:
    /// the cycle-4 pre-PR review found this exact shape reported as <c>SettingIsIgnored</c> when
    /// the daemon in fact binds and runs on the coerced string.
    /// </summary>
    [Fact]
    public async Task A_number_given_for_a_role_model_binds_as_the_daemon_would_rather_than_being_reported_as_ignored()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile, """{"hall9k": {"modelByRole": {"build": 3}}}""");

        ConfigFileReadResult result = await PlatformConfigFile.TryReadOperatingSettingsAsync(CancellationToken.None);

        result.Problem.Should().BeNull("ConfigurationBinder binds this value fine, so there is nothing to report");
        result.Settings.ModelByRole.Build.Should().Be("3");
    }

    /// <summary>
    /// The same coercion as the sibling test above, for <c>defaultModel</c> rather than a role
    /// under <c>modelByRole</c>.
    /// </summary>
    [Fact]
    public async Task A_number_given_for_the_default_model_binds_as_the_daemon_would_rather_than_being_reported_as_ignored()
    {
        await File.WriteAllTextAsync(Hall9kDatabase.ConfigFile, """{"hall9k": {"defaultModel": 5}}""");

        ConfigFileReadResult result = await PlatformConfigFile.TryReadOperatingSettingsAsync(CancellationToken.None);

        result.Problem.Should().BeNull("ConfigurationBinder binds this value fine, so there is nothing to report");
        result.Settings.DefaultModel.Should().Be("5");
    }

    /// <summary>
    /// <c>JsonConfigurationFileParser</c> flattens an empty JSON object into nested keys rather
    /// than a value at <c>maxConcurrentAgentSessions</c>'s own key, so <c>ConfigurationBinder</c>
    /// finds nothing to convert there — it does not crash the way an unparseable string does. But
    /// unlike every other shape this method reports as merely ignored, the binder does not leave
    /// the setting at its built-in default of three either: because the property mirrors a
    /// non-nullable <c>int</c> on the daemon side, its explicit-value handling resolves an object
    /// with no children to zero. Origin: the cycle-4 pre-PR review found this shape reported as
    /// <c>DaemonFailsToStart</c> when the daemon in fact starts (though not on the default); the
    /// cycle-7 review found the reported settings still claimed the healthy default of three when
    /// the daemon actually floors dispatch to one running session. Both confirmed against the
    /// pinned binder version directly.
    /// </summary>
    [Fact]
    public async Task An_empty_object_for_the_concurrency_ceiling_is_reported_as_ignored_but_binds_to_zero()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile, """{"hall9k": {"maxConcurrentAgentSessions": {}}}""");

        ConfigFileReadResult result = await PlatformConfigFile.TryReadOperatingSettingsAsync(CancellationToken.None);

        result.Problem.Should().NotBeNull();
        result.Problem!.Consequence.Should().Be(ConfigFileProblemConsequence.SettingIsIgnored,
            "the daemon starts normally on this shape rather than crashing");
        result.Settings.MaxConcurrentAgentSessions.Should().Be(0,
            "ConfigurationBinder's explicit-value handling resolves a childless object to zero for a "
            + "non-nullable int property, not to the type's declared default of three");
    }

    /// <summary>
    /// Unlike an empty object, an empty JSON array still gets a direct entry at
    /// <c>maxConcurrentAgentSessions</c>'s own key (the empty string) — historically, back when
    /// this leaf was still bound through <c>ConfigurationBinder</c>, that shape crashed the daemon
    /// exactly like an unparseable string did. It no longer can: this leaf is now excluded from
    /// the daemon's own <c>ConfigurationBinder</c> call entirely (<c>DaemonOptionsBinding
    /// .ResolverOwnedKeys</c>, Decisions Log #109's follow-up), so it is recovered like any other
    /// shape mismatch instead. Origin: the cycle-7 pre-PR review found this shape reported as
    /// merely ignored when the daemon at the time in fact never started; independent pre-PR
    /// review, cycle 1 of the concurrency-in-runs branch, found that verdict itself gone stale.
    /// </summary>
    [Fact]
    public async Task An_empty_array_for_the_concurrency_ceiling_is_reported_as_ignored()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile, """{"hall9k": {"maxConcurrentAgentSessions": []}}""");

        ConfigFileReadResult result = await PlatformConfigFile.TryReadOperatingSettingsAsync(CancellationToken.None);

        result.Problem.Should().NotBeNull();
        result.Problem!.Consequence.Should().Be(ConfigFileProblemConsequence.SettingIsIgnored,
            "this leaf is excluded from the daemon's own ConfigurationBinder call entirely, so nothing crashes on it");
        result.Settings.MaxConcurrentAgentSessions.Should().BeNull(
            "an empty array is not the null/empty-object shape ApplyMaxConcurrentAgentSessionsBinderQuirk zeroes, "
            + "so the removed leaf is left genuinely unset");
    }

    /// <summary>
    /// A non-empty object or array is what the cycle-4 fix actually covers: <c>ConfigurationBinder</c>
    /// finds children under this key instead of a leaf value, so it leaves the property alone —
    /// genuinely at its built-in default of three, unlike the empty-object shape above.
    /// </summary>
    [Theory]
    [InlineData("""{"anything": "goes"}""")]
    [InlineData("[1, 2]")]
    public async Task A_non_empty_container_for_the_concurrency_ceiling_is_reported_as_ignored_at_the_true_default(string shape)
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile, "{\"hall9k\": {\"maxConcurrentAgentSessions\": " + shape + "}}");

        ConfigFileReadResult result = await PlatformConfigFile.TryReadOperatingSettingsAsync(CancellationToken.None);

        result.Problem.Should().NotBeNull();
        result.Problem!.Consequence.Should().Be(ConfigFileProblemConsequence.SettingIsIgnored,
            "the daemon starts normally on this shape rather than crashing");
        result.Settings.MaxConcurrentAgentSessions.Should().BeNull(
            "ConfigurationBinder finds children under this key rather than a leaf value, so it leaves the "
            + "property genuinely untouched at its built-in default of three");
    }

    /// <summary>
    /// An explicit JSON <c>null</c> never fails to deserialize at all — <c>int?</c> accepts it
    /// happily — so this shape reaches none of the exception-driven classification above and needs
    /// its own guard. <c>ConfigurationBinder</c> treats it the same way it treats an empty object:
    /// its explicit-value handling resolves the non-nullable <c>int</c> this property mirrors on
    /// the daemon side to zero, not to the type's declared default of three. Reporting this read as
    /// healthy with no problem at all — which is what happens without the guard, since there is no
    /// exception to classify — would be the same wrong-value mistake as the empty-object shape, just
    /// with no message alongside it whatsoever. Origin: cycle-7 pre-PR review, confirmed against the
    /// pinned binder version directly.
    /// </summary>
    [Fact]
    public async Task An_explicit_null_for_the_concurrency_ceiling_binds_to_zero_with_no_exception_to_classify()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile, """{"hall9k": {"maxConcurrentAgentSessions": null}}""");

        ConfigFileReadResult result = await PlatformConfigFile.TryReadOperatingSettingsAsync(CancellationToken.None);

        result.Settings.MaxConcurrentAgentSessions.Should().Be(0,
            "ConfigurationBinder's explicit-value handling resolves an explicit null to zero for a "
            + "non-nullable int property, exactly as it does for an empty object");
    }

    /// <summary>
    /// Two malformed leaves, where the first one <see cref="JsonException"/> reports is
    /// <c>defaultModel</c> and the second — found only once recovery retries the deserialize with
    /// the first removed — is <c>maxConcurrentAgentSessions</c> holding a non-numeric string.
    /// Historically the second leaf crashed <c>ConfigurationBinder</c>, so the overall read had to
    /// be reported as a crash regardless of what the first leaf did (origin: the cycle-6 pre-PR
    /// review found the retry's own failure silently discarded). Neither leaf can crash the daemon
    /// any more (<c>DaemonOptionsBinding.ResolverOwnedKeys</c> excludes every concurrency setting
    /// from the daemon's own <c>ConfigurationBinder</c> call, Decisions Log #109's follow-up), so
    /// two malformed leaves now fall back to nothing recovered, the same conservative outcome as
    /// before that fix — the sibling test below covers the same two leaves in the opposite key
    /// order, and both orders must land on the identical verdict.
    /// </summary>
    [Fact]
    public async Task A_second_malformed_leaf_found_only_on_retry_is_reported_as_ignored_with_nothing_recovered()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile,
            """{"hall9k": {"defaultModel": {}, "maxConcurrentAgentSessions": "four"}}""");

        ConfigFileReadResult result = await PlatformConfigFile.TryReadOperatingSettingsAsync(CancellationToken.None);

        result.Problem.Should().NotBeNull();
        result.Problem!.Consequence.Should().Be(ConfigFileProblemConsequence.SettingIsIgnored,
            "neither leaf is bound through ConfigurationBinder any more, so nothing crashes the daemon here");
        result.Settings.Should().BeEquivalentTo(new OperatingSettings(),
            "a second malformed leaf beyond the one already being ignored falls back to nothing recovered "
            + "rather than looping, the same conservative outcome the single-malformed-leaf tests above do not need");
    }

    /// <summary>
    /// The same two malformed leaves as the test above, with <c>maxConcurrentAgentSessions</c>
    /// written first: <see cref="JsonException"/> reports it as the very first mismatch, so
    /// recovery removes it and retries immediately, discovering <c>defaultModel</c>'s own mismatch
    /// on the retry instead of the other way around. Both key orders have to land on the identical
    /// verdict — only the code path that discovers the second mismatch differs.
    /// </summary>
    [Fact]
    public async Task A_second_malformed_leaf_found_first_is_also_reported_as_ignored_with_nothing_recovered()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile,
            """{"hall9k": {"maxConcurrentAgentSessions": "four", "defaultModel": {}}}""");

        ConfigFileReadResult result = await PlatformConfigFile.TryReadOperatingSettingsAsync(CancellationToken.None);

        result.Problem.Should().NotBeNull();
        result.Problem!.Consequence.Should().Be(ConfigFileProblemConsequence.SettingIsIgnored,
            "neither leaf is bound through ConfigurationBinder any more, so nothing crashes the daemon here");
        result.Settings.Should().BeEquivalentTo(new OperatingSettings(),
            "whichever key order this is found in, a second malformed leaf falls back to nothing recovered");
    }

    /// <summary>
    /// The daemon's own guard (<c>PlatformConfigFileSource</c>) degrades gracefully when it cannot
    /// read the file at all — a root-owned file, or one <c>chmod</c>'d by another account on a
    /// shared box. The CLI side has to match rather than let the raw exception escape, since
    /// <c>h9k config show</c> and <c>h9k daemon status</c> both call through here unconditionally.
    /// Origin: the cycle-1 pre-PR review found <c>ReadDocumentAsync</c> with no guard at all around
    /// <c>File.ReadAllTextAsync</c>, so an unreadable file crashed both diagnostic commands with a
    /// raw stack trace instead of the reported failure this method promises.
    /// </summary>
    [Fact]
    public async Task An_unreadable_config_file_is_a_diagnosable_exception_rather_than_a_raw_crash()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await File.WriteAllTextAsync(Hall9kDatabase.ConfigFile, """{"hall9k": {"maxConcurrentAgentSessions": 4}}""");
        File.SetUnixFileMode(Hall9kDatabase.ConfigFile, UnixFileMode.None);

        try
        {
            Func<Task> read = () => PlatformConfigFile.ReadOperatingSettingsAsync(CancellationToken.None);

            await read.Should().ThrowAsync<DomainValidationException>(
                "an unreadable file is reported the same way a syntax error is, not left to escape as a raw "
                + "UnauthorizedAccessException");
        }
        finally
        {
            File.SetUnixFileMode(Hall9kDatabase.ConfigFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}

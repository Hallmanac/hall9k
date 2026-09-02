using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Hall9k.Connectors.Prompts;
using Hall9k.Daemon;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// Pins the one settings-file shape every dispatched session and every interactive
/// <c>h9k task work</c> claim launches with (<c>ClaudeExecutor</c> builds it from the live
/// <c>DaemonOptions.VerifyGateTimeout</c>; <c>TaskWorkCommand</c>, unable to reach that option
/// at all, builds it from <see cref="ClaudeSettingsFile.DefaultCommandTimeout"/>): the
/// co-authored-by suppression (PLAN.md §6.6) and, since the 2026-09-01 finding, the
/// command-timeout headroom that lets a foreground gate run survive without a session needing to
/// know a timeout trick.
/// </summary>
public sealed class ClaudeSettingsFileTests
{
    [Fact]
    public void The_built_content_is_well_formed_json()
    {
        JsonDocument.Parse(ClaudeSettingsFile.Build(ClaudeSettingsFile.DefaultCommandTimeout)).Dispose();
    }

    [Fact]
    public void The_built_content_suppresses_co_authored_by()
    {
        using JsonDocument document =
            JsonDocument.Parse(ClaudeSettingsFile.Build(ClaudeSettingsFile.DefaultCommandTimeout));

        document.RootElement.GetProperty("includeCoAuthoredBy").GetBoolean().Should()
            .BeFalse("agents never author co-authored-by trailers (PLAN.md §6.6)");
    }

    [Fact]
    public void The_default_command_timeout_matches_the_platforms_own_gate_timeout_default()
    {
        // ClaudeSettingsFile.DefaultCommandTimeout is the fallback a caller with no live-configured
        // ceiling in reach (TaskWorkCommand, which cannot reference Hall9k.Daemon at all) falls back
        // to — it mirrors DaemonOptions.VerifyGateTimeout's own default. Hall9k.Daemon does
        // reference Hall9k.Connectors, so collapsing the two into one constant is possible in that
        // direction; it is declined deliberately (the gate ceiling should not be defined by a
        // Claude Code settings type), which is what leaves this test as the thing holding them
        // equal.
        ClaudeSettingsFile.DefaultCommandTimeout.Should().Be(new DaemonOptions().VerifyGateTimeout,
            "TaskWorkCommand builds its settings file from this default when VerifyGateTimeout's " +
            "live value is out of reach; a drift here silently reopens the 2026-09-01 finding for " +
            "an interactive h9k task work claim on any machine that never overrode the option");
    }

    [Fact]
    public void Build_sizes_the_default_command_timeout_to_the_requested_value()
    {
        using JsonDocument document = JsonDocument.Parse(ClaudeSettingsFile.Build(TimeSpan.FromMinutes(30)));

        // Read out of the shipped content rather than restated as a second literal beside it, so
        // the assertion cannot agree with a number no session ever receives.
        TimeSpan defaultCommandTimeout = ReadTimeout(document, "BASH_DEFAULT_TIMEOUT_MS");

        defaultCommandTimeout.Should().Be(TimeSpan.FromMinutes(30),
            "a caller with a live-configured ceiling in reach (ClaudeExecutor, resolving " +
            "IOptions<DaemonOptions>.Value.VerifyGateTimeout) must have that value land verbatim " +
            "in the settings file a session actually launches with, not a build-time default " +
            "(2026-09-02 finding: a compile-time constant went stale the moment an operator raised " +
            "the option it claimed to mirror)");
    }

    [Fact]
    public void The_fallback_default_command_timeout_clears_the_stock_two_minute_default()
    {
        using JsonDocument document =
            JsonDocument.Parse(ClaudeSettingsFile.Build(ClaudeSettingsFile.DefaultCommandTimeout));

        // Build itself enforces no floor — a caller can hand it any TimeSpan, including one
        // below the stock default. This only pins that the fallback DefaultCommandTimeout,
        // the value a caller with no live-configured ceiling in reach actually ships, clears it.
        ReadTimeout(document, "BASH_DEFAULT_TIMEOUT_MS").Should().BeGreaterThan(TimeSpan.FromMinutes(2),
            "the stock 2-minute default killed obedient foreground suite runs (2026-09-01 finding)");
    }

    [Fact]
    public void The_maximum_command_timeout_is_double_whatever_default_was_requested()
    {
        using JsonDocument document = JsonDocument.Parse(ClaudeSettingsFile.Build(TimeSpan.FromMinutes(30)));

        // Double the requested default, so a session's own explicit per-command timeout can still
        // ask for more than the default on a day the suite runs long, whatever the default is.
        ReadTimeout(document, "BASH_MAX_TIMEOUT_MS").Should().Be(TimeSpan.FromMinutes(60),
            "the stock 10-minute cap made foreground compliance impossible on days the suite exceeded it");
    }

    /// <summary>
    /// Reads one of the settings file's own command-timeout values, so an assertion measures what
    /// a session actually receives instead of a literal restated beside it.
    /// </summary>
    private static TimeSpan ReadTimeout(JsonDocument document, string variable)
    {
        JsonElement value = document.RootElement.GetProperty("env").GetProperty(variable);
        value.ValueKind.Should().Be(JsonValueKind.String,
            "Claude Code parses a settings file's env values as strings");

        return TimeSpan.FromMilliseconds(int.Parse(value.ToString(), CultureInfo.InvariantCulture));
    }
}

using System.Diagnostics;
using System.Linq;
using FluentAssertions;
using Hall9k.Connectors.Processes;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The environment a platform-spawned git runs under. Machine-independent by design: the hang
/// this guards against (origin incident 2026-09-05, a fixture rebase falling through to the
/// operator's own editor and signing) only reproduces on a machine configured that way, so the
/// coverage that travels is the shape of the environment itself.
/// </summary>
public sealed class NonInteractiveGitTests
{
    /// <summary>
    /// A fresh <see cref="ProcessStartInfo"/> with any inherited GIT_CONFIG_* or GIT_*EDITOR*
    /// variable stripped. <see cref="ProcessStartInfo.Environment"/> starts as a copy of this
    /// process's own environment, and the gate runner that hosts this test process already
    /// applies <see cref="NonInteractiveGit.Apply"/> to it (origin incident 2026-09-06: a branch
    /// containing that hardening failed its own test gate twice because the inherited
    /// GIT_CONFIG_COUNT=1 made a fresh <see cref="ProcessStartInfo"/> here look pre-populated, and
    /// <see cref="NonInteractiveGit.Apply"/> appended onto it instead of starting from zero). This
    /// keeps the tests below honest about what they are asserting, whether or not the parent
    /// process already applied the same hardening.
    /// </summary>
    private static ProcessStartInfo FreshGitProcessStartInfo()
    {
        ProcessStartInfo startInfo = new();

        foreach (string key in startInfo.Environment.Keys
            .Where(key => key.StartsWith("GIT_CONFIG_", StringComparison.Ordinal)
                || key.Contains("EDITOR", StringComparison.Ordinal))
            .ToList())
        {
            startInfo.Environment.Remove(key);
        }

        return startInfo;
    }

    [Fact]
    public void Every_interactive_knob_is_pinned_off()
    {
        ProcessStartInfo startInfo = FreshGitProcessStartInfo();

        NonInteractiveGit.Apply(startInfo);

        startInfo.Environment["GIT_EDITOR"].Should().Be("true");
        startInfo.Environment["GIT_SEQUENCE_EDITOR"].Should().Be(":");
        startInfo.Environment["GIT_TERMINAL_PROMPT"].Should().Be("0");
        startInfo.Environment["GIT_CONFIG_COUNT"].Should().Be("1");
        startInfo.Environment["GIT_CONFIG_KEY_0"].Should().Be("commit.gpgsign");
        startInfo.Environment["GIT_CONFIG_VALUE_0"].Should().Be("false");
    }

    [Fact]
    public void An_inherited_config_count_is_appended_to_rather_than_overwritten()
    {
        ProcessStartInfo startInfo = new();
        startInfo.Environment["GIT_CONFIG_COUNT"] = "2";
        startInfo.Environment["GIT_CONFIG_KEY_0"] = "user.name";
        startInfo.Environment["GIT_CONFIG_VALUE_0"] = "Test";
        startInfo.Environment["GIT_CONFIG_KEY_1"] = "user.email";
        startInfo.Environment["GIT_CONFIG_VALUE_1"] = "test@test";

        NonInteractiveGit.Apply(startInfo);

        startInfo.Environment["GIT_CONFIG_COUNT"].Should().Be("3");
        startInfo.Environment["GIT_CONFIG_KEY_0"].Should().Be("user.name");
        startInfo.Environment["GIT_CONFIG_KEY_1"].Should().Be("user.email");
        startInfo.Environment["GIT_CONFIG_KEY_2"].Should().Be("commit.gpgsign");
        startInfo.Environment["GIT_CONFIG_VALUE_2"].Should().Be("false");
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("-5")]
    [InlineData("three")]
    public void An_unusable_inherited_count_starts_over_at_zero(string inherited)
    {
        // -1 is the one value git reads as "zero entries" rather than refusing outright, which
        // would drop the signing override silently; every other bad value is already fatal to
        // git before this helper runs. Either way the helper repairs it instead of appending.
        ProcessStartInfo startInfo = FreshGitProcessStartInfo();
        startInfo.Environment["GIT_CONFIG_COUNT"] = inherited;

        NonInteractiveGit.Apply(startInfo);

        startInfo.Environment["GIT_CONFIG_COUNT"].Should().Be("1");
        startInfo.Environment["GIT_CONFIG_KEY_0"].Should().Be("commit.gpgsign");
        startInfo.Environment["GIT_CONFIG_VALUE_0"].Should().Be("false");
        startInfo.Environment.Keys.Should().NotContain(key => key.Contains("_-", StringComparison.Ordinal));
    }
}

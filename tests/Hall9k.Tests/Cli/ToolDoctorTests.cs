using System.ComponentModel;
using FluentAssertions;
using Hall9k.Cli.Diagnostics;
using Hall9k.Connectors.Processes;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="ToolDoctor"/> against a database it cannot reach at all — nothing configured, an
/// unreachable address, a broken config file: exactly the not-yet-installed or
/// unconfigured-install shapes. It has to degrade to the git-only check rather than throw or hang,
/// since it runs before <c>DoctorCommand</c>'s own database section and must still say something
/// useful on that section's early-return path.
/// <para>
/// Every case here hands the doctor its own <see cref="ConnectionStringSource"/> — a config file
/// this class wrote in a directory of its own, or a connection string it holds — so nothing it
/// observes depends on the machine's <c>HALL9K_CONNECTION_STRING</c>, its real
/// <see cref="Hall9kDatabase.ConfigFile"/>, or its live Postgres. That is why this class sets no
/// environment variable and carries no <c>[Collection("Hall9kHome")]</c>: it has no process-wide
/// state left to race anyone over. Origin incident (2026-09-17 11:15 EDT, run 01a0af3c):
/// <see cref="An_unreachable_configured_database_reports_gh_as_unconfirmed_rather_than_silently_skipped"/>
/// failed because it arranged itself by exporting <c>HALL9K_CONNECTION_STRING</c> and capturing
/// the process-wide <c>AnsiConsole.Console</c>, and neither is a test's to own while the suite
/// runs beside it (PLAN.md §16 #220).
/// </para>
/// </summary>
public sealed class ToolDoctorTests : IDisposable
{
    // Holds only the config.json files these tests write. Never HALL9K_HOME: no code path under
    // test resolves a home at all now, so redirecting one would arrange nothing.
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), $"h9k-tool-doctor-{Path.GetRandomFileName()}");

    public ToolDoctorTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, recursive: true);

    /// <summary>The config file a case writes, and points the doctor's own source at.</summary>
    private string ConfigFile => Path.Combine(directory, "config.json");

    [Fact]
    public async Task With_no_connection_string_configured_only_git_is_probed_and_gh_is_unconfirmed()
    {
        RecordingProcessRunner runner = RecordingProcessRunner.Succeeding(string.Empty);

        string output = await RunAgainstOwnConfigFileAsync(runner.Runner);

        runner.Calls.Should().ContainSingle(call => call.FileName == "git");
        runner.Calls.Should().NotContain(call => call.FileName == "gh",
            "no project could be read, so there is no confirmed fact saying gh is needed");
        output.Should().Contain("Could not confirm whether gh needs checking",
            "a machine can be left unconfigured on purpose (install skips writing config.json when "
            + "something is already listening on 5432) while a database full of registered projects "
            + "still exists, so no connection string resolving must not be reported the same as a "
            + "confirmed \"gh is not needed\"");
    }

    [Fact]
    public async Task A_missing_git_is_taught_rather_than_thrown()
    {
        ProcessRunner runner = (fileName, _, _, _) => fileName == "git"
            ? throw new Win32Exception("No such file or directory")
            : Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));

        string output = await RunAgainstOwnConfigFileAsync(runner);

        output.Should().Contain("git is not installed", "a missing tool has to be named, not silently skipped");
        string expectedFix = OperatingSystem.IsWindows() ? "git-scm.com" : "brew install git";
        output.Should().Contain(expectedFix, "the teaching message has to name the actual fix, not just that something is wrong");
    }

    [Fact]
    public async Task An_installed_git_is_reported_quietly()
    {
        RecordingProcessRunner runner = RecordingProcessRunner.Succeeding(string.Empty);

        string output = await RunAgainstOwnConfigFileAsync(runner.Runner);

        // Markup tags are consumed by Spectre before the text reaches the capture writer (the
        // no-color, no-ANSI console ScopedAnsiConsoleCapture builds never emits "[red]" as literal
        // text even when a line is styled red), so this checks the rendered word instead of the
        // tag that would have produced it.
        output.Should().NotContain("not installed", "quiet reporting means no alarm for a tool that is actually present");
        output.Should().Contain("git is installed");
    }

    [Fact]
    public async Task A_git_that_starts_but_exits_nonzero_is_reported_as_not_working()
    {
        // A bare-stub git on a fresh Mac without the Xcode Command Line Tools: the binary exists
        // and starts, but answers "--version" with a non-zero exit instead of throwing.
        ProcessRunner runner = (fileName, _, _, _) => fileName == "git"
            ? Task.FromResult(new ProcessResult(1, string.Empty, "xcrun: error: invalid active developer path"))
            : Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));

        string output = await RunAgainstOwnConfigFileAsync(runner);

        output.Should().NotContain("git is installed",
            "a git that starts but fails to answer is not a git that is actually usable");
        string expectedFix = OperatingSystem.IsWindows() ? "git-scm.com" : "brew install git";
        output.Should().Contain(expectedFix, "the teaching message has to name the actual fix, not just that something is wrong");
    }

    [Fact]
    public async Task An_unreachable_configured_database_reports_gh_as_unconfirmed_rather_than_silently_skipped()
    {
        File.WriteAllText(ConfigFile, $$"""{"connectionString": "{{UnreachableConnectionString}}"}""");
        RecordingProcessRunner runner = RecordingProcessRunner.Succeeding(string.Empty);

        string output = await RunAgainstOwnConfigFileAsync(runner.Runner);

        runner.Calls.Should().ContainSingle(call => call.FileName == "git");
        runner.Calls.Should().NotContain(call => call.FileName == "gh",
            "the projects could not be read, so there is no confirmed fact saying gh is needed");
        output.Should().Contain("Could not confirm whether gh needs checking",
            "an unreachable database must not be reported the same as a confirmed \"gh is not needed\"");
    }

    /// <summary>
    /// The same unreachable case as above, run with this machine's own platform config file left
    /// exactly where it is and whatever it names — the shape that actually failed on 2026-09-17,
    /// where the doctor reached a live local Postgres and printed its rows while the test believed
    /// it had configured an address nothing answers at.
    /// <para>
    /// The discriminating assertion is the <c>gh</c> probe, not the message: a doctor that fell
    /// back to a reachable install holding a GitHub-remote project would probe <c>gh</c>, which is
    /// precisely what the building node's own config file points at. On a machine with nothing
    /// configured (a CI runner) there is no fallback to catch, so this case proves less there than
    /// here — stated rather than hidden, since no test may write over the real config to make the
    /// stronger case available everywhere.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_machines_own_config_file_is_never_consulted_in_place_of_the_injected_source()
    {
        int consulted = 0;
        ConnectionStringSource unreachable = ConnectionStringSource.Configured(UnreachableConnectionString);
        RecordingProcessRunner runner = RecordingProcessRunner.Succeeding(string.Empty);

        string output = await ScopedAnsiConsoleCapture.CaptureAsync(() => ToolDoctor.RunAsync(
            runner.Runner,
            ConnectionStringSource.From(() =>
            {
                consulted++;
                return unreachable.Resolve();
            }),
            CancellationToken.None));

        consulted.Should().Be(1, "the injected source is the doctor's only way to learn a connection string");
        runner.Calls.Should().NotContain(call => call.FileName == "gh",
            "nothing answers at the injected address, so no registered project could be read — whatever "
            + "this machine's own config.json names");
        output.Should().Contain("Could not confirm whether gh needs checking");
    }

    [Fact]
    public async Task A_malformed_platform_config_file_reports_gh_as_unconfirmed_rather_than_not_needed()
    {
        File.WriteAllText(ConfigFile, "{ not valid json");
        RecordingProcessRunner runner = RecordingProcessRunner.Succeeding(string.Empty);

        string output = await RunAgainstOwnConfigFileAsync(runner.Runner);

        runner.Calls.Should().ContainSingle(call => call.FileName == "git");
        runner.Calls.Should().NotContain(call => call.FileName == "gh",
            "the projects could not be read, so there is no confirmed fact saying gh is needed");
        output.Should().Contain("Could not confirm whether gh needs checking",
            "a broken platform config file is not the same as a confirmed \"gh is not needed\", the same as an unreachable database");
    }

    [Fact]
    public async Task An_unreadable_platform_config_file_reports_gh_as_unconfirmed_rather_than_not_needed()
    {
        using FileStream lockHandle = new(ConfigFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        lockHandle.Write("""{"connectionString": "config-value"}"""u8);
        lockHandle.Flush();
        RecordingProcessRunner runner = RecordingProcessRunner.Succeeding(string.Empty);

        string output = await RunAgainstOwnConfigFileAsync(runner.Runner);

        runner.Calls.Should().ContainSingle(call => call.FileName == "git");
        runner.Calls.Should().NotContain(call => call.FileName == "gh",
            "the projects could not be read, so there is no confirmed fact saying gh is needed");
        output.Should().Contain("Could not confirm whether gh needs checking",
            "a broken platform config file is not the same as a confirmed \"gh is not needed\", the same as an unreachable database");
    }

    /// <summary>
    /// The doctor pointed at this class's own config file — the arrangement every case but
    /// <see cref="The_machines_own_config_file_is_never_consulted_in_place_of_the_injected_source"/>
    /// wants, whether that file holds an unreachable address, invalid JSON, or does not exist at all.
    /// </summary>
    private Task<string> RunAgainstOwnConfigFileAsync(ProcessRunner runner) =>
        ScopedAnsiConsoleCapture.CaptureAsync(() => ToolDoctor.RunAsync(
            runner, ConnectionStringSource.PlatformConfigFile(ConfigFile), CancellationToken.None));

    /// <summary>Port 1 is reserved and never listened on, so a probe of it fails fast rather than hanging out the doctor's own 3s budget.</summary>
    private const string UnreachableConnectionString = "Host=127.0.0.1;Port=1;Database=nope;Username=nope;Password=nope";
}

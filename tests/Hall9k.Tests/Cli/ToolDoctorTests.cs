using System.ComponentModel;
using FluentAssertions;
using Hall9k.Cli.Diagnostics;
using Hall9k.Connectors.Processes;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Tests.Fakes;
using Spectre.Console;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="ToolDoctor"/> against a database it cannot reach at all — no connection string
/// configured, exactly the not-yet-installed or unconfigured-install shape. It has to degrade to
/// the git-only check rather than throw or hang, since it runs before <c>DoctorCommand</c>'s own
/// database section and must still say something useful on that section's early-return path.
/// </summary>
// HALL9K_HOME and HALL9K_CONNECTION_STRING are process-wide state; sharing the collection
// serializes this against every other test that redirects the same environment.
[Collection("Hall9kHome")]
public sealed class ToolDoctorTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), $"h9k-tool-doctor-{Path.GetRandomFileName()}");
    private readonly string? previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");
    private readonly string? previousConnectionString =
        Environment.GetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName);

    public ToolDoctorTests()
    {
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("HALL9K_HOME", home);
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", previousHome);
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, previousConnectionString);
        Directory.Delete(home, recursive: true);
    }

    [Fact]
    public async Task With_no_connection_string_configured_only_git_is_probed_and_gh_is_unconfirmed()
    {
        RecordingProcessRunner runner = RecordingProcessRunner.Succeeding(string.Empty);

        string output = await CaptureAsync(() => ToolDoctor.RunAsync(runner.Runner, CancellationToken.None));

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

        string output = await CaptureAsync(() => ToolDoctor.RunAsync(runner, CancellationToken.None));

        output.Should().Contain("git is not installed", "a missing tool has to be named, not silently skipped");
        string expectedFix = OperatingSystem.IsWindows() ? "git-scm.com" : "brew install git";
        output.Should().Contain(expectedFix, "the teaching message has to name the actual fix, not just that something is wrong");
    }

    [Fact]
    public async Task An_installed_git_is_reported_quietly()
    {
        RecordingProcessRunner runner = RecordingProcessRunner.Succeeding(string.Empty);

        string output = await CaptureAsync(() => ToolDoctor.RunAsync(runner.Runner, CancellationToken.None));

        // Markup tags are consumed by Spectre before the text reaches the capture writer (the
        // no-color, no-ANSI console below never emits "[red]" as literal text even when a line is
        // styled red), so this checks the rendered word instead of the tag that would have
        // produced it.
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

        string output = await CaptureAsync(() => ToolDoctor.RunAsync(runner, CancellationToken.None));

        output.Should().NotContain("git is installed",
            "a git that starts but fails to answer is not a git that is actually usable");
        string expectedFix = OperatingSystem.IsWindows() ? "git-scm.com" : "brew install git";
        output.Should().Contain(expectedFix, "the teaching message has to name the actual fix, not just that something is wrong");
    }

    [Fact]
    public async Task An_unreachable_configured_database_reports_gh_as_unconfirmed_rather_than_silently_skipped()
    {
        Environment.SetEnvironmentVariable(
            Hall9kDatabase.EnvironmentVariableName, "Host=127.0.0.1;Port=1;Database=nope;Username=nope;Password=nope");
        RecordingProcessRunner runner = RecordingProcessRunner.Succeeding(string.Empty);

        string output = await CaptureAsync(() => ToolDoctor.RunAsync(runner.Runner, CancellationToken.None));

        runner.Calls.Should().ContainSingle(call => call.FileName == "git");
        runner.Calls.Should().NotContain(call => call.FileName == "gh",
            "the projects could not be read, so there is no confirmed fact saying gh is needed");
        output.Should().Contain("Could not confirm whether gh needs checking",
            "an unreachable database must not be reported the same as a confirmed \"gh is not needed\"");
    }

    [Fact]
    public async Task A_malformed_platform_config_file_reports_gh_as_unconfirmed_rather_than_not_needed()
    {
        File.WriteAllText(Path.Combine(home, "config.json"), "{ not valid json");
        RecordingProcessRunner runner = RecordingProcessRunner.Succeeding(string.Empty);

        string output = await CaptureAsync(() => ToolDoctor.RunAsync(runner.Runner, CancellationToken.None));

        runner.Calls.Should().ContainSingle(call => call.FileName == "git");
        runner.Calls.Should().NotContain(call => call.FileName == "gh",
            "the projects could not be read, so there is no confirmed fact saying gh is needed");
        output.Should().Contain("Could not confirm whether gh needs checking",
            "a broken platform config file is not the same as a confirmed \"gh is not needed\", the same as an unreachable database");
    }

    [Fact]
    public async Task An_unreadable_platform_config_file_reports_gh_as_unconfirmed_rather_than_not_needed()
    {
        string path = Path.Combine(home, "config.json");
        using FileStream lockHandle = new(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        lockHandle.Write("""{"connectionString": "config-value"}"""u8);
        lockHandle.Flush();
        RecordingProcessRunner runner = RecordingProcessRunner.Succeeding(string.Empty);

        string output = await CaptureAsync(() => ToolDoctor.RunAsync(runner.Runner, CancellationToken.None));

        runner.Calls.Should().ContainSingle(call => call.FileName == "git");
        runner.Calls.Should().NotContain(call => call.FileName == "gh",
            "the projects could not be read, so there is no confirmed fact saying gh is needed");
        output.Should().Contain("Could not confirm whether gh needs checking",
            "a broken platform config file is not the same as a confirmed \"gh is not needed\", the same as an unreachable database");
    }

    private static async Task<string> CaptureAsync(Func<Task> action)
    {
        IAnsiConsole original = AnsiConsole.Console;
        StringWriter writer = new();
        IAnsiConsole captured = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });
        captured.Profile.Width = 4096;
        AnsiConsole.Console = captured;
        try
        {
            await action();
            return writer.ToString();
        }
        finally
        {
            AnsiConsole.Console = original;
        }
    }
}

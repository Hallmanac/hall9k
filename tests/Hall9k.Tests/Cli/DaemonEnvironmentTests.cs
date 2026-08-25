using FluentAssertions;
using Hall9k.Cli.DaemonControl;
using Xunit;

namespace Hall9k.Tests.Cli;

public sealed class DaemonEnvironmentTests
{
    [Fact]
    public void The_capture_carries_the_path_and_never_an_unset_variable()
    {
        IReadOnlyList<KeyValuePair<string, string>> captured = DaemonEnvironment.Capture();

        // A registration is only worth writing because of PATH: the daemon resolves
        // claude, gh, and git through it, and a service manager supplies its own.
        captured.Should().ContainSingle(variable => variable.Key == "PATH");
        captured.Should().OnlyContain(variable => variable.Value.Length > 0);
    }

    [Fact]
    public void Tools_missing_from_the_recorded_path_are_named()
    {
        // Stands in for launchd's default PATH, which is what an autostarted daemon gets
        // when the plist carries no environment: git is there, claude and gh are not. The
        // directory is built rather than named because the assertion is about what the
        // code resolves, not about what the host has installed. Origin incident
        // (2026-08-20): the literal /usr/bin:/bin:/usr/sbin:/sbin passed on a Mac and
        // failed CI, whose ubuntu image ships gh in /usr/bin.
        string searchDirectory = Directory.CreateTempSubdirectory("h9k-path-").FullName;
        File.WriteAllText(Path.Combine(searchDirectory, "git"), "#!/bin/sh\n");
        try
        {
            IReadOnlyList<string> unresolved = DaemonEnvironment.UnresolvedTools(
                [new KeyValuePair<string, string>("PATH", searchDirectory)]);

            unresolved.Should().Contain("claude");
            unresolved.Should().Contain("gh");
            unresolved.Should().NotContain("git");
        }
        finally
        {
            Directory.Delete(searchDirectory, true);
        }
    }

    [Fact]
    public void A_pinned_claude_is_resolved_by_its_own_path_not_the_search_path()
    {
        string pinned = Path.Combine(Path.GetTempPath(), $"claude-{Path.GetRandomFileName()}");
        File.WriteAllText(pinned, "#!/bin/sh\n");
        try
        {
            IReadOnlyList<string> unresolved = DaemonEnvironment.UnresolvedTools(
            [
                new KeyValuePair<string, string>("PATH", "/usr/bin:/bin"),
                new KeyValuePair<string, string>("HALL9K_CLAUDE_PATH", pinned),
            ]);

            unresolved.Should().NotContain(pinned);
            unresolved.Should().NotContain("claude");
        }
        finally
        {
            File.Delete(pinned);
        }
    }

    [Fact]
    public void An_environment_without_a_path_resolves_nothing()
    {
        IReadOnlyList<string> unresolved = DaemonEnvironment.UnresolvedTools([]);

        unresolved.Should().BeEquivalentTo(["claude", "gh", "git"]);
    }

    [Fact]
    public void On_windows_a_bare_tool_name_resolves_through_pathext()
    {
        // Runs for real only on the Windows CI leg (the AtomicFileWriteTests convention for
        // OS-specific assertions): a Windows install never has a literal file named `git`,
        // only `git.exe` — CreateProcess and cmd.exe both resolve the bare name against
        // PATHEXT. Checking only the bare name reported a correctly-installed tool as
        // missing on every Windows h9k daemon autostart enable.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string searchDirectory = Directory.CreateTempSubdirectory("h9k-pathext-").FullName;
        File.WriteAllText(Path.Combine(searchDirectory, "git.exe"), string.Empty);
        try
        {
            IReadOnlyList<string> unresolved = DaemonEnvironment.UnresolvedTools(
                [new KeyValuePair<string, string>("PATH", searchDirectory)]);

            unresolved.Should().NotContain("git");
        }
        finally
        {
            Directory.Delete(searchDirectory, true);
        }
    }
}

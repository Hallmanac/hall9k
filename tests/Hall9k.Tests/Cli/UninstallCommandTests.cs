using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Processes;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The two tiers <c>h9k uninstall</c> keeps deliberately separate (Decisions Log #83): the
/// machine-local tier (PATH link, ~/.hall9k) is plain file removal, exercised here the same
/// way <c>InstallCommandTests</c> exercises its install-time mirror image; the data tier
/// (the hall9k-postgres container and its volume) never touches a real Docker, standing in
/// with <see cref="RecordingProcessRunner"/> exactly as <c>ContainerRuntimeProbeTests</c> does.
/// </summary>
public sealed class UninstallCommandTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), $"h9k-uninstall-{Path.GetRandomFileName()}");

    public UninstallCommandTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            foreach (string nested in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories))
            {
                File.SetUnixFileMode(
                    nested, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        Directory.Delete(directory, recursive: true);
    }

    // --- InstallOwnedEntries / RemoveInstallOwnedEntries -----------------------------------

    [Fact]
    public void A_machine_that_only_ever_ran_install_ends_with_no_home_at_all()
    {
        string home = Path.Combine(directory, "home");
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        File.WriteAllText(Path.Combine(home, "bin", "h9k"), "cli\n");
        Directory.CreateDirectory(Path.Combine(home, "postgres"));
        File.WriteAllText(Path.Combine(home, "postgres", "docker-compose.yml"), "services: {}\n");
        File.WriteAllText(Path.Combine(home, "h9kd.log"), "log\n");
        File.WriteAllText(Path.Combine(home, "h9kd.log.1"), "rolled-aside log\n");
        File.WriteAllText(Path.Combine(home, "h9kd.pid"), "1234\n");
        File.WriteAllText(Path.Combine(home, "h9kd.lock"), string.Empty);

        IReadOnlyList<string> stillPresent = UninstallCommand.RemoveInstallOwnedEntries(
            UninstallCommand.InstallOwnedEntries(home));
        UninstallCommand.TryRemoveIfEmpty(home);

        stillPresent.Should().BeEmpty();
        Directory.Exists(home).Should().BeFalse(
            "bin/, postgres/, h9kd.log, h9kd.log.1, h9kd.pid, and h9kd.lock are all install-owned, and "
            + "nothing else was ever there — config.json is never install's to write in the first place, "
            + "and the canonical skill set is a separate removal path (SkillSeeder.RemovePublished) and is "
            + "not part of this one.");
    }

    [Fact]
    public void The_rotated_log_is_swept_alongside_the_live_one()
    {
        // DaemonLogRotation writes h9kd.log.1 once the live log passes its size budget. Before
        // this was named here, an uninstall on a machine whose daemon had rotated a log left
        // that file behind, the home was never removed, and the summary blamed the operator for
        // a file the platform itself wrote.
        string home = Path.Combine(directory, "home");

        UninstallCommand.InstallOwnedEntries(home).Should().Contain(Path.Combine(home, "h9kd.log.1"));
    }

    [Fact]
    public void A_registered_project_home_survives_uninstall()
    {
        // The safety property this whole feature rests on: "the work" includes a project's
        // real git clones and worktrees, which live under the SAME ~/.hall9k as the install's
        // own files. Deleting projects/<name> would be exactly the "taking the work with it"
        // this command exists not to do.
        string home = Path.Combine(directory, "home");
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        File.WriteAllText(Path.Combine(home, "config.json"), "{}");
        string projectHome = Path.Combine(home, "projects", "hall9k", "repo", "dev");
        Directory.CreateDirectory(projectHome);
        File.WriteAllText(Path.Combine(projectHome, "uncommitted-work.txt"), "do not delete me\n");
        string credentials = Path.Combine(home, "credentials");
        Directory.CreateDirectory(credentials);
        File.WriteAllText(Path.Combine(credentials, "jira-token"), "secret\n");

        IReadOnlyList<string> stillPresent = UninstallCommand.RemoveInstallOwnedEntries(
            UninstallCommand.InstallOwnedEntries(home));
        UninstallCommand.TryRemoveIfEmpty(home);

        stillPresent.Should().BeEmpty();
        Directory.Exists(home).Should().BeTrue("a project home, config.json, and credentials are still there");
        File.Exists(Path.Combine(projectHome, "uncommitted-work.txt")).Should().BeTrue(
            "a project's worktree is real, possibly-uncommitted work, never install's to remove");
        File.Exists(Path.Combine(credentials, "jira-token")).Should().BeTrue(
            "a credential is not something install wrote");
        Directory.Exists(Path.Combine(home, "bin")).Should().BeFalse("bin/ is install-owned and still goes");
        File.Exists(Path.Combine(home, "config.json")).Should().BeTrue(
            "config.json is never install's to write — an operator or h9k doctor's start-offer writes it, "
            + "and it can be the only record of a hand-configured connection string");
    }

    [Fact]
    public void An_absent_home_has_nothing_to_remove()
    {
        string home = Path.Combine(directory, "never-existed");

        UninstallCommand.RemoveInstallOwnedEntries(UninstallCommand.InstallOwnedEntries(home)).Should().BeEmpty();
    }

    [Fact]
    public void A_retired_bin_old_fallback_directory_is_swept_too()
    {
        // InstallCommand.RetireDirectory falls back to a uniquely suffixed bin.old.<random>
        // when bin.old is itself still locked from an earlier run — uninstall has to find that
        // fallback the same way install's own next run does, or it survives forever.
        string home = Path.Combine(directory, "home");
        string fallback = Path.Combine(home, $"bin.old.{Path.GetRandomFileName()}");
        Directory.CreateDirectory(fallback);
        File.WriteAllText(Path.Combine(fallback, "h9k"), "a retired copy\n");

        UninstallCommand.InstallOwnedEntries(home).Should().Contain(fallback);

        IReadOnlyList<string> stillPresent = UninstallCommand.RemoveInstallOwnedEntries(
            UninstallCommand.InstallOwnedEntries(home));

        stillPresent.Should().BeEmpty();
        Directory.Exists(fallback).Should().BeFalse();
    }

    [Fact]
    public void Bin_old_itself_is_never_listed_twice()
    {
        // On Windows, FileSystemName's Win32-expression translation rewrites a trailing ".*"
        // into DOS_DOT, which also matches zero characters — so a glob for "bin.old.*" matches
        // "bin.old" itself, not just its uniquely suffixed fallbacks. InstallCommand already
        // documents and skips this (SweepRetiredDirectories); InstallOwnedEntries lists
        // "bin.old" explicitly too, so without the same exclusion a locked bin.old would appear,
        // and be reported, twice.
        string home = Path.Combine(directory, "home");
        Directory.CreateDirectory(Path.Combine(home, "bin.old"));

        UninstallCommand.InstallOwnedEntries(home)
            .Count(entry => entry == Path.Combine(home, "bin.old"))
            .Should().Be(1);
    }

    [Fact]
    public void A_file_that_cannot_be_deleted_is_named_not_silently_dropped()
    {
        string home = Path.Combine(directory, "locked-home");
        string binDirectory = Path.Combine(home, "bin");
        Directory.CreateDirectory(binDirectory);
        string locked = Path.Combine(binDirectory, "h9k");
        File.WriteAllText(locked, "cli\n");
        File.WriteAllText(Path.Combine(home, "h9kd.log"), "log\n");

        if (OperatingSystem.IsWindows())
        {
            // Unlike Unix, Windows will not delete an executable image mapped into a running
            // process, so this run relocates what is left of bin/ outside the install home
            // instead — the identical rename-not-delete trick InstallCommand.SwapIntoPlace
            // already relies on, since a rename is a directory-entry change that succeeds even
            // while the file is open. The OS loader maps a running executable's image with
            // FILE_SHARE_DELETE granted (that grant is exactly what lets an app rename its own
            // containing directory while it runs), so the lock has to include FileShare.Delete
            // to model that — a plain FileShare.Read handle is stricter than reality and would
            // block the rename too. See
            // Bin_locked_on_Windows_is_relocated_outside_home_instead_of_left_behind for that
            // path's own dedicated coverage; this test's Windows branch only needs to show the
            // relocation keeps the rest of the removal honest.
            using FileStream lockHandle = new(locked, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

            IReadOnlyList<string> stillPresent = UninstallCommand.RemoveInstallOwnedEntries(
                UninstallCommand.InstallOwnedEntries(home));

            stillPresent.Should().BeEmpty(
                "the locked bin/ was relocated outside the install home rather than left behind, so "
                + "nothing here still needs the operator's attention");
            File.Exists(Path.Combine(home, "h9kd.log")).Should().BeFalse(
                "one locked directory must not stop everything else from being removed");
        }
        else
        {
            if (!MadeUnwritable(binDirectory))
            {
                return;
            }

            try
            {
                IReadOnlyList<string> stillPresent = UninstallCommand.RemoveInstallOwnedEntries(
                    UninstallCommand.InstallOwnedEntries(home));

                stillPresent.Should().ContainSingle().Which.Should().Be(locked);
                File.Exists(Path.Combine(home, "h9kd.log")).Should().BeFalse(
                    "one locked directory must not stop everything else from being removed");
            }
            finally
            {
                File.SetUnixFileMode(
                    binDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    [Fact]
    public void A_directory_that_cannot_be_unlinked_from_its_parent_is_named_not_silently_dropped()
    {
        // Every file inside bin/ can delete fine (bin/ itself stays writable) while removing
        // the bin/ directory entry still fails, because that unlink needs write permission on
        // home — bin's parent — which is what this test denies. Before this was fixed,
        // lockedUnderThisEntry stayed empty (no per-file failure explains a parent-permission
        // failure) and the directory silently vanished from the report even though it was
        // still on disk, so the command claimed a removal that never happened.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string home = Path.Combine(directory, "readonly-parent-home");
        Directory.CreateDirectory(home);
        string binDirectory = Path.Combine(home, "bin");
        Directory.CreateDirectory(binDirectory);
        File.WriteAllText(Path.Combine(binDirectory, "h9k"), "cli\n");

        if (!MadeUnwritable(home))
        {
            return;
        }

        try
        {
            IReadOnlyList<string> stillPresent = UninstallCommand.RemoveInstallOwnedEntries(
                UninstallCommand.InstallOwnedEntries(home));

            stillPresent.Should().ContainSingle().Which.Should().Be(binDirectory,
                "bin/'s own contents deleted fine, but bin/ itself could not be unlinked from home, so it "
                + "is still on disk and must be named rather than reported as removed");
        }
        finally
        {
            File.SetUnixFileMode(
                home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Bin_locked_on_Windows_is_relocated_outside_home_instead_of_left_behind()
    {
        // The concrete scenario this exists for: h9k uninstall runs from the very binary it is
        // trying to remove. Windows will not delete an executable image mapped into a running
        // process, so without this, ~/.hall9k/bin could never be fully removed on Windows and
        // the command would report "Not fully removed" on every single run.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string home = Path.Combine(directory, "windows-home");
        string binDirectory = Path.Combine(home, "bin");
        Directory.CreateDirectory(binDirectory);
        string locked = Path.Combine(binDirectory, "h9k");
        File.WriteAllText(locked, "cli\n");

        // The OS loader maps a running executable's image with FILE_SHARE_DELETE granted, which
        // is exactly what lets an app rename its own containing directory while it runs — the
        // lock has to include FileShare.Delete to model that; see the sibling test above for the
        // full explanation of why a plain FileShare.Read handle would be stricter than reality.
        using FileStream lockHandle = new(locked, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

        IReadOnlyList<string> stillPresent = UninstallCommand.RemoveInstallOwnedEntries(
            UninstallCommand.InstallOwnedEntries(home));

        stillPresent.Should().BeEmpty();
        Directory.Exists(binDirectory).Should().BeFalse("bin/ was moved out of the install home, not merely emptied");
    }

    // --- RemoveFromPath --------------------------------------------------------------------

    [Fact]
    public void The_symlink_pointing_at_the_installed_binary_is_removed()
    {
        string binDirectory = Path.Combine(directory, "path-entry");
        Directory.CreateDirectory(binDirectory);
        string target = InstalledBinary();
        string link = Path.Combine(binDirectory, "h9k");
        File.CreateSymbolicLink(link, target);

        bool removed = UninstallCommand.RemoveFromPath(target, binDirectory, Path.Combine(directory, "home"));

        removed.Should().BeTrue();
        File.Exists(link).Should().BeFalse();
        InstallCommand.Classify(link).Should().Be(InstallCommand.PathEntry.Absent);
    }

    [Fact]
    public void A_symlink_that_cannot_be_deleted_is_reported_as_not_removed()
    {
        // Before this returned a verdict, a locked PATH link was printed as a warning but the
        // caller had no way to know the removal failed, so the summary and exit code both
        // claimed the PATH link came off regardless.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string binDirectory = Path.Combine(directory, "readonly-path-entry");
        Directory.CreateDirectory(binDirectory);
        string target = InstalledBinary();
        string link = Path.Combine(binDirectory, "h9k");
        File.CreateSymbolicLink(link, target);

        if (!MadeUnwritable(binDirectory))
        {
            return;
        }

        try
        {
            bool removed = UninstallCommand.RemoveFromPath(target, binDirectory, Path.Combine(directory, "home"));

            removed.Should().BeFalse("deleting the symlink needs write permission on its containing directory, which was denied");
            InstallCommand.Classify(link).Should().Be(InstallCommand.PathEntry.Symlink, "the link is still there — it was never removed");
        }
        finally
        {
            File.SetUnixFileMode(
                binDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void A_real_file_named_h9k_is_never_removed()
    {
        string binDirectory = Path.Combine(directory, "path-entry");
        Directory.CreateDirectory(binDirectory);
        string realFile = Path.Combine(binDirectory, "h9k");
        File.WriteAllText(realFile, "someone else's h9k\n");

        UninstallCommand.RemoveFromPath(InstalledBinary(), binDirectory, Path.Combine(directory, "home"));

        File.Exists(realFile).Should().BeTrue("a real file on the PATH is never install's, or uninstall's, to remove");
    }

    [Fact]
    public void A_symlink_pointing_somewhere_else_is_left_alone()
    {
        string binDirectory = Path.Combine(directory, "path-entry");
        Directory.CreateDirectory(binDirectory);
        string link = Path.Combine(binDirectory, "h9k");
        string somewhereElse = Path.Combine(directory, "not-the-installed-binary");
        File.WriteAllText(somewhereElse, "not ours\n");
        File.CreateSymbolicLink(link, somewhereElse);

        UninstallCommand.RemoveFromPath(InstalledBinary(), binDirectory, Path.Combine(directory, "home"));

        InstallCommand.Classify(link).Should().Be(InstallCommand.PathEntry.Symlink,
            "a link pointing at a different binary is not this install's to remove");
    }

    [Fact]
    public void The_fallback_local_bin_link_is_found_and_removed()
    {
        string home = Path.Combine(directory, "home");
        string target = InstalledBinary();
        string fallbackDirectory = Path.Combine(home, ".local", "bin");
        Directory.CreateDirectory(fallbackDirectory);
        string link = Path.Combine(fallbackDirectory, "h9k");
        File.CreateSymbolicLink(link, target);

        UninstallCommand.RemoveFromPath(target, pathVariable: string.Empty, homeDirectory: home);

        File.Exists(link).Should().BeFalse("~/.local/bin is install's fallback link location too");
    }

    /// <summary>A stand-in for ~/.hall9k/bin/h9k, matching InstallCommandTests' own helper.</summary>
    private string InstalledBinary()
    {
        string bin = Path.Combine(directory, "hall9k-bin");
        Directory.CreateDirectory(bin);
        string binary = Path.Combine(bin, "h9k");
        File.WriteAllText(binary, "the installed binary\n");
        return binary;
    }

    private static bool MadeUnwritable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        string probe = Path.Combine(path, "probe");
        try
        {
            File.WriteAllText(probe, "probe\n");
            File.Delete(probe);
            return false;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    // --- ComputeUserPathWithoutDirectory -----------------------------------------------------

    [Fact]
    public void The_installed_directory_is_dropped_from_the_user_path()
    {
        string others = $"/usr/bin{Path.PathSeparator}/bin";
        string current = $"/opt/hall9k/bin{Path.PathSeparator}{others}";

        UninstallCommand.ComputeUserPathWithoutDirectory(current, "/opt/hall9k/bin").Should().Be(others);
    }

    [Fact]
    public void A_path_without_the_directory_is_left_alone()
    {
        string current = $"/usr/bin{Path.PathSeparator}/bin";

        UninstallCommand.ComputeUserPathWithoutDirectory(current, "/opt/hall9k/bin").Should().Be(current);
    }

    [Fact]
    public void Removal_ignores_trailing_separators_and_casing()
    {
        string directoryEntry = Path.Combine("Users", "me", ".hall9k", "bin");
        string current = directoryEntry + Path.DirectorySeparatorChar;

        UninstallCommand.ComputeUserPathWithoutDirectory(
            current, Path.Combine("Users", "me", ".hall9k", "BIN")).Should().BeEmpty();
    }

    [Fact]
    public void Every_other_entry_survives_untouched_including_expandable_references()
    {
        // %JAVA_HOME%\bin (an unexpanded reference) must round-trip exactly — the same
        // never-flatten concern InstallCommand.EnsureOnWindowsPath documents for the write side.
        string javaHome = Path.Combine("%JAVA_HOME%", "bin");
        string current = $"{javaHome}{Path.PathSeparator}/opt/hall9k/bin{Path.PathSeparator}/usr/bin";

        UninstallCommand.ComputeUserPathWithoutDirectory(current, "/opt/hall9k/bin")
            .Should().Be($"{javaHome}{Path.PathSeparator}/usr/bin");
    }

    // --- HandleDataTierAsync -----------------------------------------------------------------

    [Fact]
    public async Task A_running_container_is_stopped_not_removed_by_default()
    {
        RecordingProcessRunner runner = RecordingProcessRunner.Succeeding("running\n");

        (bool ok, UninstallCommand.DataTierOutcome outcome) = await UninstallCommand.HandleDataTierAsync(purgeData: false, runner.Runner, CancellationToken.None);

        ok.Should().BeTrue();
        outcome.Should().Be(UninstallCommand.DataTierOutcome.ContainerStopped);
        runner.Calls.Should().Contain(call => call.Arguments.SequenceEqual(new[] { "stop", "hall9k-postgres" }));
        runner.Calls.Should().NotContain(call => call.Arguments.Count > 0 && call.Arguments[0] == "rm");
        runner.Calls.Should().NotContain(call => call.Arguments.Count > 0 && call.Arguments[0] == "volume");
    }

    [Fact]
    public async Task No_container_means_nothing_to_stop()
    {
        RecordingProcessRunner runner = RecordingProcessRunner.Succeeding(string.Empty);

        (bool ok, UninstallCommand.DataTierOutcome outcome) = await UninstallCommand.HandleDataTierAsync(purgeData: false, runner.Runner, CancellationToken.None);

        ok.Should().BeTrue();
        outcome.Should().Be(UninstallCommand.DataTierOutcome.ContainerAbsent, "no container means no purge happened");
        runner.Calls.Should().NotContain(call => call.Arguments.Count > 0 && call.Arguments[0] == "stop");
    }

    [Fact]
    public async Task Docker_not_running_leaves_the_default_tier_untouched_and_honest()
    {
        RecordingProcessRunner runner = RecordingProcessRunner.Failing("Cannot connect to the Docker daemon");

        (bool ok, UninstallCommand.DataTierOutcome outcome) = await UninstallCommand.HandleDataTierAsync(purgeData: false, runner.Runner, CancellationToken.None);

        ok.Should().BeTrue("nothing reachable is not a failure for the default, non-destructive tier");
        outcome.Should().Be(UninstallCommand.DataTierOutcome.NoContainerRuntime,
            "this is the non-purge tier — nothing was ever asked to be destroyed, and there was no runtime to ask");
    }

    [Fact]
    public async Task Docker_not_running_fails_a_requested_purge_rather_than_claiming_success()
    {
        RecordingProcessRunner runner = RecordingProcessRunner.Failing("Cannot connect to the Docker daemon");

        (bool ok, UninstallCommand.DataTierOutcome outcome) = await UninstallCommand.HandleDataTierAsync(purgeData: true, runner.Runner, CancellationToken.None);

        ok.Should().BeFalse("--purge-data promises destruction; an unreachable Docker cannot honor that silently");
        outcome.Should().Be(UninstallCommand.DataTierOutcome.NoContainerRuntime,
            "nothing could be reached, so nothing could have been destroyed");
        runner.Calls.Should().NotContain(call => call.Arguments.Count > 0 && (call.Arguments[0] == "rm" || call.Arguments[0] == "volume"));
    }

    [Fact]
    public async Task Purge_removes_both_the_container_and_its_volume()
    {
        RecordingProcessRunner runner = null!;
        runner = new RecordingProcessRunner(() => runner.Calls[^1].Arguments switch
        {
            ["ps", ..] => new ProcessResult(0, "running\n", string.Empty),
            ["inspect", ..] => new ProcessResult(0, "hall9k-pgdata\n", string.Empty),
            ["volume", "ls", ..] => new ProcessResult(0, "hall9k-pgdata\n", string.Empty),
            _ => new ProcessResult(0, string.Empty, string.Empty),
        });

        (bool ok, UninstallCommand.DataTierOutcome outcome) = await UninstallCommand.HandleDataTierAsync(purgeData: true, runner.Runner, CancellationToken.None);

        ok.Should().BeTrue();
        outcome.Should().Be(UninstallCommand.DataTierOutcome.PurgedContainerAndVolume,
            "both the container and its named volume were actually removed");
        runner.Calls.Should().Contain(call => call.Arguments.SequenceEqual(new[] { "rm", "-f", "hall9k-postgres" }));
        runner.Calls.Should().Contain(call => call.Arguments.SequenceEqual(new[] { "volume", "rm", "hall9k-pgdata" }));
    }

    [Fact]
    public async Task Purge_removes_the_volume_the_container_actually_mounts_not_the_guessed_literal()
    {
        // A container created before the compose file's name: pin (or brought up from an
        // unpinned checkout) mounts a Compose-project-prefixed volume, e.g.
        // postgres_hall9k-pgdata, never the bare PostgresRuntime.VolumeName literal. Naming
        // the volume by convention instead of asking the container what it actually has
        // mounted either misses the real volume (reporting destruction that never happened)
        // or hits an unrelated same-named volume instead — this uninstall feature's own
        // pre-PR review found both shapes live.
        RecordingProcessRunner runner = null!;
        runner = new RecordingProcessRunner(() => runner.Calls[^1].Arguments switch
        {
            ["ps", ..] => new ProcessResult(0, "running\n", string.Empty),
            ["inspect", ..] => new ProcessResult(0, "postgres_hall9k-pgdata\n", string.Empty),
            ["volume", "ls", ..] => new ProcessResult(0, "postgres_hall9k-pgdata\n", string.Empty),
            _ => new ProcessResult(0, string.Empty, string.Empty),
        });

        (bool ok, UninstallCommand.DataTierOutcome outcome) = await UninstallCommand.HandleDataTierAsync(purgeData: true, runner.Runner, CancellationToken.None);

        ok.Should().BeTrue();
        outcome.Should().Be(UninstallCommand.DataTierOutcome.PurgedContainerAndVolume,
            "the container's real volume was observed and actually removed");
        runner.Calls.Should().Contain(call => call.Arguments.SequenceEqual(new[] { "volume", "rm", "postgres_hall9k-pgdata" }));
        runner.Calls.Should().NotContain(call => call.Arguments.SequenceEqual(new[] { "volume", "rm", "hall9k-pgdata" }),
            "the container's real volume has a different name; guessing the bare literal would miss it");
    }

    [Fact]
    public async Task An_absent_container_with_a_literally_named_volume_is_left_untouched()
    {
        // With hall9k-postgres absent there is nothing left to docker inspect, so which volume
        // is really this install's cannot be observed — only guessed at. A pre-migration Aspire
        // dev-loop volume carries this exact literal name too (PostgresRuntime.VolumeName's own
        // remarks), so guessing here risks destroying someone else's database while reporting
        // success. This is the fix for the defect the pre-PR review found in the fallback that
        // used to run "docker volume rm" against the bare literal in this exact case.
        RecordingProcessRunner runner = null!;
        runner = new RecordingProcessRunner(() => runner.Calls[^1].Arguments switch
        {
            ["volume", "ls", ..] => new ProcessResult(0, "hall9k-pgdata\n", string.Empty),
            _ => new ProcessResult(0, string.Empty, string.Empty),
        });

        (bool ok, UninstallCommand.DataTierOutcome outcome) = await UninstallCommand.HandleDataTierAsync(purgeData: true, runner.Runner, CancellationToken.None);

        ok.Should().BeFalse("ownership of the literally-named volume cannot be confirmed with no container to inspect");
        outcome.Should().Be(UninstallCommand.DataTierOutcome.PurgeUnconfirmedVolume,
            "nothing was destroyed — the volume was left untouched rather than guessed at");
        runner.Calls.Should().NotContain(call => call.Arguments.SequenceEqual(new[] { "volume", "rm", "hall9k-pgdata" }),
            "never destroy a volume whose ownership was never observed");
        runner.Calls.Should().NotContain(call => call.Arguments.Count > 0 && call.Arguments[0] == "rm",
            "there is no container to remove, so docker rm is never asked to fail against one");
    }

    [Fact]
    public async Task A_machine_that_never_created_the_volume_purges_as_a_no_op()
    {
        // Decisions Log #58: install deliberately never starts Postgres, so a fresh install's
        // first --purge-data has no container and no volume to destroy. Before the existence
        // guard, HandleDataTierAsync tried "docker volume rm" anyway and reported the purge as
        // incomplete for a machine already in exactly the state --purge-data asked for.
        RecordingProcessRunner runner = RecordingProcessRunner.Succeeding(string.Empty);

        (bool ok, UninstallCommand.DataTierOutcome outcome) = await UninstallCommand.HandleDataTierAsync(purgeData: true, runner.Runner, CancellationToken.None);

        ok.Should().BeTrue("nothing was ever created, so there is nothing --purge-data needs to remove");
        outcome.Should().Be(UninstallCommand.DataTierOutcome.ContainerAbsent,
            "there was no container and no volume to destroy — the summary must not claim either was destroyed when this ran as a no-op");
        runner.Calls.Should().NotContain(call => call.Arguments.Count > 0 && call.Arguments[0] == "rm",
            "there is no container to remove");
        runner.Calls.Should().NotContain(call => call.Arguments.SequenceEqual(new[] { "volume", "rm", "hall9k-pgdata" }),
            "there is no volume to remove — asking docker to remove one that was never created is a failure, not a no-op");
    }

    [Fact]
    public async Task A_container_with_no_named_volume_mount_purges_the_container_without_guessing_a_volume()
    {
        // A container brought up with a bind mount, or an anonymous volume, has nothing named
        // for `docker inspect` to report — DataVolumeNameAsync returns null. Falling back to the
        // bare PostgresRuntime.VolumeName literal there would be the identical guess the
        // absent-container branch already refuses to make, and could destroy an unrelated
        // volume that happens to carry that literal name (the pre-migration Aspire dev loop's
        // own hall9k-pgdata, per PostgresRuntime.VolumeName's remarks).
        RecordingProcessRunner runner = null!;
        runner = new RecordingProcessRunner(() => runner.Calls[^1].Arguments switch
        {
            ["ps", ..] => new ProcessResult(0, "running\n", string.Empty),
            ["inspect", ..] => new ProcessResult(0, string.Empty, string.Empty),
            _ => new ProcessResult(0, string.Empty, string.Empty),
        });

        (bool ok, UninstallCommand.DataTierOutcome outcome) = await UninstallCommand.HandleDataTierAsync(purgeData: true, runner.Runner, CancellationToken.None);

        ok.Should().BeTrue("the container came off, and there was no named volume to observe and destroy");
        outcome.Should().Be(UninstallCommand.DataTierOutcome.PurgedContainerOnly,
            "no named volume was ever observed — the summary must not claim a data volume was destroyed here");
        runner.Calls.Should().Contain(call => call.Arguments.SequenceEqual(new[] { "rm", "-f", "hall9k-postgres" }));
        runner.Calls.Should().NotContain(call => call.Arguments.Count > 0 && call.Arguments[0] == "volume",
            "no named volume was observed, so nothing should ever be asked about a volume, guessed or otherwise");
    }

    [Fact]
    public async Task A_volume_docker_refuses_to_remove_reports_the_purge_as_incomplete()
    {
        RecordingProcessRunner runner = null!;
        runner = new RecordingProcessRunner(() => runner.Calls[^1].Arguments switch
        {
            ["ps", ..] => new ProcessResult(0, "running\n", string.Empty),
            ["inspect", ..] => new ProcessResult(0, "hall9k-pgdata\n", string.Empty),
            ["volume", "ls", ..] => new ProcessResult(0, "hall9k-pgdata\n", string.Empty),
            ["volume", "rm", ..] => new ProcessResult(1, string.Empty, "volume is in use"),
            _ => new ProcessResult(0, string.Empty, string.Empty),
        });

        (bool ok, UninstallCommand.DataTierOutcome outcome) = await UninstallCommand.HandleDataTierAsync(purgeData: true, runner.Runner, CancellationToken.None);

        ok.Should().BeFalse("the container came off but the data did not — that is not what --purge-data promised");
        outcome.Should().Be(UninstallCommand.DataTierOutcome.PurgeIncomplete, "the volume removal itself failed — nothing was actually destroyed");
    }
}

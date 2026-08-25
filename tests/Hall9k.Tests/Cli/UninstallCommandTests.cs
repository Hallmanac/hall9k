using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.DaemonControl;
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
        File.WriteAllText(Path.Combine(home, "h9kd.stop"), "1234\n");

        List<string> stillPresent = [];
        stillPresent.AddRange(UninstallCommand.RemoveInstallOwnedEntries(
            UninstallCommand.InstallOwnedEntries(home, stillPresent)));
        UninstallCommand.TryRemoveIfEmpty(home, stillPresent);

        stillPresent.Should().BeEmpty();
        Directory.Exists(home).Should().BeFalse(
            "bin/, postgres/, h9kd.log, h9kd.log.1, h9kd.pid, h9kd.lock, and h9kd.stop are all install-owned, "
            + "and nothing else was ever there — config.json is never install's to write in the first place, "
            + "and the canonical skill set is a separate removal path (SkillSeeder.RemovePublished) and is "
            + "not part of this one.");
    }

    [Fact]
    public void A_stale_windows_stop_request_file_is_swept_too()
    {
        // WindowsStopRequestWatcher normally deletes h9kd.stop within a tick of honoring it,
        // but it can survive a force-kill, a crash, or a delete that lost to a lock — left
        // behind, it made a machine that had run nothing but install and uninstall keep a
        // platform-owned file uninstall never enumerated, misattributed to the operator.
        string home = Path.Combine(directory, "home");

        UninstallCommand.InstallOwnedEntries(home, []).Should().Contain(Path.Combine(home, "h9kd.stop"));
    }

    [Fact]
    public void A_stale_claimed_stop_request_copy_is_swept_too()
    {
        // WindowsStopRequestWatcher claims h9kd.stop onto h9kd.stop.claimed before reading it
        // and normally deletes that copy within the same tick, but a read or delete that loses
        // to a lock or a crash mid-claim leaves it behind exactly like its unclaimed sibling —
        // and h9kd.stop's own sweep entry does not cover a different filename.
        string home = Path.Combine(directory, "home");

        UninstallCommand.InstallOwnedEntries(home, []).Should().Contain(
            Path.Combine(home, "h9kd.stop.claimed"));
    }

    [Fact]
    public void A_stale_autostart_launch_script_is_swept_too()
    {
        // WindowsDaemonAutostart.DisableAsync normally deletes h9kd-autostart-launch.vbs
        // itself, but that delete is best-effort and can lose to a lock — left behind, the
        // file can carry a captured PATH (and, before this file's own connection-string fix,
        // a Postgres password) in plain text past an uninstall that otherwise reports clean.
        string home = Path.Combine(directory, "home");

        UninstallCommand.InstallOwnedEntries(home, []).Should().Contain(
            Path.Combine(home, "h9kd-autostart-launch.vbs"));
    }

    [Fact]
    public void The_rotated_log_is_swept_alongside_the_live_one()
    {
        // DaemonLogRotation writes h9kd.log.1 once the live log passes its size budget. Before
        // this was named here, an uninstall on a machine whose daemon had rotated a log left
        // that file behind, the home was never removed, and the summary blamed the operator for
        // a file the platform itself wrote.
        string home = Path.Combine(directory, "home");

        UninstallCommand.InstallOwnedEntries(home, []).Should().Contain(Path.Combine(home, "h9kd.log.1"));
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

        List<string> stillPresent = [];
        stillPresent.AddRange(UninstallCommand.RemoveInstallOwnedEntries(
            UninstallCommand.InstallOwnedEntries(home, stillPresent)));
        UninstallCommand.TryRemoveIfEmpty(home, stillPresent);

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
    public void An_empty_home_that_cannot_be_unlinked_is_named_not_silently_reported_removed()
    {
        // TryRemoveIfEmpty confirms home is empty and then deletes it, but confirming empty and
        // actually unlinking the directory entry are two different operations that can fail
        // independently: dropping the write bit on home's PARENT lets EnumerateFileSystemEntries
        // still succeed (home is genuinely empty) while Directory.Delete(home) itself is denied.
        // Before this was fixed, that denial was swallowed and homeFullyRemoved came back true
        // with the empty home still on disk.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string home = Path.Combine(directory, "undeletable-home");
        Directory.CreateDirectory(home);

        UnixFileMode originalDirectoryMode = File.GetUnixFileMode(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            try
            {
                Directory.Delete(home);
                return; // this environment does not enforce the restriction (e.g. running as root)
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                // Confirmed undeletable — home is still there, proceed with the real assertion.
            }

            List<string> stillPresent = [];
            Action act = () => UninstallCommand.TryRemoveIfEmpty(home, stillPresent);

            act.Should().NotThrow();
            stillPresent.Should().Contain(
                home, "the delete failed, so this run's exit code must not read it as removed");
            Directory.Exists(home).Should().BeTrue();
        }
        finally
        {
            File.SetUnixFileMode(directory, originalDirectoryMode);
        }
    }

    [Fact]
    public void An_absent_home_has_nothing_to_remove()
    {
        string home = Path.Combine(directory, "never-existed");

        UninstallCommand.RemoveInstallOwnedEntries(UninstallCommand.InstallOwnedEntries(home, [])).Should().BeEmpty();
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

        UninstallCommand.InstallOwnedEntries(home, []).Should().Contain(fallback);

        List<string> stillPresent = [];
        stillPresent.AddRange(UninstallCommand.RemoveInstallOwnedEntries(
            UninstallCommand.InstallOwnedEntries(home, stillPresent)));

        stillPresent.Should().BeEmpty();
        Directory.Exists(fallback).Should().BeFalse();
    }

    [Fact]
    public void An_unenumerable_home_is_named_not_thrown_while_sweeping_retired_bin_fallbacks()
    {
        // RetiredBinFallbacks enumerates home itself for bin.old.* fallbacks — the one
        // enumeration in this feature's removal path that used to have no guard, unlike its
        // siblings (DeleteContentsBestEffort, TryRemoveIfEmpty, SkillSeeder's own retiring
        // pass). Before this was fixed, a read bit dropped on home escaped this call site as a
        // raw, uncaught exception after the PATH link was already gone, rather than being named
        // through stillPresent the way every other failure in this removal path already is.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string home = Path.Combine(directory, "unenumerable-home");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "h9kd.log"), "log\n");

        // Execute-only: a known child path (bin, h9kd.log, …) can still be reached by name, but
        // listing home's own entries — the glob this test targets — needs the read bit too.
        File.SetUnixFileMode(home, UnixFileMode.UserExecute);
        try
        {
            Directory.EnumerateFileSystemEntries(home).Any();
            return;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            // Confirmed unenumerable — proceed with the assertion below.
        }

        try
        {
            List<string> stillPresent = [];
            Action act = () => UninstallCommand.InstallOwnedEntries(home, stillPresent);

            act.Should().NotThrow("an unenumerable home must be reported, never left to escape as a raw exception");
            stillPresent.Should().Contain(home);
        }
        finally
        {
            File.SetUnixFileMode(
                home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void An_unenumerable_home_is_named_once_not_twice_across_the_whole_removal_pass()
    {
        // ExecuteAsync runs InstallOwnedEntries (which sweeps RetiredBinFallbacks) and then
        // TryRemoveIfEmpty against the very same stillPresent list, and both hit the identical
        // unreadable-home condition independently. Before this was fixed, an unenumerable home
        // was recorded by both, so the summary listed ~/.hall9k twice under "Could not remove
        // everything install owns" as though it were two distinct leftovers.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string home = Path.Combine(directory, "unenumerable-home");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "h9kd.log"), "log\n");

        File.SetUnixFileMode(home, UnixFileMode.UserExecute);
        try
        {
            Directory.EnumerateFileSystemEntries(home).Any();
            return;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            // Confirmed unenumerable — proceed with the assertion below.
        }

        try
        {
            List<string> stillPresent = [];
            stillPresent.AddRange(UninstallCommand.RemoveInstallOwnedEntries(
                UninstallCommand.InstallOwnedEntries(home, stillPresent)));
            UninstallCommand.TryRemoveIfEmpty(home, stillPresent);

            stillPresent.Count(path => string.Equals(path, home, StringComparison.OrdinalIgnoreCase))
                .Should().Be(1, "home is one leftover, not two, no matter how many steps independently hit it");
        }
        finally
        {
            File.SetUnixFileMode(
                home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
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

        UninstallCommand.InstallOwnedEntries(home, [])
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

            List<string> stillPresent = [];
            stillPresent.AddRange(UninstallCommand.RemoveInstallOwnedEntries(
                UninstallCommand.InstallOwnedEntries(home, stillPresent)));

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
                List<string> stillPresent = [];
                stillPresent.AddRange(UninstallCommand.RemoveInstallOwnedEntries(
                    UninstallCommand.InstallOwnedEntries(home, stillPresent)));

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
            List<string> stillPresent = [];
            stillPresent.AddRange(UninstallCommand.RemoveInstallOwnedEntries(
                UninstallCommand.InstallOwnedEntries(home, stillPresent)));

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
    public void A_directory_symlink_standing_in_for_bin_is_unlinked_not_recursed_into()
    {
        // Directory.Exists follows a directory symlink or junction, so recursing into "bin/"'s
        // contents when bin/ is actually a link would walk through to whatever it points at and
        // delete that directory's contents instead of just removing the link itself.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string home = Path.Combine(directory, "symlinked-bin-home");
        Directory.CreateDirectory(home);
        string outsideTarget = Path.Combine(directory, "outside-target");
        Directory.CreateDirectory(outsideTarget);
        File.WriteAllText(Path.Combine(outsideTarget, "do-not-delete.txt"), "real work\n");
        string binLink = Path.Combine(home, "bin");
        Directory.CreateSymbolicLink(binLink, outsideTarget);

        List<string> stillPresent = [];
        stillPresent.AddRange(UninstallCommand.RemoveInstallOwnedEntries(
            UninstallCommand.InstallOwnedEntries(home, stillPresent)));

        stillPresent.Should().BeEmpty();
        Directory.Exists(binLink).Should().BeFalse("the link itself is install-owned and must go");
        Directory.Exists(outsideTarget).Should().BeTrue(
            "a directory symlink must be unlinked, never recursed into and emptied");
        File.Exists(Path.Combine(outsideTarget, "do-not-delete.txt")).Should().BeTrue();
    }

    [Fact]
    public void A_subdirectory_that_cannot_be_enumerated_is_named_not_thrown()
    {
        // DeleteContentsBestEffort enumerates lazily (Directory.EnumerateFiles /
        // EnumerateDirectories), so a failure surfaces mid-iteration rather than at the initial
        // call. Before this fix, that escaped as a raw, uncaught exception from this
        // point-of-no-return call site (bin/'s own PATH link is already gone by the time this
        // reaches a locked sibling) instead of being named through stillPresent the way every
        // other failure in this method already is.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string home = Path.Combine(directory, "unenumerable-home");
        string binDirectory = Path.Combine(home, "bin");
        string blocked = Path.Combine(binDirectory, "blocked");
        Directory.CreateDirectory(blocked);
        File.WriteAllText(Path.Combine(blocked, "h9k"), "cli\n");

        // Execute-only: a subdirectory can still be traversed into by full path, but listing its
        // own entries — what this test targets — needs the read bit specifically.
        File.SetUnixFileMode(blocked, UnixFileMode.UserExecute);
        try
        {
            Directory.EnumerateFileSystemEntries(blocked).Any();
            return;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            // Confirmed unenumerable — proceed with the assertion below.
        }

        try
        {
            IReadOnlyList<string> stillPresent = null!;
            Action act = () => stillPresent =
                UninstallCommand.RemoveInstallOwnedEntries(UninstallCommand.InstallOwnedEntries(home, []));

            act.Should().NotThrow("an unenumerable directory must be reported, never left to escape as a raw exception");
            stillPresent.Should().Contain(blocked);
        }
        finally
        {
            File.SetUnixFileMode(
                blocked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
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

        List<string> stillPresent = [];
        stillPresent.AddRange(UninstallCommand.RemoveInstallOwnedEntries(
            UninstallCommand.InstallOwnedEntries(home, stillPresent)));

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
    public async Task A_restarting_container_is_still_stopped_not_left_to_come_back()
    {
        // Hall9kContainerStatusAsync collapses every docker ps State besides "running" into
        // Stopped, so a restart-looping container (its restart policy actively bringing it back
        // up) reads exactly like one that is genuinely at rest. Before this was fixed, only the
        // Running case called `docker stop`, so a restarting container was left alone here and
        // could come back under its own restart policy while the rest of the machine was removed
        // around it. docker stop is idempotent, so calling it unconditionally (whenever a
        // container is present at all) costs nothing when it turns out to already be at rest.
        RecordingProcessRunner runner = RecordingProcessRunner.Succeeding("restarting\n");

        (bool ok, UninstallCommand.DataTierOutcome outcome) = await UninstallCommand.HandleDataTierAsync(purgeData: false, runner.Runner, CancellationToken.None);

        ok.Should().BeTrue();
        outcome.Should().Be(UninstallCommand.DataTierOutcome.ContainerStopped);
        runner.Calls.Should().Contain(call => call.Arguments.SequenceEqual(new[] { "stop", "hall9k-postgres" }));
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
    public async Task A_failed_container_status_check_is_reported_not_read_as_absent()
    {
        // docker info succeeds (the runtime is running) but docker ps -a itself then fails —
        // this uninstall feature's own pre-PR review (cycle 4) found that fail-open empty
        // stdout read as a confirmed ContainerAbsent, so a live hall9k-postgres container was
        // never actually stopped while the run claimed there was nothing to stop.
        RecordingProcessRunner runner = null!;
        runner = new RecordingProcessRunner(() => runner.Calls[^1].Arguments switch
        {
            ["ps", ..] => new ProcessResult(1, string.Empty, "Cannot connect to the Docker daemon"),
            _ => new ProcessResult(0, string.Empty, string.Empty),
        });

        (bool ok, UninstallCommand.DataTierOutcome outcome) = await UninstallCommand.HandleDataTierAsync(purgeData: false, runner.Runner, CancellationToken.None);

        ok.Should().BeFalse("the container's real status was never actually observed");
        outcome.Should().Be(UninstallCommand.DataTierOutcome.ContainerStatusCheckFailed);
        runner.Calls.Should().NotContain(call => call.Arguments.Count > 0 && call.Arguments[0] == "stop",
            "nothing confirmed running means nothing here should be stopped either");
    }

    [Fact]
    public async Task A_failed_container_status_check_fails_a_requested_purge_rather_than_guessing_absent()
    {
        RecordingProcessRunner runner = null!;
        runner = new RecordingProcessRunner(() => runner.Calls[^1].Arguments switch
        {
            ["ps", ..] => new ProcessResult(1, string.Empty, "Cannot connect to the Docker daemon"),
            _ => new ProcessResult(0, string.Empty, string.Empty),
        });

        (bool ok, UninstallCommand.DataTierOutcome outcome) = await UninstallCommand.HandleDataTierAsync(purgeData: true, runner.Runner, CancellationToken.None);

        ok.Should().BeFalse("--purge-data promises destruction; a container status that was never actually observed cannot honor that");
        outcome.Should().Be(UninstallCommand.DataTierOutcome.ContainerStatusCheckFailed);
        runner.Calls.Should().NotContain(call => call.Arguments.Count > 0 && (call.Arguments[0] == "rm" || call.Arguments[0] == "volume"),
            "nothing confirmed absent or present means nothing here should be destroyed");
    }

    [Fact]
    public async Task Docker_not_running_leaves_the_default_tier_untouched_and_honest()
    {
        RecordingProcessRunner runner = RecordingProcessRunner.Failing("Cannot connect to the Docker daemon");

        (bool ok, UninstallCommand.DataTierOutcome outcome) = await UninstallCommand.HandleDataTierAsync(purgeData: false, runner.Runner, CancellationToken.None);

        ok.Should().BeTrue("nothing reachable is not a failure for the default, non-destructive tier");
        outcome.Should().Be(UninstallCommand.DataTierOutcome.ContainerRuntimeNotRunning,
            "docker info answered with a nonzero exit — Docker is installed, just not running, which is a "
            + "different fact from no runtime being installed at all");
    }

    [Fact]
    public async Task Docker_not_running_fails_a_requested_purge_rather_than_claiming_success()
    {
        RecordingProcessRunner runner = RecordingProcessRunner.Failing("Cannot connect to the Docker daemon");

        (bool ok, UninstallCommand.DataTierOutcome outcome) = await UninstallCommand.HandleDataTierAsync(purgeData: true, runner.Runner, CancellationToken.None);

        ok.Should().BeFalse("--purge-data promises destruction; an unreachable Docker cannot honor that silently");
        outcome.Should().Be(UninstallCommand.DataTierOutcome.ContainerRuntimeNotRunning,
            "nothing could be reached, so nothing could have been destroyed");
        runner.Calls.Should().NotContain(call => call.Arguments.Count > 0 && (call.Arguments[0] == "rm" || call.Arguments[0] == "volume"));
    }

    [Fact]
    public async Task Docker_not_installed_is_reported_distinctly_from_docker_installed_but_stopped()
    {
        // Collapsing the two into one outcome made the final summary tell an operator with
        // Docker installed but stopped that no container runtime was found on the machine at
        // all — the opposite diagnosis from what was actually observed, and one that points
        // them at "install Docker" when their container and volume are sitting right there.
        RecordingProcessRunner runner = RecordingProcessRunner.Unstartable(
            new System.ComponentModel.Win32Exception("No such file or directory"));

        (bool ok, UninstallCommand.DataTierOutcome outcome) = await UninstallCommand.HandleDataTierAsync(purgeData: false, runner.Runner, CancellationToken.None);

        ok.Should().BeTrue("nothing reachable is not a failure for the default, non-destructive tier");
        outcome.Should().Be(UninstallCommand.DataTierOutcome.NoContainerRuntime,
            "docker itself would not even start — there is no runtime installed to ask");
    }

    [Fact]
    public async Task A_purge_on_a_machine_with_no_docker_at_all_proceeds_rather_than_refusing()
    {
        // docs/operations.md supports a native or remote Postgres reached via
        // HALL9K_CONNECTION_STRING, so a machine with no docker binary is ordinary, not an edge
        // case — and it has no container and no docker-managed volume for --purge-data to have
        // ever created. Before this was fixed, this case refused the whole uninstall and printed
        // "Start Docker, then run h9k uninstall --purge-data again to finish", a remedy nobody on
        // such a machine could ever follow.
        RecordingProcessRunner runner = RecordingProcessRunner.Unstartable(
            new System.ComponentModel.Win32Exception("No such file or directory"));

        (bool ok, UninstallCommand.DataTierOutcome outcome) = await UninstallCommand.HandleDataTierAsync(purgeData: true, runner.Runner, CancellationToken.None);

        ok.Should().BeTrue("no docker runtime means no container and no volume ever existed here to purge");
        outcome.Should().Be(UninstallCommand.DataTierOutcome.NoContainerRuntime);
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
    public async Task An_absent_container_with_only_a_legacy_named_volume_is_left_untouched_not_declared_purged()
    {
        // The ordinary "docker compose down" sequence removes the container but keeps the
        // volume. On a pre-pin install, that volume is the Compose-project-prefixed
        // PostgresRuntime.LegacyVolumeName, not the bare PostgresRuntime.VolumeName literal —
        // checking only the literal read every task, run, and idea it holds as "nothing here",
        // and the purge falsely reported the machine as already data-free.
        RecordingProcessRunner runner = null!;
        runner = new RecordingProcessRunner(() => runner.Calls[^1].Arguments switch
        {
            ["volume", "ls", "--filter", "name=hall9k-pgdata", ..] => new ProcessResult(0, "postgres_hall9k-pgdata\n", string.Empty),
            _ => new ProcessResult(0, string.Empty, string.Empty),
        });

        (bool ok, UninstallCommand.DataTierOutcome outcome) = await UninstallCommand.HandleDataTierAsync(purgeData: true, runner.Runner, CancellationToken.None);

        ok.Should().BeFalse("ownership of the legacy-named volume cannot be confirmed with no container to inspect");
        outcome.Should().Be(UninstallCommand.DataTierOutcome.PurgeUnconfirmedVolume,
            "the volume is real and was found — it must not be reported as nothing to purge");
        runner.Calls.Should().NotContain(call => call.Arguments.Count > 0 && call.Arguments[0] == "rm",
            "there is no container to remove, so docker rm is never asked to fail against one");
        runner.Calls.Should().NotContain(call => call.Arguments.SequenceEqual(new[] { "volume", "rm", "postgres_hall9k-pgdata" }),
            "never destroy a volume whose ownership was never observed");
    }

    [Fact]
    public async Task An_absent_container_with_a_checkout_dirname_prefixed_volume_is_found_not_declared_absent()
    {
        // A contributor's own pre-pin `docker compose up -d` from a repository checkout (before
        // this branch's compose name: pin existed) leaves Compose's project-name derivation
        // in the volume's name too — typically <checkout-dirname>_hall9k-pgdata, which is
        // neither PostgresRuntime.VolumeName nor PostgresRuntime.LegacyVolumeName. Enumerating
        // only those two literals read this volume's every task, run, and idea as "nothing
        // here" and reported the machine as already data-free.
        RecordingProcessRunner runner = null!;
        runner = new RecordingProcessRunner(() => runner.Calls[^1].Arguments switch
        {
            ["volume", "ls", "--filter", "name=hall9k-pgdata", ..] => new ProcessResult(0, "hall9k_platform_hall9k-pgdata\n", string.Empty),
            _ => new ProcessResult(0, string.Empty, string.Empty),
        });

        (bool ok, UninstallCommand.DataTierOutcome outcome) = await UninstallCommand.HandleDataTierAsync(purgeData: true, runner.Runner, CancellationToken.None);

        ok.Should().BeFalse("ownership of the checkout-dirname-prefixed volume cannot be confirmed with no container to inspect");
        outcome.Should().Be(UninstallCommand.DataTierOutcome.PurgeUnconfirmedVolume,
            "the volume is real and was found — it must not be reported as nothing to purge");
        runner.Calls.Should().NotContain(call => call.Arguments.SequenceEqual(new[] { "volume", "rm", "hall9k_platform_hall9k-pgdata" }),
            "never destroy a volume whose ownership was never observed");
    }

    [Fact]
    public async Task An_absent_container_whose_volume_check_itself_fails_is_reported_as_unconfirmed_not_absent()
    {
        // A failed `docker volume ls` is not the same fact as "no matching volume" — reading it
        // that way would report a purge complete ("nothing to purge") when it was never actually
        // observed.
        RecordingProcessRunner runner = null!;
        runner = new RecordingProcessRunner(() => runner.Calls[^1].Arguments switch
        {
            ["info", ..] => new ProcessResult(0, string.Empty, string.Empty),
            ["ps", ..] => new ProcessResult(0, string.Empty, string.Empty),
            ["volume", "ls", ..] => new ProcessResult(1, string.Empty, "Cannot connect to the Docker daemon"),
            _ => new ProcessResult(0, string.Empty, string.Empty),
        });

        (bool ok, UninstallCommand.DataTierOutcome outcome) = await UninstallCommand.HandleDataTierAsync(purgeData: true, runner.Runner, CancellationToken.None);

        ok.Should().BeFalse("the volume check itself failed — nothing was confirmed either way");
        outcome.Should().Be(UninstallCommand.DataTierOutcome.PurgeIncomplete);
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
        // for `docker inspect` to report — DataVolumeNameAsync returns an empty list. Falling back to the
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
    public async Task A_container_present_but_uninspectable_leaves_the_container_and_volume_untouched()
    {
        // A failed docker inspect between confirming the container is present and asking what
        // it mounts is not the same fact as "no named volume" — reading it that way used to
        // let the purge remove the container while believing (and reporting) there had never
        // been a volume to observe, leaving the real data volume behind while claiming success.
        RecordingProcessRunner runner = null!;
        runner = new RecordingProcessRunner(() => runner.Calls[^1].Arguments switch
        {
            ["ps", ..] => new ProcessResult(0, "running\n", string.Empty),
            ["inspect", ..] => new ProcessResult(1, string.Empty, "Error: No such object: hall9k-postgres"),
            _ => new ProcessResult(0, string.Empty, string.Empty),
        });

        (bool ok, UninstallCommand.DataTierOutcome outcome) = await UninstallCommand.HandleDataTierAsync(purgeData: true, runner.Runner, CancellationToken.None);

        ok.Should().BeFalse("the volume mount could not be confirmed, so nothing was safe to destroy");
        outcome.Should().Be(UninstallCommand.DataTierOutcome.PurgeIncomplete);
        runner.Calls.Should().NotContain(call => call.Arguments.Count > 0 && call.Arguments[0] == "rm",
            "removing the container now would destroy the one thing that could still answer what it mounts");
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

    [Fact]
    public async Task A_container_mounting_two_named_volumes_has_both_removed_before_purge_is_declared_complete()
    {
        // A container can mount more than one named volume (a hand-created container, or a
        // pre-pin compose file with a separate volume). Keeping only the first name docker
        // inspect reports would purge that one, leave the second sitting untouched on disk, and
        // still report the whole install's data as gone — the fix for that gap.
        RecordingProcessRunner runner = null!;
        runner = new RecordingProcessRunner(() => runner.Calls[^1].Arguments switch
        {
            ["ps", ..] => new ProcessResult(0, "running\n", string.Empty),
            ["inspect", ..] => new ProcessResult(0, "hall9k-pgdata\nhall9k-wal\n", string.Empty),
            ["volume", "ls", "--filter", "name=^hall9k-pgdata$", ..] => new ProcessResult(0, "hall9k-pgdata\n", string.Empty),
            ["volume", "ls", "--filter", "name=^hall9k-wal$", ..] => new ProcessResult(0, "hall9k-wal\n", string.Empty),
            _ => new ProcessResult(0, string.Empty, string.Empty),
        });

        (bool ok, UninstallCommand.DataTierOutcome outcome) = await UninstallCommand.HandleDataTierAsync(purgeData: true, runner.Runner, CancellationToken.None);

        ok.Should().BeTrue("both named volumes were observed and actually removed");
        outcome.Should().Be(UninstallCommand.DataTierOutcome.PurgedContainerAndVolume);
        runner.Calls.Should().Contain(call => call.Arguments.SequenceEqual(new[] { "volume", "rm", "hall9k-pgdata" }));
        runner.Calls.Should().Contain(call => call.Arguments.SequenceEqual(new[] { "volume", "rm", "hall9k-wal" }));
    }

    [Fact]
    public async Task A_container_mounting_two_named_volumes_reports_incomplete_when_only_one_removal_fails()
    {
        RecordingProcessRunner runner = null!;
        runner = new RecordingProcessRunner(() => runner.Calls[^1].Arguments switch
        {
            ["ps", ..] => new ProcessResult(0, "running\n", string.Empty),
            ["inspect", ..] => new ProcessResult(0, "hall9k-pgdata\nhall9k-wal\n", string.Empty),
            ["volume", "ls", "--filter", "name=^hall9k-pgdata$", ..] => new ProcessResult(0, "hall9k-pgdata\n", string.Empty),
            ["volume", "ls", "--filter", "name=^hall9k-wal$", ..] => new ProcessResult(0, "hall9k-wal\n", string.Empty),
            ["volume", "rm", "hall9k-wal"] => new ProcessResult(1, string.Empty, "volume is in use"),
            _ => new ProcessResult(0, string.Empty, string.Empty),
        });

        (bool ok, UninstallCommand.DataTierOutcome outcome) = await UninstallCommand.HandleDataTierAsync(purgeData: true, runner.Runner, CancellationToken.None);

        ok.Should().BeFalse("one of the two data volumes could not actually be removed");
        outcome.Should().Be(UninstallCommand.DataTierOutcome.PurgeIncomplete,
            "a partial removal must never be reported as the whole install's data being gone");
    }

    // --- FoldAutostartOutcomeIntoStopped -----------------------------------------------------

    [Theory]
    [InlineData(false, DaemonAutostartDisableOutcome.DaemonStopped, true)]
    [InlineData(true, DaemonAutostartDisableOutcome.DaemonStopped, true)]
    [InlineData(true, DaemonAutostartDisableOutcome.NothingStopped, true)]
    [InlineData(false, DaemonAutostartDisableOutcome.NothingStopped, false)]
    public void An_outcome_other_than_DaemonStopping_leaves_or_confirms_stopped(
        bool stoppedSoFar, DaemonAutostartDisableOutcome outcome, bool expected)
    {
        UninstallCommand.FoldAutostartOutcomeIntoStopped(stoppedSoFar, outcome).Should().Be(expected);
    }

    [Fact]
    public void DaemonStopping_overrides_an_already_true_stopped_flag()
    {
        // The direct stop attempt can come back true because it found nothing running yet — then
        // autostart's own DisableAsync observes a daemon launchd started in that gap, signals it,
        // and has not confirmed it exited by the time it stops watching. A plain `||` would let
        // the earlier true mask that: this must come back false instead.
        UninstallCommand.FoldAutostartOutcomeIntoStopped(
            stoppedSoFar: true, DaemonAutostartDisableOutcome.DaemonStopping).Should().BeFalse();
    }
}

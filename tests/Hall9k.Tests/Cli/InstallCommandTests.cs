using FluentAssertions;
using Hall9k.Cli.Commands;
using Spectre.Console;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// What install may do to an h9k already sitting on the PATH. The discrimination
/// matters because the answer decides whether a file on the operator's PATH is
/// replaced: a symlink is install's own to retarget, a real file is never clobbered.
/// </summary>
public sealed class InstallCommandTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), $"h9k-install-{Path.GetRandomFileName()}");

    public InstallCommandTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            // A test that staged an unwritable directory left it that way; the tree cannot
            // be torn down until the write bit is back.
            foreach (string nested in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories))
            {
                File.SetUnixFileMode(
                    nested, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void Nothing_there_is_absent()
    {
        InstallCommand.Classify(Path.Combine(directory, "h9k")).Should().Be(InstallCommand.PathEntry.Absent);
    }

    [Fact]
    public void A_real_file_is_never_ours_to_replace()
    {
        string path = Path.Combine(directory, "h9k");
        File.WriteAllText(path, "someone else's h9k\n");

        InstallCommand.Classify(path).Should().Be(InstallCommand.PathEntry.RealFile);
    }

    [Fact]
    public void A_symlink_is_ours_to_retarget()
    {
        string target = Path.Combine(directory, "installed-h9k");
        File.WriteAllText(target, "the installed binary\n");
        string path = Path.Combine(directory, "h9k");
        File.CreateSymbolicLink(path, target);

        InstallCommand.Classify(path).Should().Be(InstallCommand.PathEntry.Symlink);
    }

    [Fact]
    public void A_symlink_left_dangling_by_a_previous_install_is_still_a_symlink()
    {
        // FileInfo.Exists follows the link and says no; LinkTarget still names the entry
        // as install's own. Reading it as Absent would leave the dead link on the PATH.
        string path = Path.Combine(directory, "h9k");
        File.CreateSymbolicLink(path, Path.Combine(directory, "binary-that-is-gone"));

        InstallCommand.Classify(path).Should().Be(InstallCommand.PathEntry.Symlink);
    }

    [Fact]
    public void A_directory_of_that_name_is_left_alone()
    {
        string path = Path.Combine(directory, "h9k");
        Directory.CreateDirectory(path);

        InstallCommand.Classify(path).Should().Be(InstallCommand.PathEntry.Absent);
    }

    [Fact]
    public void The_fallback_never_clobbers_a_real_file_either()
    {
        // ~/.local/bin is reached precisely when it is *not* on the PATH, so nothing in
        // the PATH sweep vetted it. Linking there unconditionally deleted a real h9k the
        // operator kept in the one place install refused to look.
        string home = Path.Combine(directory, "home");
        string fallbackDirectory = Path.Combine(home, ".local", "bin");
        Directory.CreateDirectory(fallbackDirectory);
        string fallback = Path.Combine(fallbackDirectory, "h9k");
        File.WriteAllText(fallback, "someone else's h9k\n");

        InstallCommand.LinkOntoPath(InstalledBinary(), pathVariable: string.Empty, homeDirectory: home);

        InstallCommand.Classify(fallback).Should().Be(InstallCommand.PathEntry.RealFile);
        File.ReadAllText(fallback).Should().Be("someone else's h9k\n");
    }

    [Fact]
    public void With_nothing_in_the_way_the_fallback_is_linked()
    {
        string home = Path.Combine(directory, "home");
        string target = InstalledBinary();

        InstallCommand.LinkOntoPath(target, pathVariable: string.Empty, homeDirectory: home);

        string fallback = Path.Combine(home, ".local", "bin", "h9k");
        InstallCommand.Classify(fallback).Should().Be(InstallCommand.PathEntry.Symlink);
        new FileInfo(fallback).LinkTarget.Should().Be(target);
    }

    [Fact]
    public void A_path_entry_padded_with_whitespace_still_names_its_directory()
    {
        // A stray space around a PATH entry (a shell profile assembling PATH from
        // fragments) is incidental, not part of the directory name. Reading it literally
        // hides the link install already owns and drops a second h9k somewhere else.
        string binDirectory = Path.Combine(directory, "padded-bin");
        Directory.CreateDirectory(binDirectory);
        string existing = Path.Combine(binDirectory, "h9k");
        File.CreateSymbolicLink(existing, Path.Combine(directory, "a-previous-install"));
        string target = InstalledBinary();

        InstallCommand.LinkOntoPath(
            target, pathVariable: $" {binDirectory} ", homeDirectory: Path.Combine(directory, "home"));

        new FileInfo(existing).LinkTarget.Should().Be(target,
            "the link already on the PATH is install's to retarget");
        Directory.Exists(Path.Combine(directory, "home", ".local", "bin")).Should().BeFalse(
            "with the PATH entry read correctly there was no reason to fall back to ~/.local/bin");
    }

    [Fact]
    public void A_link_install_cannot_retarget_is_reported_not_passed_over()
    {
        // Origin incident (S1-12 pre-PR review): a symlink in a root-owned /usr/local/bin
        // failed to retarget, the sweep moved on in silence, and install announced a link
        // in ~/.local/bin as a success — while the stale link, earlier on the PATH, went on
        // answering every h9k. That is the installed-binary staleness install exists to end,
        // reported as a fresh install.
        string blockedDirectory = Path.Combine(directory, "blocked-bin");
        Directory.CreateDirectory(blockedDirectory);
        string blocked = Path.Combine(blockedDirectory, "h9k");
        File.CreateSymbolicLink(blocked, Path.Combine(directory, "a-previous-install"));
        string home = Path.Combine(directory, "home");
        string target = InstalledBinary();
        if (!MadeUnwritable(blockedDirectory))
        {
            // Windows has no POSIX mode, and root writes through one; on either the case
            // this test describes cannot be staged, so there is nothing to assert.
            return;
        }

        string output = Capture(() => InstallCommand.LinkOntoPath(target, blockedDirectory, home));

        new FileInfo(blocked).LinkTarget.Should().NotBe(target, "the directory refused the rewrite");
        output.Should().Contain(blocked, "the operator is told which entry stayed stale")
            .And.Contain("still resolves", "and what that costs them")
            .And.Contain($"ln -sfn {target} {blocked}", "and how to fix it by hand");
        InstallCommand.Classify(Path.Combine(home, ".local", "bin", "h9k")).Should().Be(
            InstallCommand.PathEntry.Symlink,
            "the fresh binary is still linked somewhere reachable — the warning is about precedence");
    }

    [Fact]
    public void A_link_that_already_names_the_installed_binary_is_left_alone()
    {
        // Retargeting unlinks before it relinks, so a read-only directory defeats it even
        // when the link there is already right. Reading that as a failure would warn about
        // a stale h9k that is not stale.
        string frozenDirectory = Path.Combine(directory, "frozen-bin");
        Directory.CreateDirectory(frozenDirectory);
        string target = InstalledBinary();
        File.CreateSymbolicLink(Path.Combine(frozenDirectory, "h9k"), target);
        string home = Path.Combine(directory, "home");
        if (!MadeUnwritable(frozenDirectory))
        {
            return;
        }

        string output = Capture(() => InstallCommand.LinkOntoPath(target, frozenDirectory, home));

        output.Should().Contain("On PATH").And.NotContain("Could not retarget");
        Directory.Exists(Path.Combine(home, ".local", "bin")).Should().BeFalse(
            "the link on the PATH already points at the installed binary, so there is nothing to fall back to");
    }

    [Fact]
    public void A_repository_pointed_at_by_hand_is_named_in_the_failure()
    {
        // --repo is taken as given, never walked upward, so the message must not describe
        // the upward search — nor prescribe the flag that just failed, which is how an agent
        // self-correcting from the message re-runs the identical command.
        string message = InstallCommand.DescribeMissingRepository(directory);

        message.Should().Contain(directory, "the one directory actually looked in is named")
            .And.Contain("not", "and the message says what --repo does not do")
            .And.Contain("searched upward");
        message.Should().NotContain("point at it with --repo",
            "prescribing --repo as the remedy for a failed --repo is a loop");
    }

    [Fact]
    public void With_no_repository_flag_the_failure_describes_the_upward_walk()
    {
        string message = InstallCommand.DescribeMissingRepository(configured: null);

        message.Should().Contain(Directory.GetCurrentDirectory())
            .And.Contain("above it")
            .And.Contain("--repo <path>", "here the flag really is the remedy");
    }

    /// <summary>
    /// Strips the write bit and confirms the directory actually refuses a new entry. False
    /// when the platform or the caller's privileges make the refusal impossible to stage.
    /// </summary>
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

    /// <summary>What install told the operator: the global console, swapped for a writer and
    /// widened so a path never wraps mid-assertion, then put back.</summary>
    private static string Capture(Action action)
    {
        IAnsiConsole original = AnsiConsole.Console;
        StringWriter writer = new();
        IAnsiConsole captured = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });
        captured.Profile.Width = 4096;
        AnsiConsole.Console = captured;
        try
        {
            action();
            return writer.ToString();
        }
        finally
        {
            AnsiConsole.Console = original;
        }
    }

    /// <summary>A stand-in for ~/.hall9k/bin/h9k: what install would have just published.</summary>
    private string InstalledBinary()
    {
        string bin = Path.Combine(directory, "hall9k-bin");
        Directory.CreateDirectory(bin);
        string binary = Path.Combine(bin, "h9k");
        File.WriteAllText(binary, "the installed binary\n");
        return binary;
    }
}

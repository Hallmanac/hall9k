using System.Diagnostics;
using FluentAssertions;
using Hall9k.Domain.Infrastructure.Processes;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// Git for Windows' launcher is told apart from the real git by where it sits, so the tests build
/// a fake install of empty git.exe files in a temp directory and run on every platform: the
/// resolver reads the file system and a PATH string, and is switched on by a constructor flag
/// rather than by the platform it runs on.
/// </summary>
public sealed class GitForWindowsLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "h9k-gfw-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TemporaryTree.TryDelete(_root);

    private string Touch(params string[] segments)
    {
        string file = Path.Combine([_root, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(file) ?? _root);
        File.WriteAllBytes(file, []);
        return file;
    }

    private string Install(string name, string flavour = "ucrt64")
    {
        Touch(name, "cmd", "git.exe");
        Touch(name, "bin", "git.exe");
        Touch(name, flavour, "bin", "git.exe");
        return Path.Combine(_root, name);
    }

    private static string GitFileName(GitForWindowsLauncher launcher) => launcher.Current()?.RealGit ?? "git";

    private static string PathOf(params string[] entries) => string.Join(Path.PathSeparator, entries);

    private static ProcessStartInfo StartInfo(string fileName, string path)
    {
        ProcessStartInfo startInfo = new(fileName);
        startInfo.Environment["PATH"] = path;
        return startInfo;
    }

    [Theory]
    [InlineData("cmd")]
    [InlineData("bin")]
    public void Starts_the_real_git_when_the_launcher_in_cmd_or_bin_is_first_on_path(string launcherDirectory)
    {
        string install = Install("git");
        string path = PathOf(Path.Combine(install, launcherDirectory), "elsewhere");
        GitForWindowsLauncher launcher = new(path, enabled: true);

        GitFileName(launcher).Should().Be(Path.Combine(install, "ucrt64", "bin", "git.exe"));
    }

    [Fact]
    public void Puts_the_real_gits_directory_immediately_before_the_launchers_and_moves_nothing_else()
    {
        string install = Install("git");
        string path = PathOf("first", Path.Combine(install, "cmd"), "second", "third");
        GitForWindowsLauncher launcher = new(path, enabled: true);
        ProcessStartInfo startInfo = StartInfo("git", path);

        launcher.Apply(startInfo);

        startInfo.FileName.Should().Be(Path.Combine(install, "ucrt64", "bin", "git.exe"));
        startInfo.Environment["PATH"].Should().Be(
            PathOf("first", Path.Combine(install, "ucrt64", "bin"), Path.Combine(install, "cmd"), "second", "third"));
    }

    [Fact]
    public void Applying_twice_adds_the_directory_once()
    {
        string install = Install("git");
        string path = PathOf(Path.Combine(install, "cmd"), "second");
        GitForWindowsLauncher launcher = new(path, enabled: true);
        ProcessStartInfo startInfo = StartInfo("git.exe", path);

        launcher.Apply(startInfo);
        string once = startInfo.Environment["PATH"]!;
        launcher.Apply(startInfo);

        startInfo.Environment["PATH"].Should().Be(once);
    }

    [Fact]
    public void Gives_every_other_program_the_adjusted_path_and_leaves_its_file_name_alone()
    {
        string install = Install("git");
        string path = PathOf(Path.Combine(install, "cmd"));
        GitForWindowsLauncher launcher = new(path, enabled: true);
        ProcessStartInfo startInfo = StartInfo("gh", path);

        launcher.Apply(startInfo);

        startInfo.FileName.Should().Be("gh");
        startInfo.Environment["PATH"].Should().Be(PathOf(Path.Combine(install, "ucrt64", "bin"), Path.Combine(install, "cmd")));
    }

    [Fact]
    public void Changes_nothing_when_the_first_git_on_path_is_the_real_git_itself()
    {
        string install = Install("git");
        string path = PathOf(Path.Combine(install, "ucrt64", "bin"), Path.Combine(install, "cmd"));
        GitForWindowsLauncher launcher = new(path, enabled: true);
        ProcessStartInfo startInfo = StartInfo("git", path);

        launcher.Apply(startInfo);

        GitFileName(launcher).Should().Be("git");
        startInfo.FileName.Should().Be("git");
        startInfo.Environment["PATH"].Should().Be(path);
    }

    [Fact]
    public void Changes_nothing_when_a_launcher_is_only_later_on_path()
    {
        string install = Install("git");
        Touch("other", "git.exe");
        string path = PathOf(Path.Combine(_root, "other"), Path.Combine(install, "cmd"));
        GitForWindowsLauncher launcher = new(path, enabled: true);

        GitFileName(launcher).Should().Be("git");
    }

    [Fact]
    public void Changes_nothing_when_the_install_has_no_real_git_beside_the_launcher()
    {
        Touch("lone", "cmd", "git.exe");
        string path = PathOf(Path.Combine(_root, "lone", "cmd"));
        GitForWindowsLauncher launcher = new(path, enabled: true);

        GitFileName(launcher).Should().Be("git");
    }

    [Fact]
    public void Changes_nothing_when_it_is_switched_off()
    {
        string install = Install("git");
        string path = PathOf(Path.Combine(install, "cmd"));
        GitForWindowsLauncher launcher = new(path, enabled: false);
        ProcessStartInfo startInfo = StartInfo("git", path);

        launcher.Apply(startInfo);

        startInfo.FileName.Should().Be("git");
        startInfo.Environment["PATH"].Should().Be(path);
    }

    [Theory]
    [InlineData("mingw64")]
    [InlineData("clangarm64")]
    public void Finds_the_real_git_under_mingw64_or_clangarm64_when_ucrt64_is_absent(string flavour)
    {
        string install = Install("git", flavour);
        string path = PathOf(Path.Combine(install, "cmd"));
        GitForWindowsLauncher launcher = new(path, enabled: true);

        GitFileName(launcher).Should().Be(Path.Combine(install, flavour, "bin", "git.exe"));
    }

    [Fact]
    public void Resolves_again_from_the_launcher_when_the_real_git_has_moved_and_starts_by_name_when_it_is_gone()
    {
        string install = Install("git");
        string path = PathOf(Path.Combine(install, "cmd"));
        GitForWindowsLauncher launcher = new(path, enabled: true);
        GitFileName(launcher).Should().Be(Path.Combine(install, "ucrt64", "bin", "git.exe"));

        // A Git for Windows update that moves the real git to another flavour's directory.
        File.Delete(Path.Combine(install, "ucrt64", "bin", "git.exe"));
        Touch("git", "mingw64", "bin", "git.exe");
        GitFileName(launcher).Should().Be(Path.Combine(install, "mingw64", "bin", "git.exe"));

        File.Delete(Path.Combine(install, "mingw64", "bin", "git.exe"));
        GitFileName(launcher).Should().Be("git");
    }

    [Fact]
    public void Leaves_a_path_without_the_launchers_directory_as_it_is()
    {
        string install = Install("git");
        GitForWindowsLauncher launcher = new(PathOf(Path.Combine(install, "cmd")), enabled: true);
        ProcessStartInfo startInfo = StartInfo("git", PathOf("a", "b"));

        launcher.Apply(startInfo);

        startInfo.Environment["PATH"].Should().Be(PathOf("a", "b"));
    }

    [Fact]
    public void Reads_a_quoted_path_entry_with_a_trailing_separator_as_the_launchers_directory()
    {
        string install = Install("git");
        string quoted = "\"" + Path.Combine(install, "cmd") + Path.DirectorySeparatorChar + "\"";
        string path = PathOf("first", quoted);
        GitForWindowsLauncher launcher = new(path, enabled: true);
        ProcessStartInfo startInfo = StartInfo("git", path);

        launcher.Apply(startInfo);

        startInfo.Environment["PATH"].Should().Be(PathOf("first", Path.Combine(install, "ucrt64", "bin"), quoted));
    }
}

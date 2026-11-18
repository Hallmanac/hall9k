using FluentAssertions;
using Hall9k.Connectors.ClaudeCode;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// How the account probe's binary is resolved for a direct start. The operating system, PATH, PATHEXT, and file
/// existence are all inputs, so the Windows rules run on every operating system and no process ever starts. Paths
/// compare case-insensitively, as Windows does: the extension comes back spelled the way PATHEXT spells it.
/// </summary>
public sealed class ClaudeCodeBinaryTests
{
    private const string Npm = @"C:\Users\someone\AppData\Roaming\npm";
    private const string Tools = @"C:\tools";
    private const string WindowsPath = Tools + ";" + Npm;

    private static Func<string, bool> Existing(params string[] files) =>
        file => files.Contains(file, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void A_path_holding_only_claude_cmd_starts_the_cmd() =>
        ClaudeCodeBinary.ForDirectStart("claude", windows: true, WindowsPath, null, Existing(Npm + @"\claude.cmd"))
            .Should().BeEquivalentTo(Npm + @"\claude.cmd");

    [Fact]
    public void Claude_exe_beside_claude_cmd_wins_by_pathext_order() =>
        ClaudeCodeBinary.ForDirectStart(
                "claude", windows: true, WindowsPath, null, Existing(Npm + @"\claude.cmd", Npm + @"\claude.exe"))
            .Should().BeEquivalentTo(Npm + @"\claude.exe");

    [Fact]
    public void An_earlier_path_directory_wins_over_a_later_one() =>
        ClaudeCodeBinary.ForDirectStart(
                "claude", windows: true, WindowsPath, null, Existing(Tools + @"\claude.cmd", Npm + @"\claude.exe"))
            .Should().BeEquivalentTo(Tools + @"\claude.cmd");

    [Fact]
    public void A_ps1_or_an_extensionless_shell_script_is_never_chosen_even_when_pathext_lists_it() =>
        ClaudeCodeBinary.ForDirectStart(
                "claude", windows: true, WindowsPath, ".PS1;.EXE;.CMD", Existing(Npm + @"\claude.ps1", Npm + @"\claude"))
            .Should().Be("claude", "nothing startable resolved, so the name goes through as given and fails with its own message");

    [Fact]
    public void Pathext_decides_which_extensions_are_tried() =>
        ClaudeCodeBinary.ForDirectStart("claude", windows: true, WindowsPath, ".EXE", Existing(Npm + @"\claude.cmd"))
            .Should().Be("claude");

    [Fact]
    public void A_pinned_path_is_honored_as_written_or_with_a_pathext_extension()
    {
        ClaudeCodeBinary.ForDirectStart(@"D:\pinned\claude.cmd", windows: true, WindowsPath, null, Existing(@"D:\pinned\claude.cmd"))
            .Should().BeEquivalentTo(@"D:\pinned\claude.cmd");
        ClaudeCodeBinary.ForDirectStart(@"D:\pinned\claude", windows: true, WindowsPath, null, Existing(@"D:\pinned\claude.cmd"))
            .Should().BeEquivalentTo(@"D:\pinned\claude.cmd");
    }

    [Fact]
    public void Off_windows_the_binary_is_started_exactly_as_it_is_today() =>
        ClaudeCodeBinary.ForDirectStart("claude", windows: false, "/usr/local/bin", null, _ => true)
            .Should().Be("claude");
}

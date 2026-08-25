using FluentAssertions;
using Hall9k.Domain.Infrastructure.Storage;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <c>WindowsCommandLine.WrapForCmdExe</c> is the one piece of quoting arithmetic this
/// task's whole Windows story leans on: every <c>cmd.exe /c</c> invocation (agent spawn,
/// the daemon's own detach, the Task Scheduler action, a project's verify gates) uses it
/// to survive cmd's documented "strip the first and last quote character" fallback rule
/// rather than getting mangled by .NET's C-runtime-style ArgumentList escaping. These
/// tests assert the shape of the wrap, not that a real cmd.exe agrees — that is what
/// <c>ProcessManagerParityTests</c> proves by actually running one on Windows CI.
/// </summary>
public sealed class WindowsCommandLineTests
{
    [Fact]
    public void The_wrapped_result_starts_with_the_c_switch()
    {
        WindowsCommandLine.WrapForCmdExe("echo hi").Should().StartWith("/c \"");
    }

    [Fact]
    public void The_outer_quote_is_the_very_first_and_very_last_quote_character()
    {
        // cmd.exe's fallback rule strips exactly the first and last quote character on
        // the whole /c argument line — not a matched inner pair — so the wrap only
        // survives if its own added quotes truly are the outermost ones, however many
        // quoted paths or redirections sit inside.
        string wrapped = WindowsCommandLine.WrapForCmdExe(
            "\"C:\\Program Files\\h9kd.exe\" < NUL >> \"C:\\Users\\someone\\.hall9k\\h9kd.log\" 2>&1");

        wrapped.IndexOf('"').Should().Be(3, "the wrap's own opening quote must be the first quote in the string");
        wrapped.LastIndexOf('"').Should().Be(wrapped.Length - 1, "the wrap's own closing quote must be the last character");
    }

    [Fact]
    public void Stripping_the_outer_quote_pair_recovers_the_original_command_unmangled()
    {
        // This is cmd.exe's own documented fallback rule, applied by hand: strip the
        // first and last character (both quotes) and what remains must be byte-for-byte
        // the command that was wrapped.
        const string command = "set PATH=C:\\tools& \"C:\\h9kd.exe\" < NUL >> \"C:\\log.txt\" 2>&1";
        string wrapped = WindowsCommandLine.WrapForCmdExe(command);

        wrapped.Should().StartWith("/c \"");
        string stripped = wrapped["/c \"".Length..^1];
        stripped.Should().Be(command);
    }

    [Fact]
    public void An_empty_environment_prefix_still_wraps_correctly()
    {
        string wrapped = WindowsCommandLine.WrapForCmdExe("\"C:\\h9kd.exe\" < NUL >> \"C:\\log.txt\" 2>&1");

        wrapped.IndexOf('"').Should().Be(3);
        wrapped.LastIndexOf('"').Should().Be(wrapped.Length - 1);
    }
}

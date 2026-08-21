using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Hall9k.Connectors.Processes;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// How a connector spawns the tool it reads a work item through. The spawn is where the import's
/// verbatim promise is either kept or lost for good: whatever the child's bytes are decoded as at
/// this moment is what gets stored, and nothing downstream can tell a mangled title from one the
/// source really wrote.
/// </summary>
public sealed class ExternalProcessTests
{
    [Fact]
    public void Both_redirected_streams_are_read_as_the_utf8_the_tool_writes()
    {
        // Redirection without an encoding falls back to the console's, which on Windows is the
        // OEM code page while gh emits UTF-8 regardless. An issue titled "Añadir soporte" would
        // be adopted as mojibake, on a platform CI builds for, with no way back.
        ProcessStartInfo startInfo = ExternalProcess.StartInfoFor("gh", ["issue", "view"], "/repos/hall9k");

        startInfo.StandardOutputEncoding.Should().Be(Encoding.UTF8);
        startInfo.StandardErrorEncoding.Should().Be(Encoding.UTF8);
    }

    [Fact]
    public void The_tool_is_run_directly_with_its_arguments_kept_apart()
    {
        ProcessStartInfo startInfo = ExternalProcess.StartInfoFor(
            "gh", ["issue", "view", "owner/repo#42", "--json", "number,title"], "/repos/hall9k");

        startInfo.UseShellExecute.Should().BeFalse("a shell between here and gh is a quoting rule nobody asked for");
        startInfo.RedirectStandardOutput.Should().BeTrue();
        startInfo.RedirectStandardError.Should().BeTrue();
        startInfo.WorkingDirectory.Should().Be("/repos/hall9k");
        startInfo.ArgumentList.Should().Equal("issue", "view", "owner/repo#42", "--json", "number,title");
    }
}

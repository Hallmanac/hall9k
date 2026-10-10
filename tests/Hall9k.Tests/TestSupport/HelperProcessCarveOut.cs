using System.Text.RegularExpressions;
using FluentAssertions;

namespace Hall9k.Tests.TestSupport;

/// <summary>
/// Asserts that a rendered prompt carries the foreground-gates rule's helper-process carve-out:
/// the one supported way to start and stop a dev server (Bash <c>run_in_background</c> as the
/// plain command, <c>TaskStop</c> by id, loaded through <c>ToolSearch</c> when absent) and the
/// ban on stopping anything by name, pattern, or port. Whitespace is collapsed first because the
/// templates hard-wrap their prose.
/// </summary>
public static partial class HelperProcessCarveOut
{
    public static void AssertStated(string prompt, string because)
    {
        string flat = Whitespace().Replace(prompt, " ");

        flat.Should().Contain("start it with Bash `run_in_background` as the plain server command", because);
        flat.Should().Contain("no `nohup`", because);
        flat.Should().Contain("no `setsid`", because);
        flat.Should().Contain("no `disown`", because);
        flat.Should().Contain("stop it before your final message with `TaskStop`, by the id `run_in_background` returned", because);
        flat.Should().Contain("load it through `ToolSearch` first", because);
        flat.Should().Contain("Never stop any process by name, pattern, or port", because);
        flat.Should().Contain("`pkill`", because);
        flat.Should().Contain("`taskkill /IM`", because);
        flat.Should().Contain("`Stop-Process -Name`", because);
        flat.Should().Contain("never send a kill or cleanup command's stderr to /dev/null", because);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

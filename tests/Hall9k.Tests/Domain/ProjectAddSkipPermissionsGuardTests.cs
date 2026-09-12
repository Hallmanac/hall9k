using FluentAssertions;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// A source-level guard for the one call site Decisions Log #181 depends on:
/// <c>ProjectAddCommand</c> is the only <c>src/</c> caller of <c>ProjectDecider.Register</c>, and
/// it must pass <c>skipPermissions: true</c>. Nothing exercises <c>ProjectAddCommand</c> itself —
/// every behavioural test on this decision (<c>ProjectDeciderTests</c>,
/// <c>ProjectAggregate</c>/<c>ProjectDetailsProjection</c> replay tests) calls
/// <c>ProjectDecider.Register</c> directly and simulates the command's own call rather than
/// running it, so dropping the argument (easy, since the parameter is optional and defaults to
/// <c>false</c>) would leave every test on this branch green while a freshly registered project
/// silently went back to stalling every headless dispatch on it (independent pre-PR review, cycle
/// 1, adversarial lens). Deliberately a source scan rather than a behavioural test, the same
/// reasoning <see cref="StackedBaseBranchGuardTests"/> gives for its own call-site scans: what
/// actually regresses here is the argument being dropped or the call site moving, not a runtime
/// behaviour a fixture would exercise differently.
/// </summary>
public sealed class ProjectAddSkipPermissionsGuardTests
{
    [Fact]
    public void The_only_src_caller_of_project_register_passes_skip_permissions_true()
    {
        List<string> offenders = [];
        int callSites = 0;

        foreach (string file in Directory.EnumerateFiles(
            TestSourceTree.SourceDirectory(), "*.cs", SearchOption.AllDirectories))
        {
            if (TestSourceTree.IsBuildOutput(TestSourceTree.SourceDirectory(), file))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal)
                    || !lines[i].Contains("ProjectDecider.Register(", StringComparison.Ordinal))
                {
                    continue;
                }

                callSites++;

                // The call's own argument list wraps onto several lines; wide enough to cover
                // ProjectAddCommand's own call (ten lines of positional arguments) without
                // reaching into unrelated code that follows it.
                string window = string.Join(' ', lines.Skip(i).Take(15));
                if (!window.Contains("skipPermissions: true", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "every src/ call to ProjectDecider.Register must pass skipPermissions: true (Decisions "
            + "Log #181) — a freshly registered project left without it stalls every headless "
            + "dispatch on the first permission prompt it cannot answer, and no behavioural test "
            + "here calls ProjectAddCommand itself to catch the regression at runtime");

        callSites.Should().Be(
            1,
            "this guard is written for exactly one src/ caller (ProjectAddCommand); a second one "
            + "needs this test's own window/positive-control assumptions revisited, not silently "
            + "folded into the same count");
    }
}

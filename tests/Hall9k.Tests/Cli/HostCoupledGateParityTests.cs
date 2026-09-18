using System.Reflection;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Project;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="HostCoupledGate"/> is a hand-copied mirror of three <c>VerificationRunner</c>
/// algorithms (<c>ComposeGateCommand</c>, <c>ApplyTestFilter</c>, <c>IsDotnetTestGate</c>) and its
/// own node-wide permit's lock file name, kept separate because <c>Hall9k.Cli</c> cannot reference
/// <c>Hall9k.Daemon</c> — see <see cref="HostCoupledGate"/>'s own doc comment. Nothing in
/// <c>tests/</c> referenced <c>HostCoupledGate</c> at all before this file (independent pre-PR
/// review, cycle 3, both lenses, low): the two copies could diverge silently, and <c>h9k task
/// verify</c> would then spawn a differently-filtered command than the daemon spawns for the
/// identical gate. Same shape as <see cref="Hall9k.Tests.Daemon.ProcessManagerParityTests"/> —
/// identical assertions against two independently-maintained implementations — except the two
/// implementations mirrored here are meant to behave byte-identically, not merely equivalently, so
/// every case below asserts the CLI's own answer equals the daemon's rather than checking each in
/// isolation.
/// </summary>
public sealed class HostCoupledGateParityTests
{
    [Theory]
    [InlineData("dotnet test", null)]
    [InlineData("dotnet test", "Category=Hall9kHome")]
    [InlineData("""dotnet test --filter "Category=Existing" """, "Category=Hall9kHome")]
    [InlineData("dotnet build", "Category=Hall9kHome")]
    [InlineData("dotnet test | tail -200", "Category=RealProcessSpawn")]
    public void ComposeGateCommand_matches_the_daemons_own_copy(string command, string? hostCoupledFilter)
    {
        VerifyCommand gate = new("gate", command, hostCoupledFilter);

        string cliResult = HostCoupledGate.ComposeGateCommand(gate);
        string daemonResult = VerificationRunner.ComposeGateCommand(gate);

        cliResult.Should().Be(daemonResult);
    }

    [Theory]
    [InlineData("dotnet test", "Category=Hall9kHome")]
    [InlineData("""dotnet test --filter "Category=Existing" """, "Category=Hall9kHome")]
    [InlineData("""dotnet test --filter='Category=Existing'""", "Category=RealProcessSpawn")]
    [InlineData("dotnet test --filter Category=Existing", "Category=RealProcessSpawn")]
    [InlineData("dotnet test&&dotnet format", "Category=Hall9kHome")]
    [InlineData("dotnet test|tail -200", "Category=Hall9kHome")]
    [InlineData("dotnet test;echo done", "Category=Hall9kHome")]
    public void ApplyTestFilter_matches_the_daemons_own_copy(string command, string filterExpression)
    {
        string cliResult = HostCoupledGate.ApplyTestFilter(command, filterExpression);
        string daemonResult = VerificationRunner.ApplyTestFilter(command, filterExpression);

        cliResult.Should().Be(daemonResult);
    }

    [Theory]
    [InlineData("dotnet test")]
    [InlineData("dotnet test --filter Category=X")]
    [InlineData("dotnet build")]
    [InlineData("  dotnet   test")]
    [InlineData("dotnettest")]
    public void IsDotnetTestGate_matches_the_daemons_own_copy(string command)
    {
        HostCoupledGate.IsDotnetTestGate(command).Should().Be(VerificationRunner.IsDotnetTestGate(command));
    }

    /// <summary>
    /// The two node-wide permits have to lock the identical file, or a daemon run's host-coupled
    /// gate and an operator's own concurrent <c>h9k task verify</c> stop serializing against each
    /// other with nothing failing red to say so — see <see cref="HostCoupledGate"/>'s own doc
    /// comment on its <c>LockFileName</c> constant. Read by reflection rather than made
    /// <c>internal</c> on either side: both are deliberately <c>private const</c>, an
    /// implementation detail neither class exposes for any other reason, and this is the one test
    /// that needs to reach in and check it.
    /// </summary>
    [Fact]
    public void The_two_permits_lock_the_identical_file_name()
    {
        string cliLockFileName = ReadPrivateConstString(typeof(HostCoupledGate), "LockFileName");
        string daemonLockFileName = ReadPrivateConstString(typeof(VerificationRunner), "HostCoupledGateLockFileName");

        cliLockFileName.Should().Be(daemonLockFileName);
    }

    private static string ReadPrivateConstString(Type type, string fieldName)
    {
        FieldInfo field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"{type.FullName} no longer declares a field named '{fieldName}'");
        return (string)field.GetRawConstantValue()!;
    }
}

/// <summary>
/// <see cref="HostCoupledGate.LooksLikeNoTestsExecuted"/> has no daemon-side equivalent to compare
/// against — its own doc comment explains why it deliberately reads differently from
/// <c>VerificationRunner.ScopedRunExecutedNoTests</c> — so this is a behavior suite, not a parity
/// one, and was entirely untested before this file (independent pre-PR review, cycle 3, adversarial
/// lens, low).
/// </summary>
public sealed class HostCoupledGateLooksLikeNoTestsExecutedTests
{
    [Fact]
    public void VSTests_own_no_match_warning_is_positive_evidence()
    {
        HostCoupledGate.LooksLikeNoTestsExecuted(
            "No test matches the given testcase filter `Category=Nothing`").Should().BeTrue();
    }

    [Fact]
    public void An_explicit_zero_total_is_positive_evidence()
    {
        HostCoupledGate.LooksLikeNoTestsExecuted("Passed!  Total: 0").Should().BeTrue();
    }

    [Fact]
    public void A_nonzero_total_is_not_evidence_of_a_vacuous_filter()
    {
        HostCoupledGate.LooksLikeNoTestsExecuted("Passed!  Total: 42").Should().BeFalse();
    }

    [Fact]
    public void One_project_matching_alongside_another_that_did_not_is_not_vacuous()
    {
        // The multi-project-solution case ScopedRunExecutedNoTests's own sibling doc names: one
        // assembly's Total: line is genuinely nonzero, so the filter did select real work even
        // though a second assembly's own "no test matches" warning also appears in the same tail.
        HostCoupledGate.LooksLikeNoTestsExecuted(
            "No test matches the given testcase filter `Category=X` in Other.Tests.dll\n" +
            "Passed!  Total: 7").Should().BeFalse();
    }

    [Fact]
    public void An_unparseable_count_is_never_read_as_zero()
    {
        // A Total: line whose count overflows int is unreadable, not confirmed zero — treating it
        // as zero would be the false-positive direction this method's own doc says it must never
        // take (independent pre-PR review, cycle 3, adversarial lens, low: the pre-fix
        // `!int.TryParse(...) || count == 0` did exactly that).
        HostCoupledGate.LooksLikeNoTestsExecuted("Passed!  Total: 99999999999999999999").Should().BeFalse();
    }
}

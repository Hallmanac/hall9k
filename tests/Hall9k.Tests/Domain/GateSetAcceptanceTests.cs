using FluentAssertions;
using Hall9k.Connectors.Verification;
using Hall9k.Domain.Features.Project;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <see cref="GateSetAcceptance"/> is the single check behind "a node runs a project's verify
/// gates only after its own operator has accepted that exact gate set" (security review idea
/// 6be68ee2, process-injection finding 1, the local half) — deliberately pure, so every case the
/// dispatcher's hold and the runner's wait actually depend on is exercised here rather than only
/// in the two RequiresDocker classes those consume it from.
/// </summary>
public sealed class GateSetAcceptanceTests
{
    private static readonly VerifyCommand Build = new("build", "dotnet build");
    private static readonly VerifyCommand Test = new("test", "dotnet test");

    [Fact]
    public void A_replicated_change_yields_the_diff()
    {
        IReadOnlyList<VerifyCommand> accepted = [Build, Test];
        IReadOnlyList<VerifyCommand> current = [Build, new VerifyCommand("test", "dotnet test --filter Category=Fast")];

        GateSetAcceptance.Decision decision = GateSetAcceptance.Decide(accepted, current);

        decision.Proceed.Should().BeFalse("the current set never ran through this node's own accept-gates");
        decision.Diff.Should().Equal(
        [
            new GateSetAcceptance.GateDiffLine(GateSetAcceptance.GateDiffLineKind.Unchanged, Build),
            new GateSetAcceptance.GateDiffLine(GateSetAcceptance.GateDiffLineKind.Removed, Test),
            new GateSetAcceptance.GateDiffLine(
                GateSetAcceptance.GateDiffLineKind.Added, current[1]),
        ]);
    }

    [Fact]
    public void Nothing_accepted_yet_holds()
    {
        IReadOnlyList<VerifyCommand> current = [Build, Test];

        GateSetAcceptance.Decide(accepted: null, current).Proceed.Should().BeFalse(
            "nothing has been accepted on this node yet");
    }

    /// <summary>
    /// <see cref="GateSetAcceptance.Decide"/> is pure and order-of-events-blind: it cannot tell "an
    /// operator's own local change advanced the accepted list to match the new current one in a
    /// single act" (h9k project set --verify, ProjectSetCommand's own same-call acceptance) apart
    /// from "the set was never touched at all" (h9k project accept-gates against an already-matching
    /// set) — both reach it as the identical call, two equal lists — so both scenarios are named
    /// here as cases of the one branch they actually exercise, rather than as separate tests
    /// (independent pre-PR review, cycle 1, conformance lens, low: the project's own test-hygiene
    /// guidance flags duplicate coverage through the same seam).
    /// </summary>
    [Theory]
    [InlineData("a local change accepted in the same call that changed it (h9k project set --verify)")]
    [InlineData("a set that was never touched (h9k project accept-gates against an already-matching set)")]
    public void Decide_proceeds_when_current_matches_accepted(string scenario)
    {
        IReadOnlyList<VerifyCommand> gates = [Build, Test, new VerifyCommand("lint", "dotnet format --verify-no-changes")];

        GateSetAcceptance.Decision decision = GateSetAcceptance.Decide(gates, gates);

        decision.Proceed.Should().BeTrue(scenario);
        decision.Diff.Should().BeEmpty();
    }

    [Fact]
    public void An_empty_current_set_never_holds()
    {
        IReadOnlyList<VerifyCommand> accepted = [Build, Test];

        GateSetAcceptance.Decision decision = GateSetAcceptance.Decide(accepted, current: []);

        decision.Proceed.Should().BeTrue("a project with no gates configured executes nothing");
        decision.Diff.Should().BeEmpty();
    }

    [Fact]
    public void A_zero_gate_project_that_gains_a_gate_by_replication_holds()
    {
        GateSetAcceptance.Decision decision = GateSetAcceptance.Decide(accepted: [], current: [Build]);

        decision.Proceed.Should().BeFalse();
        decision.Diff.Should().ContainSingle()
            .Which.Should().Be(new GateSetAcceptance.GateDiffLine(GateSetAcceptance.GateDiffLineKind.Added, Build));
    }

    [Fact]
    public void Accept_gates_records_the_fingerprint_it_printed_and_refuses_when_the_set_moved()
    {
        IReadOnlyList<VerifyCommand> printed = [Build, Test];
        IReadOnlyList<VerifyCommand> unchangedAtCommitTime = [Build, Test];
        IReadOnlyList<VerifyCommand> movedAtCommitTime = [Build];

        GateSetAcceptance.CanRecordAcceptance(printed, unchangedAtCommitTime).Should().BeTrue();
        GateSetAcceptance.CanRecordAcceptance(printed, movedAtCommitTime).Should().BeFalse(
            "another h9k project set --verify (or a replicated change) landed while accept-gates was running");
    }

    /// <summary>
    /// Every gate this feature ever prints — the full list, and a diff line either side of it —
    /// goes through <see cref="Connectors.Text.RelayedText.Printable"/> via
    /// <see cref="GateSetAcceptanceDisplay"/>, so an escape sequence embedded in a command a
    /// teammate's node replicated in can never repaint what accept-gates shows an operator on
    /// screen.
    /// </summary>
    [Fact]
    public void A_command_containing_an_escape_sequence_prints_through_printable()
    {
        VerifyCommand malicious = new("build", "dotnet build\u001b[2Kdotnet build # looks clean");

        string formatted = GateSetAcceptanceDisplay.FormatGate(malicious);

        formatted.Should().NotContain("\u001b");

        GateSetAcceptance.Decision decision = GateSetAcceptance.Decide(accepted: [], current: [malicious]);
        IReadOnlyList<string> diffLines = GateSetAcceptanceDisplay.FormatDiffLines(decision.Diff);
        diffLines.Should().OnlyContain(line => !line.Contains('\u001b'));
    }
}

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

    /// <summary>
    /// An operator's own local change advances the accepted list to match the new current one in a
    /// single act (h9k project set --verify, ProjectSetCommand's own same-call acceptance) —
    /// distinct from "unchanged" below: the list itself is genuinely new, but because it is
    /// accepted the same call that recorded it, the two are equal by the time anything reads them.
    /// </summary>
    [Fact]
    public void A_local_change_proceeds()
    {
        IReadOnlyList<VerifyCommand> newlyAcceptedAndCurrent = [Build, Test, new VerifyCommand("lint", "dotnet format --verify-no-changes")];

        GateSetAcceptance.Decision decision = GateSetAcceptance.Decide(newlyAcceptedAndCurrent, newlyAcceptedAndCurrent);

        decision.Proceed.Should().BeTrue();
        decision.Diff.Should().BeEmpty();
    }

    [Fact]
    public void Accept_gates_then_proceeds()
    {
        IReadOnlyList<VerifyCommand> current = [Build, Test];

        GateSetAcceptance.Decide(accepted: null, current).Proceed.Should().BeFalse(
            "nothing has been accepted on this node yet");

        // h9k project accept-gates records exactly the current list as accepted.
        GateSetAcceptance.Decide(accepted: current, current).Proceed.Should().BeTrue(
            "the just-accepted list is now identical to what is running");
    }

    [Fact]
    public void An_unchanged_set_proceeds()
    {
        IReadOnlyList<VerifyCommand> accepted = [Build, Test];
        IReadOnlyList<VerifyCommand> current = [Build, Test];

        GateSetAcceptance.Decide(accepted, current).Proceed.Should().BeTrue();
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

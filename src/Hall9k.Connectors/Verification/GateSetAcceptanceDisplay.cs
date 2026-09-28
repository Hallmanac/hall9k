using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Project;

namespace Hall9k.Connectors.Verification;

/// <summary>
/// The one place a gate — or a <see cref="GateSetAcceptance.GateDiffLine"/> naming one — is turned
/// into text a terminal actually shows (security review idea 6be68ee2, process-injection finding
/// 1, the local half). Every caller that prints a gate's name, command, or host-coupled filter —
/// <c>h9k project accept-gates</c>, <c>h9k task verify</c>'s own refusal, and the daemon's own hold
/// and wait log lines — goes through <see cref="RelayedText.Printable"/> here, once, rather than at
/// each call site: a gate's command is a shell string an operator wrote, or that replicated in from
/// one, and an escape sequence, a lone carriage return, or a bidi override inside it must never be
/// able to hide what it says on the very screen a human is reading to decide whether to accept it.
/// Lives here rather than in Domain, which cannot reference <see cref="RelayedText"/> at all
/// (Connectors references Domain, never the reverse), and rather than in Cli or Daemon separately,
/// which would be two copies of the identical rule.
/// </summary>
public static class GateSetAcceptanceDisplay
{
    /// <summary>One gate, printable: its name, its command, and its host-coupled filter when it has one.</summary>
    public static string FormatGate(VerifyCommand gate)
    {
        string line = RelayedText.Printable($"{gate.Name}: {gate.Command}");
        return gate.HostCoupledFilter is { } filter
            ? $"{line} [host-coupled: {RelayedText.Printable(filter)}]"
            : line;
    }

    /// <summary>The full gate list, one printable line per gate, in the recorded (and run) order.</summary>
    public static IReadOnlyList<string> FormatGateList(IReadOnlyList<VerifyCommand> gates) =>
        [.. gates.Select(FormatGate)];

    /// <summary>
    /// <see cref="GateSetAcceptance.GateDiffLine"/>s rendered as a unified-style diff: a leading
    /// <c>+</c> for a gate the current list carries that the accepted one did not (at that
    /// position), <c>-</c> for the reverse, and no marker for a position that agrees.
    /// </summary>
    public static IReadOnlyList<string> FormatDiffLines(IReadOnlyList<GateSetAcceptance.GateDiffLine> diff) =>
        [.. diff.Select(line => line.Kind switch
        {
            GateSetAcceptance.GateDiffLineKind.Removed => $"  - {FormatGate(line.Gate)}",
            GateSetAcceptance.GateDiffLineKind.Added => $"  + {FormatGate(line.Gate)}",
            _ => $"    {FormatGate(line.Gate)}",
        })];
}

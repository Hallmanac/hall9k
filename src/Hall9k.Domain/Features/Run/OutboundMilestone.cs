namespace Hall9k.Domain.Features.Run;

/// <summary>
/// The outbound milestone vocabulary a dispatched agent uses to report to the human's registered
/// session on an interactive-mode task (task: agents on an interactive-mode task report outbound,
/// design ruling R8, idea fcaded0b's design rulings — "Agents in interactive mode send judicious
/// milestone messages ... using a small defined vocabulary per role"). Defined once here, the same
/// shape <see cref="SessionRoleName"/> already gives the session-naming vocabulary, so every prompt
/// builder names the identical moments rather than each inventing its own.
/// <para>
/// Judicious by contract, not merely in practice: each role's list below IS the bound the working
/// rules ask for — "the count per phase is bounded in the prompt" means this list's own length, not
/// a separately-stated number a prompt could drift out of step with. The last entry in every list
/// is always the end-of-phase report: not a short milestone label like the others, but the
/// substantive report itself (a build agent's handoff, a review verdict with its findings, a fix
/// session's closing summary), sent as the agent's last act before it ends normally — the boundary
/// then holds per slice 8 until the human's own proceed.
/// </para>
/// </summary>
public static class OutboundMilestone
{
    public const string Claimed = "claimed";
    public const string GatesGreen = "gates green";
    public const string FindingsDrafted = "findings drafted";
    public const string VerdictRecorded = "verdict recorded";
    public const string ReportReady = "report ready";

    /// <summary>A headless build session dispatched under interactive mode: claims the work, reports its gates green, then its handoff.</summary>
    public static readonly IReadOnlyList<string> Build = [Claimed, GatesGreen, ReportReady];

    /// <summary>A review pass — conformance, adversarial, or verify: drafts its findings, then records its verdict (the report itself).</summary>
    public static readonly IReadOnlyList<string> Review = [FindingsDrafted, VerdictRecorded];

    /// <summary>A fix session: nothing worth reporting exists until it is done, so its own report is its one milestone.</summary>
    public static readonly IReadOnlyList<string> Fix = [ReportReady];
}

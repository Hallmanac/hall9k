using Hall9k.Connectors.Prompts;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;

namespace Hall9k.Daemon.Review;

/// <summary>
/// The QA persona's own earned exception to a pr-review session's otherwise fixed, read-only
/// permission file (security review idea 6be68ee2, process-injection finding 1; Brian's ruling
/// 2026-09-27): the project's own recorded gate commands, allowed verbatim so QA can still build
/// and run this project's tests (`qa checks.md`). One place rather than three, so the QA session's
/// own dispatch (<c>RunLauncher</c>), its follow-on dispatch (<c>PrReviewEngine</c>), and its
/// resumed retry (<c>PrimarySessionResumer</c>) cannot answer the identical question three
/// different ways.
/// <para>
/// Gated on <see cref="GateSetAcceptance"/>, never a bare fallback to the project's current,
/// possibly-unaccepted <see cref="ProjectDetails.VerifyCommands"/>: a project whose gate list
/// arrived by replication from a teammate's node, or changed there, and which this node's own
/// operator never vetted through <c>h9k project accept-gates</c>, must not have those unvetted
/// shell commands handed to a QA session as <c>Bash(&lt;command&gt;:*)</c> allow rules just
/// because nothing has ever been accepted here yet (independent pre-PR review, cycle 1,
/// adversarial lens — the original fallback, <c>project.AcceptedVerifyCommands ?? project.VerifyCommands</c>,
/// ran the unaccepted list the moment nothing had been accepted, which is exactly backwards). The
/// answer here is no gate commands at all, not the unaccepted ones: a session denied a gate it
/// cannot yet run is exactly the "allow list grows by evidence" story every other refusal in
/// <see cref="ClaudeSettingsFile"/> already tells.
/// </para>
/// </summary>
public static class QaGateCommandsResolver
{
    /// <summary>
    /// The gate commands a QA persona session may run verbatim, or null when the project's
    /// current gate set has never been accepted on this node.
    /// </summary>
    public static IReadOnlyList<VerifyCommand>? Resolve(ProjectDetails project) =>
        GateSetAcceptance.Decide(project.AcceptedVerifyCommands, project.VerifyCommands).Proceed
            ? project.VerifyCommands
            : null;
}

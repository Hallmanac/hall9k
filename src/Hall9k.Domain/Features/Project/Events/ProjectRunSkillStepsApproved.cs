namespace Hall9k.Domain.Features.Project.Events;

/// <summary>
/// This node's own approval of exactly this run skill's parsed command steps (security review idea
/// 6be68ee2, process-injection finding 3; <c>Run.LocalLaunchStepApproval</c> is the decision this
/// event feeds). Recorded by <c>h9k task run-local</c> once an operator has said yes to exactly the
/// steps it printed — on an interactive confirm, or on a matching <c>--approve &lt;fingerprint&gt;</c>
/// — never by the walk itself, which only ever runs the plan this fact already covers.
/// <para>
/// Node-scoped, deliberately, the identical reasoning <see cref="ProjectGateSetAccepted"/> already
/// carries: approval is a fact about this install alone. A run skill that arrives by replication, or
/// changes on this owner's own other node, holds here regardless of what any other node of this
/// owner has approved — the whole point of the gate is that a stale or swapped plan cannot ride an
/// approval given to a different one.
/// </para>
/// </summary>
public sealed record ProjectRunSkillStepsApproved(
    Guid Id,
    string StepFingerprint,
    Guid ApprovedByOwnerId,
    DateTimeOffset ApprovedAt);

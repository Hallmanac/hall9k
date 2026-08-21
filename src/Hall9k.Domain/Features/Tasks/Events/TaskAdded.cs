using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// Model is this task's optional model override, the most specific link in the resolution
/// chain (Decisions Log #33), Unknown when the task states no preference. Appended with a
/// default so streams written before the chain existed replay as Unknown, never as a guess.
/// <para>
/// BlockedBy and StartsAsDraft carry the lifecycle split (Decisions Log #34) with the same
/// discipline. StartsAsDraft defaults to <c>false</c> so a stream written before the split
/// replays exactly as it behaved: added straight into the dispatchable queue and assigned to
/// AddedByOwnerId — which is the sole owner of a v0 install, an observed fact rather than a
/// guess at who a historical task belonged to. Every task h9k creates now passes true.
/// </para>
/// <para>
/// SourceIdeaId is the other half of promotion's two-way provenance (Decisions Log #35): the
/// idea's stream names the task it became, and this names the idea it came from. Null means
/// the task was written directly, which is a fact rather than a gap — and is also how every
/// stream written before ideas existed replays.
/// </para>
/// </summary>
public sealed record TaskAdded(
    Guid Id,
    Guid ProjectId,
    string Objective,
    IReadOnlyList<string> AcceptanceCriteria,
    TaskType Type,
    string? AgentContext,
    TaskConstraints? Constraints,
    ExternalReference? ExternalReference,
    DateTimeOffset AddedAt,
    Guid AddedByOwnerId,
    AgentModel? Model = null,
    IReadOnlyList<Guid>? BlockedBy = null,
    bool StartsAsDraft = false,
    Guid? SourceIdeaId = null);

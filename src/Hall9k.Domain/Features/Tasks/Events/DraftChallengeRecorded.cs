using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// How a draft challenge ended: a readable verdict with the count of its must, should, and park changes, or a
/// failure from the closed list in <see cref="ChallengeFailureReason"/> with no verdict. One event for both, the
/// <see cref="CloseoutTriageRecorded"/> shape, so the replication rules need only two types.
/// <para>
/// It holds nothing the session wrote verbatim and no machine path: hall9k's ledger refs are public and a
/// verdict carries security detail, so the prose stays in the report file on the node that ran the session
/// (<c>h9k task show</c> derives its path from <paramref name="SessionId"/>). Nothing applies a verdict:
/// recording one changes nothing about the draft.
/// </para>
/// </summary>
/// <param name="Id">The task's id.</param>
/// <param name="SessionId">The session, or null when the challenge ended before any session was spawned.</param>
/// <param name="NodeId">The node that ran the session and holds its report.</param>
/// <param name="Verdict">The verdict, or <see cref="ChallengeVerdict.Unknown"/> for a failure.</param>
/// <param name="Failure">Why there is no verdict, or <see cref="ChallengeFailureReason.Unknown"/> when there is one.</param>
/// <param name="MustCount">How many change lines the report marked must.</param>
/// <param name="ShouldCount">How many change lines the report marked should.</param>
/// <param name="ParkCount">How many change lines the report marked park.</param>
/// <param name="Model">The model the session ran on.</param>
/// <param name="Effort">The effort the session ran at.</param>
/// <param name="RecordedAt">When the outcome was recorded.</param>
public sealed record DraftChallengeRecorded(
    Guid Id,
    Guid? SessionId,
    Guid NodeId,
    ChallengeVerdict Verdict,
    ChallengeFailureReason Failure,
    int MustCount,
    int ShouldCount,
    int ParkCount,
    AgentModel Model,
    AgentEffort Effort,
    DateTimeOffset RecordedAt)
{
    /// <summary>Whether this outcome is a verdict rather than a failure.</summary>
    [JsonIgnore]
    public bool IsVerdict => Verdict.IsVerdict;
}

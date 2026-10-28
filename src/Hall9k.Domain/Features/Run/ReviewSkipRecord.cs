using System.Diagnostics.CodeAnalysis;
using Hall9k.Domain.Features.Trust;
using Hall9k.Domain.Infrastructure.Ids;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// One <see cref="Events.ReviewSkippedToPullRequest"/> as the read side keeps it, with the words
/// every surface (<c>h9k task show</c>, the orchestrator feed, the passage line) uses for who
/// skipped, what was skipped, and why, so no surface words them differently.
/// </summary>
public sealed record ReviewSkipRecord(
    Guid SkippedByOwnerId,
    string? OwnerRootFingerprint,
    DateTimeOffset SkippedAt,
    string? Reason,
    bool ReasonByAgent,
    IReadOnlyList<string> Skipped)
{
    public const string NoReasonGiven = "no reason given";

    public const string AgentWrittenMarker = "written by the driving session";

    /// <summary>
    /// The member's label when the event carries their root fingerprint, else the owner's short
    /// local id. Never a guess: an event that names neither reads as an unrecorded owner.
    /// </summary>
    public string Who(MemberLabelLookup labels) =>
        OwnerRootFingerprint.IsNotBlank()
            ? labels.LabelForFingerprint(OwnerRootFingerprint)
            : SkippedByOwnerId == Guid.Empty
                ? "an unrecorded owner"
                : $"owner {DomainId.Short(SkippedByOwnerId)}";

    /// <summary>What was skipped as one run of words, or the honest absence of a list.</summary>
    public string SkippedText => Skipped.Count == 0 ? "nothing named" : string.Join(", ", Skipped);

    [MemberNotNullWhen(true, nameof(Reason))]
    public bool HasReason => Reason.IsNotBlank();
}

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// One implementer's direction on a parked disagreement's proposed reply, kept as history the way
/// <see cref="ReviewParkResolution"/> is rather than overwritten (task: a changes-requested
/// pull-request review from a human becomes a fix lap). This is the audit answer to "what did the
/// platform actually say to that reviewer, and who decided it" — which matters precisely because
/// the alternative the whole feature exists to prevent is an agent answering a person on its own.
/// <para>
/// One entry per reply, mirroring <see cref="Events.ReviewDisagreementReplyDirected"/>'s own
/// shape: a park holding two disagreements produces two entries, each naming the text it posted
/// and the one place it landed.
/// </para>
/// </summary>
/// <param name="Choice">Which of the three the implementer picked: as written, edited, or nothing.</param>
/// <param name="PostedBody">The text a provider write actually accepted; null when nothing was posted.</param>
/// <param name="PostedTarget">
/// Where it landed — a review thread id for an in-thread reply, or the pull request's own url for
/// a top-level comment answering a review body. Null when nothing was posted, never populated
/// speculatively.
/// </param>
public sealed record ReviewDisagreementReplyDirection(
    ReviewDisagreementReplyChoice Choice,
    string? PostedBody,
    string? PostedTarget,
    DateTimeOffset DirectedAt);

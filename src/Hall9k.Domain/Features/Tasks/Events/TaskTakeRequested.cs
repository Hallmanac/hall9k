namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// A member asked the current holder for this task (idea 202383dc, item 5, "a member can ask a
/// holder for a task"): appended on the HOLDER's own node the moment its own claim-request
/// envelope is received and acted on, whether that ends in an automatic grant, an automatic
/// refusal, or a park for the holder's own human under <c>take-policy ask</c>. Recorded before
/// either outcome so <c>h9k task show</c> and <c>h9k status</c> can always say who asked and why,
/// even while the request is still parked with nothing decided yet.
/// </summary>
/// <param name="RequesterOwnerFingerprint">
/// The requester's own owner root fingerprint (idea 20723ef8) — by the time this event is ever
/// appended, <c>ClaimRequestWatchLoop.IsRequesterOwnerVerified</c> has already checked this exact
/// value against the ledger's own trust chain for the sending node, since
/// <c>ClaimRequestEngine.ReceiveRequestAsync</c> (the only caller that ever builds this event) is
/// only ever reached after that check passes. <see cref="RequesterOwnerId"/> carries no such
/// guarantee: it is the self-declared Guid a vouched node cannot be stopped from forging to a
/// different real owner's id, since Owner events never replicate and no node can verify a Guid it
/// does not itself own.
/// </param>
/// <param name="RequesterTrackerIdentity">
/// The requester's own tracker identity (a Jira accountId or a GitHub login), carried on the
/// originating claim-request envelope so it survives from the moment this event lands through to
/// whichever grant eventually answers it — <c>take-policy auto</c> grants immediately, in the same
/// batch, but <c>take-policy ask</c> parks this event for however long it takes the holder's own
/// human to run <c>h9k task grant</c>, and the identity has to still be there when they do. Null
/// when the project is not gated, the task carries no gated item, or the requester's own read
/// failed — <see cref="Handlers.TaskDecider.GrantTake"/>'s own caller reads that as "assign it to
/// the requester by hand" rather than a reason to refuse the request outright.
/// </param>
public sealed record TaskTakeRequested(
    Guid Id,
    Guid RequesterNodeId,
    Guid RequesterOwnerId,
    string RequesterOwnerFingerprint,
    string Reason,
    DateTimeOffset RequestedAt,
    string? RequesterTrackerIdentity = null);

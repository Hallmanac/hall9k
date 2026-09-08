namespace Hall9k.Domain.Features.Run;

/// <summary>
/// One thread's triage result, as a resolve-review-threads follow-up recorded it (task: every
/// review thread on a pull request gets a triage disposition before any fix work). The thread's
/// own text never lands here — only the classification, the same "findings stay artifacts on
/// disk, only the disposition goes on the stream" discipline <see cref="ReviewFindingRecord"/>
/// already follows — so <see cref="Reasoning"/> is the session's own restatement, written to
/// carry <see cref="ReviewThreadDisposition.Decline"/>'s evidence forward for measurement, not to
/// reproduce the reviewer's comment.
/// </summary>
/// <param name="ThreadId">The GraphQL review-thread node id this outcome names.</param>
/// <param name="Disposition">Fix, decline, or route — see <see cref="ReviewThreadDisposition"/>.</param>
/// <param name="Reasoning">
/// Why: a decline's reproduction-grade evidence, a route's scope justification, or a fix's brief
/// restatement. Blank when the session gave none, which is itself worth showing rather than
/// filling in.
/// </param>
/// <param name="Author">The thread-starter's login, when the session named one.</param>
/// <param name="IsHuman">
/// Whether the thread-starter is a person rather than a bot — read off the provider's own actor
/// type, never guessed from the login (the same rule the closeout inspector's own reviewer-kind
/// read already applies). Null when the session's `kind=` tag was absent or unrecognized — an
/// unobserved fact, not a claimed "bot" — the same nullable treatment
/// <see cref="Hall9k.Domain.Features.Run.Projections.RunDetails.UnresolvedHumanReviewThreads"/>
/// already gives this exact question at the run level. This is a record of what the session
/// reported, not itself consulted anywhere to decide whether an agent may resolve a thread — that
/// decision is made entirely in the follow-up prompt's own instructions.
/// </param>
public sealed record ReviewThreadOutcome(
    string ThreadId,
    ReviewThreadDisposition Disposition,
    string Reasoning,
    string? Author = null,
    bool? IsHuman = null);

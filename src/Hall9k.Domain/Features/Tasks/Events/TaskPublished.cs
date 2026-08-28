namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// Draft -> Published: the readiness gate passed (Decisions Log #34). Publishing promises
/// two things about the state it produces — the task satisfies the readiness contract, and
/// a human may assign it at any moment — which is why validation and cycle detection live
/// here alone and revision stops here.
/// </summary>
/// <param name="NoExistingItemAttested">
/// True when a project tracking its backlog (Jira or GitHub issues) required proof no
/// existing item already covers this task, and the publisher supplied it via
/// <c>--no-existing-item</c> instead of a link (backlog: publishing an untracked task under a
/// tracking backlog policy). <see cref="PublishedAt"/> and <see cref="PublishedByOwnerId"/> are
/// the attestation's own who and when — it is made in the same breath as the publish it gates.
/// False for a policy of none, false whenever the task already carried a reference, and false
/// whenever a publication was already pending (<c>h9k task push-to-jira</c>, run while still a
/// Draft) — none of those cases ever asks for one, so the flag is clamped to false rather than
/// recorded verbatim from the caller.
/// </param>
/// <param name="UntrackedAttested">
/// The sibling attestation to <see cref="NoExistingItemAttested"/>, same shape, opposite
/// choice: the publisher supplied <c>--untracked</c> to deliberately skip external tracking for
/// this task rather than searching the tracker and either linking a match or confirming none
/// exists. <see cref="PublishedAt"/> and <see cref="PublishedByOwnerId"/> are this attestation's
/// who and when too. Clamped to false under the same conditions as
/// <see cref="NoExistingItemAttested"/> — a policy of none, an already-linked task, or a
/// pending publication never asks for either attestation, so a flag passed defensively is never
/// recorded verbatim.
/// </param>
public sealed record TaskPublished(
    Guid Id,
    DateTimeOffset PublishedAt,
    Guid PublishedByOwnerId,
    bool NoExistingItemAttested = false,
    bool UntrackedAttested = false);

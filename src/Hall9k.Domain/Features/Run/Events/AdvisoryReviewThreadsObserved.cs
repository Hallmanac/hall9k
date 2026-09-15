namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// Closeout read one or more unresolved threads a PERSON opened that ask nothing of this pull
/// request, beside a review from that same person that requests no change — and so did not buy a
/// follow-up lap for them (task: a review-feedback follow-up never answers a human reviewer in
/// the owner's name on its own). The FYI beside an approval: "nice, my own pull request needs
/// this too".
/// <para>
/// Recorded rather than left silent because the alternative reads as a machine ignoring a person.
/// A thread that dispatched nothing is exactly the one an operator would otherwise have to open
/// the pull request to discover, so <c>h9k status</c> and <c>h9k task show</c> say it on the
/// phase line instead. It changes nothing else: the thread is still unresolved, so it still holds
/// a pre-approved merge exactly as every other unresolved thread does (the merge bar is
/// untouched), and a later comment in it that does ask something is a fresh read on the next
/// sweep, which dispatches as it always has.
/// </para>
/// <para>
/// Appended only when the observed set differs from what the run already records, because
/// closeout re-reads the same pull request every sweep and a per-sweep append would grow the
/// stream forever to restate one unchanged fact.
/// </para>
/// </summary>
/// <param name="ThreadIds">
/// The advisory threads' own node ids, as observed. The bodies stay off the stream, the same
/// "findings are artifacts, only the classification travels" discipline
/// <see cref="ReviewThreadsTriaged"/> already keeps.
/// </param>
public sealed record AdvisoryReviewThreadsObserved(
    Guid Id,
    IReadOnlyList<string> ThreadIds,
    DateTimeOffset ObservedAt);

namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A fix follow-up pushed and the closeout monitor asked the pull request's reviewers to
/// look again, so whoever raised the findings countersigns that they were addressed
/// (Decisions Log #62). Opt-in per owner and per project, and bounded by its own pass cap
/// beside the automatic closeout budget: at the cap the pull request settles on the
/// internal review, the thread replies, and CI.
/// <para>
/// Distinct from <see cref="ReviewRerequested"/>, which answers a different question: that
/// one re-asks a reviewer whose review <em>errored</em> and therefore never happened.
/// </para>
/// </summary>
/// <param name="Reviewers">
/// The logins the pass was addressed to, exactly as the provider reported them. Addressed
/// rather than accepted: the pass is recorded before the requests go out, so that a reviewer
/// the provider refuses cannot strand the record and leave the cap unable to bind. A refusal
/// is logged against the reviewer, never written here as though it had been asked.
/// </param>
/// <param name="Pass">Which re-request pass this is for the task, from 1 — the counter the cap bounds.</param>
public sealed record ReviewRerequestedAfterFixes(
    Guid Id,
    IReadOnlyList<string> Reviewers,
    int Pass,
    DateTimeOffset RequestedAt);

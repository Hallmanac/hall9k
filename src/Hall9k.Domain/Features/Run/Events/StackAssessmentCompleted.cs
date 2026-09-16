namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The run's one read-only stack-assessment session reported its verdict (task: a stacked
/// checkpoint that would park for a human on a git shape first dispatches a read-only assessment
/// run) — a missing or unparseable trailer is recorded here as <c>undecidable</c> too, exactly as
/// the caller treats it, so this event's own <see cref="Verdict"/> is always one of the three the
/// daemon actually acted on, never a fourth "could not parse" state hidden in prose.
/// <para>
/// An <c>aligned</c> or <c>replay</c> verdict updates this run's own recorded fork point and base
/// branch from what was actually observed (<see cref="BoundaryCommit"/> becomes the run's
/// <c>BaseCommit</c>, <see cref="ResolvedBaseBranchName"/> becomes its <c>BaseBranch</c> when it
/// could be resolved to a named branch) — the way a later checkpoint on this same run reads the
/// updated values rather than the stale ones that caused the park. An <c>undecidable</c> verdict
/// changes neither: there is nothing observed here worth trusting over what the run already had.
/// </para>
/// </summary>
/// <param name="Id">The run this assessment ran inside.</param>
/// <param name="ParkKind">Mirrors <see cref="StackAssessmentDispatched.ParkKind"/> — carried here too, so a projection reading only this event still knows what was being assessed.</param>
/// <param name="Verdict">One of <c>aligned</c>, <c>replay</c>, or <c>undecidable</c> — <c>StackAssessmentVerdictKind</c>'s own value.</param>
/// <param name="BoundaryCommit">The boundary commit an aligned or replay verdict named; blank for undecidable.</param>
/// <param name="OntoCommit">The onto commit an aligned or replay verdict named; blank for undecidable.</param>
/// <param name="ResolvedBaseBranchName">
/// The named branch <see cref="OntoCommit"/> was confirmed to be the tip of, resolved by the
/// daemon against the candidate branches it already knew about (the project's base branch, the
/// parent branch, the pull request's own GitHub-reported base) — never guessed when none of them
/// match, in which case this is blank and the run's recorded base branch is left exactly as it was.
/// Also blank when the matched candidate is the project's own base branch — see
/// <see cref="OntoIsProjectBaseBranch"/>, the field that actually carries that case, since this
/// field's own blank already means "leave the record alone" and cannot also mean "clear it".
/// </param>
/// <param name="OntoIsProjectBaseBranch">
/// True when <see cref="OntoCommit"/> was confirmed to be the project's own base branch's tip —
/// the one case that must clear a run's recorded <c>BaseBranch</c> back to blank (the same
/// invariant <see cref="StackedPullRequestRetargeted"/>'s own apply already holds), which
/// <see cref="ResolvedBaseBranchName"/> alone cannot signal since its blank is already spoken for
/// by "no candidate matched, leave the record alone".
/// </param>
/// <param name="Evidence">The assessment's own evidence block, verbatim — also appended to this run's own evidence file beside its review findings.</param>
public sealed record StackAssessmentCompleted(
    Guid Id,
    string ParkKind,
    string Verdict,
    string BoundaryCommit,
    string OntoCommit,
    string ResolvedBaseBranchName,
    bool OntoIsProjectBaseBranch,
    string Evidence,
    DateTimeOffset CompletedAt);

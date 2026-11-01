using Hall9k.Connectors.Worktrees;
using Hall9k.Domain.Features.Tasks;

namespace Hall9k.Daemon.Execution;

/// <summary>The latest challenge verdict on a draft, as a later refinement session is told it: the phrase, the counts, and where the report is.</summary>
/// <param name="Verdict">The verdict.</param>
/// <param name="MustCount">How many must changes it lists.</param>
/// <param name="ShouldCount">How many should changes it lists.</param>
/// <param name="ParkCount">How many park changes it lists.</param>
/// <param name="ReportPath">The challenge report on this node, derived from the challenge session's id.</param>
public sealed record PriorVerdictFacts(ChallengeVerdict Verdict, int MustCount, int ShouldCount, int ParkCount, string ReportPath);

/// <summary>
/// The facts about where a plan's refinement session runs that the draft's own record cannot hold, and that the prompt
/// has to carry because the session cannot know them: the daemon computed each one at dispatch.
/// </summary>
/// <param name="Purpose">Why the session was dispatched; it decides which paragraph of the prompt says what to do.</param>
/// <param name="ProjectHome">The project home, since the draft's pointers are relative to it, or null for a project registered without one.</param>
/// <param name="Checkout">The reading checkout the session runs in.</param>
/// <param name="Commit">The commit the checkout is at, or null when it could not be read.</param>
/// <param name="Refresh">What bringing the checkout up to the remote base branch came to, or null when the daemon did not try.</param>
/// <param name="ReportPath">The report the session writes.</param>
/// <param name="RevisionPath">The revision file it writes beside the report when it revises the draft.</param>
/// <param name="SessionDirectory">The session's own directory.</param>
/// <param name="IdeaWorkspace">The idea's workspace directory when the draft came from an idea and it exists, else null.</param>
/// <param name="Journal">The draft's <c>journal.md</c> when it exists, else null.</param>
/// <param name="JournalToWrite">Where the journal is written: its existing path, or the one in the draft's directory, or null when the draft has no directory on this node.</param>
/// <param name="PriorVerdict">The verdict a later session answers, or null for a first one.</param>
public sealed record PlanRefinementPromptFacts(
    RefinementPurpose Purpose,
    string? ProjectHome,
    string Checkout,
    string? Commit,
    CheckoutRefresh? Refresh,
    string ReportPath,
    string RevisionPath,
    string SessionDirectory,
    string? IdeaWorkspace,
    string? Journal,
    string? JournalToWrite,
    PriorVerdictFacts? PriorVerdict);

using Hall9k.Connectors.Worktrees;
using Hall9k.Domain.Features.Tasks;

namespace Hall9k.Daemon.Execution;

/// <summary>The latest challenge verdict on a draft, as a later refinement session is told it: the phrase, the counts, and where the report is.</summary>
/// <param name="Verdict">The verdict.</param>
/// <param name="MustCount">How many must changes it lists.</param>
/// <param name="ShouldCount">How many should changes it lists.</param>
/// <param name="ParkCount">How many park changes it lists.</param>
/// <param name="ReportPath">The challenge report, derived from the challenge session's id, or null when another node ran the challenge and its report is not on this one.</param>
public sealed record PriorVerdictFacts(ChallengeVerdict Verdict, int MustCount, int ShouldCount, int ParkCount, string? ReportPath);

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
/// <param name="PriorVerdict">The latest challenge verdict on the draft, or null when it has never been challenged.</param>
/// <param name="OtherDrafts">The plan's other drafts by id and objective when the plan carries more than one, else empty; the session's revision may set <c>blocked-by</c> or <c>stacked-on</c> to one of them.</param>
/// <param name="DiscoveryReport">The report the plan's discovery session wrote, when the draft was cut from it, else null.</param>
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
    PriorVerdictFacts? PriorVerdict,
    IReadOnlyList<SiblingDraftFacts>? OtherDrafts = null,
    string? DiscoveryReport = null);

/// <summary>A sibling draft in the same plan, as a refinement session is told of it: the id it can name in a revision and what it is for.</summary>
public sealed record SiblingDraftFacts(Guid Id, string Objective);

/// <summary>A task already cut from the idea a discovery session studies: its id, its state, and its objective, all quoted.</summary>
public sealed record DiscoveryCutTaskFacts(Guid Id, string State, string Objective);

/// <summary>
/// The facts about where a plan's discovery session runs that the idea's own record cannot hold, and that the prompt has to
/// carry because the session cannot know them: the daemon computed each one at dispatch.
/// </summary>
/// <param name="IdeaId">The idea.</param>
/// <param name="IdeaNote">The idea's note as the store has it at dispatch.</param>
/// <param name="CutTasks">The tasks already cut from the idea, with their states.</param>
/// <param name="ProjectHome">The project home, or null for a project registered without one.</param>
/// <param name="Checkout">The reading checkout the session runs in.</param>
/// <param name="Commit">The commit the checkout is at, or null when it could not be read.</param>
/// <param name="Refresh">What bringing the checkout up to the remote base branch came to, or null when the daemon did not try.</param>
/// <param name="Workspace">The idea's workspace directory, which exists.</param>
/// <param name="Journal">The workspace's <c>journal.md</c> when it exists, else null.</param>
/// <param name="EarlierReports">Earlier discovery reports in the workspace.</param>
/// <param name="ReportPath">The report the session writes.</param>
/// <param name="SessionDirectory">The session's own directory.</param>
/// <param name="TimeBound">How long the session may run.</param>
public sealed record PlanDiscoveryPromptFacts(
    Guid IdeaId,
    string IdeaNote,
    IReadOnlyList<DiscoveryCutTaskFacts> CutTasks,
    string? ProjectHome,
    string Checkout,
    string? Commit,
    CheckoutRefresh? Refresh,
    string Workspace,
    string? Journal,
    IReadOnlyList<string> EarlierReports,
    string ReportPath,
    string SessionDirectory,
    TimeSpan TimeBound);

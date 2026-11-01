using Hall9k.Connectors.Worktrees;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// The facts about where a draft challenge runs that the draft's own record cannot hold, and that the prompt
/// has to carry because the session cannot know them: the daemon computed each one at dispatch.
/// </summary>
/// <param name="ProjectHome">The project home, since the draft's pointers (notes, ideas, tasks, skills) are relative to it, or null for a project registered without one.</param>
/// <param name="Checkout">The reading checkout the session runs in.</param>
/// <param name="Commit">The commit the checkout is at, or null when it could not be read.</param>
/// <param name="Refresh">What bringing the checkout up to the remote base branch came to, or null when the daemon did not try (a project registered before homes, reading somebody's own clone).</param>
/// <param name="ReportPath">The one file the session writes.</param>
/// <param name="SessionDirectory">The session's own directory, the only place a probe may create files.</param>
/// <param name="IdeaWorkspace">The idea's workspace directory when the draft came from an idea and it exists, else null.</param>
/// <param name="Journal">The draft's <c>journal.md</c> when it exists, else null.</param>
/// <param name="ProbeModel">The model a probe session runs on, resolved by the model policy's cheap tier, or null when no probe can run (a project without skip-permissions).</param>
public sealed record ChallengePromptFacts(
    string? ProjectHome,
    string Checkout,
    string? Commit,
    CheckoutRefresh? Refresh,
    string ReportPath,
    string SessionDirectory,
    string? IdeaWorkspace,
    string? Journal,
    string? ProbeModel);

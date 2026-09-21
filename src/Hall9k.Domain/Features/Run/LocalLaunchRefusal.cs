namespace Hall9k.Domain.Features.Run;

/// <summary>
/// Every sentence <c>h9k task run-local</c> refuses with (idea b9b09779, piece 5), in one place
/// rather than inline at each guard. They live in the domain because the orchestrator window reads
/// them out loud to the reviewer: a refusal is the answer to a person who just said yes to an
/// offer, so it has to say which of the reasons applied and what would change it, not merely that
/// it did not work. The CLI's own standard applies here as directly as anywhere
/// (AGENTS.md: failures print why, with the rule quoted, so the reader can self-correct).
/// </summary>
public static class LocalLaunchRefusal
{
    /// <summary>The offer named a worktree and it is not there any more.</summary>
    public static string WorktreeGone(string worktreePath) =>
        $"There is no worktree left to run this branch in: the review checkout at {worktreePath} is gone, "
        + "which usually means the task closed out and the platform released it, so there is nothing here to "
        + "stand up.";

    /// <summary>The run never recorded a checkout at all.</summary>
    public static string NoWorktreeRecorded(Guid taskId) =>
        $"Task {taskId} has no review checkout recorded on its run, so this command has no worktree to run "
        + "the branch in; a review opened with --no-worktree is reading a deployed environment rather than a "
        + "local one.";

    /// <summary>There is no live run to read a worktree off.</summary>
    public static string NoRun(Guid taskId) =>
        $"Task {taskId} has no current run, so there is no review checkout to stand the branch up in; this "
        + "command answers a review report's offer, and that offer belongs to a run.";

    /// <summary>The project has nothing on its ledger saying how it is started.</summary>
    public static string NoRunSkill(string projectName) =>
        $"Project {projectName} has no run skill, so nothing on this platform records how it is started and "
        + "this command will not guess at a launch command; ask for discovery with "
        + $"h9k project set {projectName} --discover-run-skill, or set one by hand with "
        + $"h9k project run-skill set {projectName} --file PATH --shape full-text.";

    /// <summary>The run skill exists but nothing in it parses as a step to take.</summary>
    public static string RunSkillHasNoSteps(string projectName) =>
        $"Project {projectName}'s run skill carries no step this command can follow: its Prerequisites, "
        + "One-time setup, Launch and Human steps sections are all empty or say there is nothing in them, so "
        + "there is a document but no procedure in it.";

    /// <summary>A launch of this task is already up.</summary>
    public static string AlreadyLaunched(Guid taskId, string what) =>
        $"A local launch of task {taskId} is already up ({what}), and two launches of one branch would race "
        + "each other for the same ports and the same checkout; stop that one with "
        + $"h9k task run-local {taskId} --stop, or resume it with --continue if it is waiting on you.";

    /// <summary><c>--continue</c> with nothing paused.</summary>
    public static string NothingToContinue(Guid taskId) =>
        $"Task {taskId} has no local launch paused at a human step, so there is nothing for --continue to "
        + $"resume; start one with h9k task run-local {taskId} and it will stop and tell you when it reaches a "
        + "step you have to do yourself.";

    /// <summary><c>--stop</c> with nothing up.</summary>
    public static string NothingToStop(Guid taskId) =>
        $"Task {taskId} has no local launch to stop: nothing is recorded as up for it, so there is no process "
        + "here to end.";
}

namespace Hall9k.Domain.Features.Project;

/// <summary>
/// One step of a project's run skill, as <see cref="RunSkillSteps"/> read it out of the document
/// (idea b9b09779, piece 5). Recorded verbatim on the launch's own start event rather than
/// re-parsed on every <c>--continue</c>: a launch follows the plan it actually started with, so a
/// run skill rewritten mid-launch cannot silently renumber the step a paused reviewer is standing
/// on.
/// </summary>
/// <param name="Number">Its 1-based position in the whole plan, which is what a paused launch resumes from.</param>
/// <param name="Kind">Whether the platform can run it or a person has to.</param>
/// <param name="Section">The run-skill heading it came from, so a printed step says where in the document it lives.</param>
/// <param name="Text">The step as the document words it, whole — including anything the parse did not lift into <paramref name="Command"/>.</param>
/// <param name="Command">
/// The command to run on the worktree, or empty for a <see cref="RunSkillStepKind.Human"/> step.
/// Never invented: a step with nothing that reads as a command is a human step, because a
/// plausible-looking command the document never carried is the one thing this parse must not
/// produce (AGENTS.md, never guess at unobserved facts).
/// </param>
public sealed record RunSkillStep(
    int Number, RunSkillStepKind Kind, string Section, string Text, string Command);

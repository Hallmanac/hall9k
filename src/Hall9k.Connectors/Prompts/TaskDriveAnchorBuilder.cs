using System.Text;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks.Notes;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Connectors.Prompts;

/// <summary>
/// The anchor <c>h9k task drive</c> hands a human's own Claude Code session with
/// <c>--append-system-prompt-file</c>: the standing rules for driving one task from the build
/// through the review loop that runs before its pull request opens. Prose lives in
/// <c>.claude/templates/task-drive-anchor</c>; this assembles it, and pastes the project's writing
/// conventions through <see cref="WorkPromptBuilder.AppendWritingConventions"/> so they read exactly
/// as they do in a headless session's prompt.
/// </summary>
public static class TaskDriveAnchorBuilder
{
    /// <summary>
    /// What the follow-up variant of the anchor says about a task whose pull request is already open,
    /// all of it read by the caller from the record: the review comments the session may read, how
    /// its commits land on the branch, and what closeout last saw.
    /// </summary>
    /// <param name="PullRequestUrl">The open pull request.</param>
    /// <param name="ReadScope">
    /// Which unresolved threads, and which comments inside them, the session may read, as the reopen
    /// recorded it. Rendered through <see cref="FollowUpPromptFragments.AppendReviewReadScope(StringBuilder, ReviewReadScope?, bool)"/>,
    /// the code a headless follow-up's prompt calls, so a session reads a thread the same way
    /// whichever door it came in by. Null reads as nothing readable, never as everything.
    /// </param>
    /// <param name="LastCloseoutObservation">One line on what closeout last observed on the pull request, or null when there is none.</param>
    /// <param name="TriageReportPath">The read-only triage report closeout wrote while the owner drove, or null when there is none.</param>
    /// <param name="CommitStyle">How fixes land on the branch, resolved project over platform.</param>
    /// <param name="BaseBranch">The branch this run's work sits on, for the narrative style's fold.</param>
    /// <param name="Fold">The fold boundary for a stacked child, or null for every ordinary run.</param>
    /// <param name="VoiceSkill">The owner's own voice skill, when they named one.</param>
    public sealed record PullRequestStage(
        string PullRequestUrl,
        ReviewReadScope? ReadScope,
        string? LastCloseoutObservation,
        string? TriageReportPath,
        CommitStyle CommitStyle,
        string BaseBranch,
        FollowUpPromptFragments.FoldBoundary? Fold,
        VoiceSkillName? VoiceSkill);

    /// <summary>The package this builder's own prose ships under in <c>.claude/templates</c>.</summary>
    public const string TemplateDirectory = "task-drive-anchor";

    /// <param name="recipeFile">
    /// The project home's <c>recipes/task-drive.md</c> when it exists, or null, in which case the
    /// anchor says nothing about a recipe.
    /// </param>
    /// <param name="pullRequestStage">
    /// Set for a task whose pull request is already open, which swaps the build-to-review anchor for
    /// the follow-up variant: the pull request, the threads the session may read, the last closeout
    /// observation, the triage report, and how commits land on the branch. Null for every task still
    /// before its pull request.
    /// </param>
    /// <param name="taskNotes">
    /// The task's notes section, which the anchor carries because the default launch line sends the
    /// session to read the record rather than handing it a composed prompt, so this is the only
    /// route the notes and the verb that writes one take to it. Null composes nothing. An attended
    /// session is taught the verb but never asked for the closing block, since nothing records an
    /// attended session's final message.
    /// </param>
    /// <param name="humanGate">
    /// Whether the task has a human gate. This anchor is where an attended build composes the summary it
    /// passes to <c>h9k task deliver</c>, so a gated task's anchor carries the same ask for a plain opening
    /// paragraph that a headless build prompt does (<see cref="WorkPromptBuilder.AppendHumanGateSummaryAsk"/>).
    /// Not used once the pull request is open, where the gate has already been answered.
    /// </param>
    public static string Build(
        Guid taskId, string worktreePath, string branch, string prSummaryDraftPath, WritingConventions conventions,
        string? recipeFile, PullRequestStage? pullRequestStage = null, InjectedTaskNotes? taskNotes = null, bool humanGate = false)
    {
        const string file = $"{TemplateDirectory}/anchor.md";
        Dictionary<string, string> values = new()
        {
            ["TaskId"] = taskId.ToString(),
            ["Worktree"] = worktreePath,
            ["Branch"] = branch,
            ["PrSummaryDraft"] = prSummaryDraftPath,
            ["RecipeFile"] = recipeFile ?? string.Empty,
            // The two command words and the title prefix are contract tokens a template must not
            // type itself (PromptContractTokens), so they arrive as parameters.
            ["Deliver"] = WorkPromptBuilder.DeliverWord,
            ["RegisterSession"] = WorkPromptBuilder.RegisterSessionWord,
            ["TitlePrefix"] = PrSummaryParser.TitlePrefix,
        };

        StringBuilder anchor;
        if (pullRequestStage is null)
        {
            anchor = BuildFragments(
                file, values, ["title", "register", "read-first", "never-advance", "verb-map", "job-changes", "worktree-rules", "pr-summary"]);
            if (humanGate)
            {
                WorkPromptBuilder.AppendHumanGateSummaryAsk(anchor);
                anchor.AppendLine();
            }

            PromptTemplates.AppendTemplate(anchor, file, "skip-to-pr", values);
            anchor.AppendLine();
        }
        else
        {
            anchor = BuildFollowUp(file, values, pullRequestStage);
        }

        WorkPromptBuilder.AppendTaskNotes(anchor, taskNotes, taskId);
        WorkPromptBuilder.AppendWritingConventions(
            anchor, string.Empty, conventions, PromptTemplates.Load(file, "writing-conventions-lead"));
        if (recipeFile is not null)
        {
            anchor.AppendLine();
            PromptTemplates.AppendTemplate(anchor, file, "recipe", values);
        }

        return PromptLineEndings.Finish(anchor);
    }

    /// <summary>
    /// The anchor for the attended session <c>h9k task drive</c> prints a launch line for at a task's human
    /// gate, whoever holds the run's claim. It names the gate, the briefing command, and the outcomes
    /// (rendered from <see cref="InteractiveBoundaryChoices"/>, the one vocabulary the park reason,
    /// <c>h9k task next</c>, <c>h9k status</c>, and the briefing also render, so no surface can offer a
    /// different set), and tells the session to leave the worktree alone once a lever has handed the branch
    /// back. It carries no register step: the session registers nothing.
    /// </summary>
    public static string BuildGate(
        Guid taskId, string worktreePath, string branch, WritingConventions conventions, string? recipeFile)
    {
        const string file = $"{TemplateDirectory}/anchor.md";
        Dictionary<string, string> values = new()
        {
            ["TaskId"] = taskId.ToString(),
            ["Worktree"] = worktreePath,
            ["Branch"] = branch,
            ["RecipeFile"] = recipeFile ?? string.Empty,
            ["RegisterSession"] = WorkPromptBuilder.RegisterSessionWord,
        };

        StringBuilder anchor = BuildFragments(file, values, ["gate-title", "gate-read-first", "gate-never-advance"]);
        PromptTemplates.AppendTemplate(anchor, file, "gate-outcomes-lead", values);
        anchor.AppendLine();
        foreach (string choice in InteractiveBoundaryChoices.Lines(InteractiveBoundaryLevers.HumanGate, taskId))
        {
            anchor.AppendLine($"- {choice}");
        }

        anchor.AppendLine();
        PromptTemplates.AppendTemplate(anchor, file, "gate-worktree-rules", values);
        anchor.AppendLine();
        WorkPromptBuilder.AppendWritingConventions(
            anchor, string.Empty, conventions, PromptTemplates.Load(file, "writing-conventions-lead"));
        if (recipeFile is not null)
        {
            anchor.AppendLine();
            PromptTemplates.AppendTemplate(anchor, file, "recipe", values);
        }

        return PromptLineEndings.Finish(anchor);
    }

    private static StringBuilder BuildFragments(string file, Dictionary<string, string> values, string[] fragments)
    {
        StringBuilder anchor = new();
        foreach (string fragment in fragments)
        {
            PromptTemplates.AppendTemplate(anchor, file, fragment, values);
            anchor.AppendLine();
        }

        return anchor;
    }

    private static StringBuilder BuildFollowUp(string file, Dictionary<string, string> values, PullRequestStage stage)
    {
        values["PullRequestUrl"] = stage.PullRequestUrl;
        StringBuilder anchor = BuildFragments(
            file, values, ["follow-up-title", "register", "read-first", "never-advance", "follow-up-pull-request"]);

        if (stage.LastCloseoutObservation.IsNotBlank())
        {
            values["Observation"] = stage.LastCloseoutObservation;
            PromptTemplates.AppendTemplate(anchor, file, "follow-up-observation", values);
            anchor.AppendLine();
        }

        if (stage.TriageReportPath.IsNotBlank())
        {
            values["TriageReport"] = stage.TriageReportPath;
            PromptTemplates.AppendTemplate(anchor, file, "follow-up-triage", values);
            anchor.AppendLine();
        }

        FollowUpPromptFragments.AppendReviewReadScope(anchor, stage.ReadScope);
        FollowUpPromptFragments.AppendCommitStyleRules(
            anchor, stage.CommitStyle, stage.BaseBranch, stage.Fold, stage.VoiceSkill);
        anchor.AppendLine();
        if (stage.CommitStyle == CommitStyle.Narrative)
        {
            PromptTemplates.AppendTemplate(anchor, file, "follow-up-absorb", values);
            anchor.AppendLine();
        }

        foreach (string fragment in new[] { "follow-up-verb-map", "follow-up-job-changes", "follow-up-worktree-rules" })
        {
            PromptTemplates.AppendTemplate(anchor, file, fragment, values);
            anchor.AppendLine();
        }

        return anchor;
    }
}

using System.Text;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Infrastructure.Storage;

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
    /// <summary>The package this builder's own prose ships under in <c>.claude/templates</c>.</summary>
    public const string TemplateDirectory = "task-drive-anchor";

    /// <param name="recipeFile">
    /// The project home's <c>recipes/task-drive.md</c> when it exists, or null, in which case the
    /// anchor says nothing about a recipe.
    /// </param>
    public static string Build(
        Guid taskId, string worktreePath, string branch, string prSummaryDraftPath, WritingConventions conventions,
        string? recipeFile)
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

        string[] fragments = ["title", "register", "read-first", "never-advance", "verb-map", "job-changes", "worktree-rules", "pr-summary"];
        StringBuilder anchor = new();
        foreach (string fragment in fragments)
        {
            PromptTemplates.AppendTemplate(anchor, file, fragment, values);
            anchor.AppendLine();
        }

        WorkPromptBuilder.AppendWritingConventions(
            anchor, string.Empty, conventions, PromptTemplates.Load(file, "writing-conventions-lead"));
        if (recipeFile is not null)
        {
            anchor.AppendLine();
            PromptTemplates.AppendTemplate(anchor, file, "recipe", values);
        }

        return PromptLineEndings.Finish(anchor);
    }
}

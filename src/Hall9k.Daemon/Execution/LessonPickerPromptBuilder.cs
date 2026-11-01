using System.Globalization;
using System.Text;
using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// The prompt the lesson picker runs on: the task's objective, criteria and context, the lessons the
/// composing node may put in a prompt, both caps, and a request for ids and nothing else, most
/// relevant first. The session has no tools, so the only thing it can do with what it reads is
/// answer. The task's words and the lessons' words go in as quoted data, the same fence a stranger's
/// text gets elsewhere, because a task adopted from an issue can carry text its author never
/// vetted and a lesson is a claim some earlier run recorded.
/// </summary>
public static class LessonPickerPromptBuilder
{
    private const string Template = $"{AgentPromptBuilder.TemplateDirectory}/lesson-picker.md";

    public static string Compose(TaskDetails task, LessonCandidates candidates)
    {
        StringBuilder prompt = new();
        prompt.AppendLine(PromptTemplates.Load(Template, "heading"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(Template, "intro"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(Template, "task-heading"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(Template, "task-fence-note"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(Template, "task-objective-heading"));
        prompt.AppendLine(RelayedText.Fenced(RelayedText.Printable(task.Objective)));
        prompt.AppendLine();
        if (task.AcceptanceCriteria.Count == 0)
        {
            prompt.AppendLine(PromptTemplates.Load(Template, "task-criteria-none"));
        }
        else
        {
            prompt.AppendLine(PromptTemplates.Load(Template, "task-criteria-heading"));
            prompt.AppendLine(RelayedText.Fenced(RelayedText.Printable(
                string.Join('\n', task.AcceptanceCriteria.Select(criterion => $"- {RelayedText.OneLine(criterion)}")))));
        }

        prompt.AppendLine();
        if (task.AgentContext.IsBlank())
        {
            prompt.AppendLine(PromptTemplates.Load(Template, "task-context-none"));
        }
        else
        {
            prompt.AppendLine(PromptTemplates.Load(Template, "task-context-heading"));
            prompt.AppendLine(RelayedText.Fenced(RelayedText.Printable(task.AgentContext)));
        }

        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(Template, "lessons-heading"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(
            Template, "lessons-intro",
            Params(("Offered", candidates.Eligible.Count.ToString(CultureInfo.InvariantCulture)))));
        prompt.AppendLine();
        prompt.AppendLine(RelayedText.Fenced(RelayedText.Printable(
            string.Join('\n', candidates.Eligible.Select(lesson => lesson.Line)))));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(Template, "limits-heading"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(
            Template, "limits",
            Params(
                ("MaxLessons", candidates.Caps.MaxLessons.ToString(CultureInfo.InvariantCulture)),
                ("MaxCharacters", candidates.Caps.MaxCharacters.ToString(CultureInfo.InvariantCulture)))));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(Template, "answer-heading"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(Template, "answer"));
        return PromptLineEndings.Finish(prompt);
    }

    private static IReadOnlyDictionary<string, string> Params(params (string Key, string Value)[] values) =>
        values.ToDictionary(pair => pair.Key, pair => pair.Value);
}

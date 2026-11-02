using System.Globalization;
using System.Text;
using Hall9k.Connectors.Text;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Tasks.Notes;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Daemon.Feedback;

/// <summary>
/// The prompt the feedback step's check runs on: the task's objective, criteria and context, and the notes its blockers left
/// that the trust rule lets into a prompt, each under the short id of the blocker that left it. Nothing else goes in. The
/// session has no tools, so the only thing it can do with what it reads is answer. The task text and the notes go in as quoted
/// data, the same fence a stranger's text gets elsewhere, because a note is text some earlier session wrote, and a note the
/// trust rule holds back is never passed to this builder at all.
/// </summary>
public static class FeedbackCheckPromptBuilder
{
    private const string Template = $"{AgentPromptBuilder.TemplateDirectory}/feedback-check.md";

    public static string Compose(TaskDetails task, InjectedTaskNotes notes)
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
        prompt.AppendLine(PromptTemplates.Load(Template, "objective-heading"));
        prompt.AppendLine(RelayedText.Fenced(RelayedText.Printable(task.Objective)));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(Template, "criteria-heading"));
        prompt.AppendLine(task.AcceptanceCriteria.Count == 0
            ? PromptTemplates.Load(Template, "criteria-none")
            : RelayedText.Fenced(string.Join('\n', task.AcceptanceCriteria.Select(criterion => $"- {RelayedText.OneLine(criterion)}"))));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(Template, "context-heading"));
        prompt.AppendLine(task.AgentContext.IsBlank()
            ? PromptTemplates.Load(Template, "context-none")
            : RelayedText.Fenced(RelayedText.Printable(task.AgentContext!)));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(Template, "notes-heading"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(
            Template, "notes-intro", Params(("Count", notes.Notes.Count.ToString(CultureInfo.InvariantCulture)))));
        prompt.AppendLine();
        prompt.AppendLine(RelayedText.Fenced(string.Join('\n', notes.Notes.Select(note => $"{note.ShortTaskId}: {RelayedText.OneLine(note.Text)}"))));
        if (notes.LeftOut > 0)
        {
            prompt.AppendLine();
            prompt.AppendLine(PromptTemplates.Load(
                Template, "notes-left-out", Params(("Count", notes.LeftOut.ToString(CultureInfo.InvariantCulture)))));
        }

        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(Template, "rules-heading"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(Template, "rules"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(Template, "answer-heading"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(Template, "answer", Params(("Marker", FeedbackCheckAnswerReader.Marker))));
        return PromptLineEndings.Finish(prompt);
    }

    private static IReadOnlyDictionary<string, string> Params(params (string Key, string Value)[] values) =>
        values.ToDictionary(pair => pair.Key, pair => pair.Value);
}

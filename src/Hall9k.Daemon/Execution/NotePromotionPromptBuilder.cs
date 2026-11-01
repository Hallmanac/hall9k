using System.Globalization;
using System.Text;
using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Tasks.Notes;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// The prompt the closeout promotion step runs on: the task's objective, each offered note under its
/// short id on one line of printable text, and the rules for keeping one. Nothing else from the task
/// goes in, not its criteria, context, handoff or findings. The session has no tools, so the only
/// thing it can do with what it reads is answer. The objective and the notes go in as quoted data,
/// the same fence a stranger's text gets elsewhere, because a note is text some earlier session wrote
/// and a task adopted from an issue can carry words its author never vetted.
/// </summary>
public static class NotePromotionPromptBuilder
{
    private const string Template = $"{AgentPromptBuilder.TemplateDirectory}/note-promotion.md";

    public static string Compose(string objective, NotePromotionOffer offer)
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
        prompt.AppendLine(RelayedText.Fenced(RelayedText.Printable(objective)));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(Template, "notes-heading"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(
            Template, "notes-intro",
            Params(("Offered", offer.Offered.Count.ToString(CultureInfo.InvariantCulture)))));
        prompt.AppendLine();
        prompt.AppendLine(RelayedText.Fenced(string.Join(
            '\n', offer.Offered.Select(note => $"{note.ShortId}: {RelayedText.OneLine(note.Text)}"))));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(Template, "rules-heading"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(
            Template, "rules",
            Params(
                ("MaxKept", NotePromotionLimits.MaxKept == 1 ? "one" : NotePromotionLimits.MaxKept.ToString(CultureInfo.InvariantCulture)),
                ("MaxClaimLength", NotePromotionLimits.MaxClaimLength.ToString(CultureInfo.InvariantCulture)))));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(Template, "answer-heading"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(
            Template, "answer",
            Params(
                ("KeepMarker", NotePromotionAnswer.KeepMarker),
                ("NoneMarker", NotePromotionAnswer.NoneMarker))));
        return PromptLineEndings.Finish(prompt);
    }

    private static IReadOnlyDictionary<string, string> Params(params (string Key, string Value)[] values) =>
        values.ToDictionary(pair => pair.Key, pair => pair.Value);
}

using System.Text;
using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// The prompt a non-member-feedback evaluator runs on: one piece of a stranger's feedback, assessed
/// against the task's objective and acceptance criteria before the owner decides on it. Read-only
/// by construction (the session runs under the pr-review permission file, never with permissions
/// skipped) and told so, and the feedback is fenced as data the session reads and never obeys.
/// <para>
/// This is the one place a stranger's words enter a session's prompt without the owner's go, which is
/// why that session cannot post, push or write anywhere but its own assessment file, and why the
/// assessment it writes is never rendered into a status row or the feed, only its recommendation word.
/// </para>
/// </summary>
public static class FeedbackEvaluatorPromptBuilder
{
    /// <summary>The longest feedback text a prompt carries; the rest is cut and said to be.</summary>
    internal const int MaxFeedbackLength = 20_000;

    private const string Template = $"{AgentPromptBuilder.TemplateDirectory}/feedback-evaluator.md";

    public static string Build(
        NonMemberFeedbackItem item, string objective, IReadOnlyList<string> acceptanceCriteria,
        string pullRequestUrl, string assessmentPath)
    {
        StringBuilder prompt = new();
        prompt.AppendLine(PromptTemplates.Load(Template, "heading"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(Template, "intro"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(Template, "task-heading"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(Template, "task-objective", Params(("Objective", RelayedText.OneLine(objective)))));
        prompt.AppendLine();
        if (acceptanceCriteria.Count == 0)
        {
            prompt.AppendLine(PromptTemplates.Load(Template, "task-criteria-none"));
        }
        else
        {
            prompt.AppendLine(PromptTemplates.Load(Template, "task-criteria-heading"));
            foreach (string criterion in acceptanceCriteria)
            {
                prompt.AppendLine($"- {RelayedText.OneLine(criterion)}");
            }
        }

        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(Template, "feedback-heading"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(
            Template, "feedback-context",
            Params(
                ("Kind", item.Kind.Noun),
                ("Reviewer", $"@{RelayedText.OneLine(item.Reviewer)}"),
                ("PullRequestUrl", RelayedText.OneLine(pullRequestUrl)),
                ("FeedbackUrl", RelayedText.OneLine(item.Url)))));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(Template, "feedback-fence-note"));
        prompt.AppendLine();
        prompt.AppendLine(RelayedText.Fenced(FeedbackText(item)));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(Template, "working-heading"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(Template, "working"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(Template, "job-heading"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(Template, "job", Params(("AssessmentPath", assessmentPath))));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(Template, "rules-heading"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(Template, "rules"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(Template, "closing"));
        return PromptLineEndings.Finish(prompt);
    }

    /// <summary>
    /// The stranger's words as held with the item: a review's findings with their locations, or a
    /// thread's opener comments. Printable and bounded; this is all the evaluator is ever shown of it.
    /// </summary>
    internal static string FeedbackText(NonMemberFeedbackItem item)
    {
        IEnumerable<string> parts = item.Review is { } review
            ? review.Findings.Select(finding => finding.Location.IsNotBlank()
                ? $"[{finding.Location}] {finding.Body}"
                : finding.Body)
            : (item.CommentCopies ?? []).Select(copy => copy.Body);
        string text = RelayedText.Printable(string.Join("\n\n", parts));
        return text.Length > MaxFeedbackLength
            ? $"{text[..RelayedText.CutLength(text, MaxFeedbackLength)]}\n[cut: the feedback is longer than this prompt carries; the owner reads the rest at its link]"
            : text.Length == 0 ? "(the held copy of this feedback carries no text)" : text;
    }

    private static IReadOnlyDictionary<string, string> Params(params (string Key, string Value)[] values) =>
        values.ToDictionary(pair => pair.Key, pair => pair.Value);
}

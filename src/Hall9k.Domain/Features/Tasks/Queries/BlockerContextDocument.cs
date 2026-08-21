using System.Text;

namespace Hall9k.Domain.Features.Tasks.Queries;

/// <summary>
/// Renders a task's blocker handoffs into the one context document the platform routes
/// along dependency edges (Decisions Log #36).
/// <para>
/// It lives in the domain rather than beside the prompt builder because two surfaces must
/// agree on it: the daemon pastes it into the claimed run's prompt, and <c>h9k task show</c>
/// prints it as the context the task would receive if it were claimed now. A human reading
/// that screen is reading the agent's actual starting context, not a second rendering of the
/// same facts that could drift from it.
/// </para>
/// </summary>
public static class BlockerContextDocument
{
    /// <summary>The section heading, shared so the prompt and the CLI announce the same thing.</summary>
    public const string Heading = "## What your blockers handed down";

    /// <summary>
    /// The whole section, heading included, or null when the task has no blockers to route
    /// anything from — an absent section rather than a section announcing emptiness.
    /// </summary>
    public static string? Render(IReadOnlyList<BlockerHandoff> blockers)
    {
        if (blockers.Count == 0)
        {
            return null;
        }

        StringBuilder document = new();
        document.AppendLine(Heading);
        document.AppendLine();
        document.AppendLine("You could not start until the work below finished, which is also a statement about");
        document.AppendLine("relatedness: a blocker almost certainly touched your area or produced the thing you");
        document.AppendLine("build on. Each entry is what that blocker's own run handed down at true closeout.");
        document.AppendLine();
        document.AppendLine("These are your IMMEDIATE blockers only, deliberately. The platform does not walk");
        document.AppendLine("further back, so if you find yourself needing a fact from two hops away, that is");
        document.AppendLine("evidence of a missing dependency edge rather than a gap to work around — say so in");
        document.AppendLine("your own handoff instead of guessing.");

        int position = 0;
        foreach (BlockerHandoff blocker in blockers)
        {
            position++;
            document.AppendLine();
            document.AppendLine($"### {position}. {blocker.Objective}");
            document.AppendLine();
            document.AppendLine($"[{ShortId(blocker.TaskId)} · {blocker.State.Value}]");
            document.AppendLine();
            AppendBody(document, blocker);
        }

        return document.ToString();
    }

    /// <summary>
    /// The handoff when there is one, and otherwise the blocker's own intent. A blocker with
    /// nothing to say still says what it was for, and the reason there is no handoff is
    /// printed rather than left as a blank the reader has to interpret (the AGENTS.md
    /// never-guess rule).
    /// </summary>
    private static void AppendBody(StringBuilder document, BlockerHandoff blocker)
    {
        if (blocker.HasSummary)
        {
            document.AppendLine(blocker.Summary);
            return;
        }

        document.AppendLine($"No handoff was recorded for this blocker ({blocker.Outcome.Describe()}). What it set");
        document.AppendLine("out to do, which is what you have instead:");
        document.AppendLine();
        if (blocker.AcceptanceCriteria.Count == 0)
        {
            document.AppendLine("- No acceptance criteria were recorded either.");
            return;
        }

        foreach (string criterion in blocker.AcceptanceCriteria)
        {
            document.AppendLine($"- {criterion}");
        }
    }

    /// <summary>The eight-character task id humans read on every other h9k surface.</summary>
    private static string ShortId(Guid id) => id.ToString("N")[^8..];
}

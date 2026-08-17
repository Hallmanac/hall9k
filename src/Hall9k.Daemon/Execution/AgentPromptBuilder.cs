using System.Text;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks.Projections;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// Assembles the agent's prompt from the readiness contract (PLAN.md §4): objective,
/// acceptance criteria, agent context, and the project's context links. Pointers, not
/// pasted content — the agent pulls context itself.
/// </summary>
public static class AgentPromptBuilder
{
    public static string Build(TaskDetails task, ProjectDetails project, string branch)
    {
        StringBuilder prompt = new();
        prompt.AppendLine("# Task");
        prompt.AppendLine();
        prompt.AppendLine(task.Objective);
        prompt.AppendLine();

        prompt.AppendLine("## Acceptance criteria");
        prompt.AppendLine();
        foreach (string criterion in task.AcceptanceCriteria)
        {
            prompt.AppendLine($"- {criterion}");
        }

        prompt.AppendLine();

        if (task.AgentContext.IsNotBlank())
        {
            prompt.AppendLine("## Context");
            prompt.AppendLine();
            prompt.AppendLine(task.AgentContext);
            prompt.AppendLine();
        }

        if (project.ContextLinks.Count > 0)
        {
            prompt.AppendLine("## Project links (fetch yourself as needed)");
            prompt.AppendLine();
            foreach (var link in project.ContextLinks)
            {
                prompt.AppendLine($"- {link.Name}: {link.Url}");
            }

            prompt.AppendLine();
        }

        prompt.AppendLine("## Working rules");
        prompt.AppendLine();
        prompt.AppendLine($"- You are in an isolated git worktree on branch `{branch}`. Work only here.");
        prompt.AppendLine("- Implement the objective so every acceptance criterion is satisfied.");
        prompt.AppendLine("- Commit your work with clear messages. Do NOT push, do NOT open a pull request —");
        prompt.AppendLine("  the platform verifies and opens the PR after you finish.");
        prompt.AppendLine("- If something is genuinely ambiguous, make the most reasonable choice and record");
        prompt.AppendLine("  the assumption in your final summary (the ask-a-human loop is not available yet).");
        prompt.AppendLine("- End with a short summary: what you did, decisions made, assumptions, open questions.");

        return prompt.ToString();
    }
}

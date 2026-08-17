using System.Text;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks.Projections;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// Assembles the agent's prompt from the readiness contract (PLAN.md §4): objective,
/// acceptance criteria, agent context, the project's context links, and — when the
/// worktree ships repo skills — a one-line pointer per skill. Pointers, not pasted
/// content — the agent pulls context itself and skills load on invocation.
/// </summary>
public static class AgentPromptBuilder
{
    public static string Build(TaskDetails task, ProjectDetails project, string branch, string worktreePath)
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

        IReadOnlyList<RepoSkill> skills = DiscoverRepoSkills(worktreePath);
        if (skills.Count > 0)
        {
            prompt.AppendLine("- This repo ships Claude skills; invoke the matching one instead of improvising its workflow:");
            foreach (RepoSkill skill in skills)
            {
                prompt.AppendLine(skill.Description is null
                    ? $"  - `{skill.Name}`"
                    : $"  - `{skill.Name}` — {skill.Description}");
            }
        }

        prompt.AppendLine("- If something is genuinely ambiguous, make the most reasonable choice and record");
        prompt.AppendLine("  the assumption in your final summary (the ask-a-human loop is not available yet).");
        prompt.AppendLine("- End with a short summary: what you did, decisions made, assumptions, open questions.");

        return prompt.ToString();
    }

    /// <summary>
    /// The follow-up variant (PR closeout, Decisions Log #20): the agent resumes the task's
    /// existing PR branch to resolve review feedback via the repo-resident
    /// resolve-copilot-reviews skill. The platform re-verifies and pushes; the PR updates
    /// in place.
    /// </summary>
    public static string BuildFollowUp(TaskDetails task, ProjectDetails project, string branch, string pullRequestUrl)
    {
        StringBuilder prompt = new();
        prompt.AppendLine("# Follow-up task: resolve review feedback on an existing pull request");
        prompt.AppendLine();
        prompt.AppendLine($"Pull request: {pullRequestUrl}");
        prompt.AppendLine();
        prompt.AppendLine("The original task below already shipped in the pull request above, which now has");
        prompt.AppendLine("unresolved review comments. Your job is to resolve that review feedback — not to");
        prompt.AppendLine("redo the original work.");
        prompt.AppendLine();

        prompt.AppendLine("## Original objective (context, already implemented)");
        prompt.AppendLine();
        prompt.AppendLine(task.Objective);
        prompt.AppendLine();

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
        prompt.AppendLine("- You are in an isolated git worktree checked out on the EXISTING pull-request");
        prompt.AppendLine($"  branch `{branch}`. Work only here.");
        prompt.AppendLine("- Use the resolve-copilot-reviews skill to triage the review comments on");
        prompt.AppendLine($"  {pullRequestUrl}: apply valid fixes, reply to each thread, resolve them.");
        prompt.AppendLine("- Commit your fixes on this branch with clear messages. Do NOT push, do NOT open");
        prompt.AppendLine("  a new pull request — the platform re-verifies and pushes after you finish; the");
        prompt.AppendLine("  existing PR updates in place.");
        prompt.AppendLine("- End with a short summary: which comments you addressed, which you dismissed and");
        prompt.AppendLine("  why, and any open questions.");

        return prompt.ToString();
    }

    private static IReadOnlyList<RepoSkill> DiscoverRepoSkills(string worktreePath)
    {
        string skillsDirectory = Path.Combine(worktreePath, ".claude", "skills");
        if (!Directory.Exists(skillsDirectory))
        {
            return [];
        }

        List<RepoSkill> skills = [];
        foreach (string skillDirectory in Directory.EnumerateDirectories(skillsDirectory).Order(StringComparer.Ordinal))
        {
            string manifestPath = Path.Combine(skillDirectory, "SKILL.md");
            if (File.Exists(manifestPath))
            {
                skills.Add(new RepoSkill(Path.GetFileName(skillDirectory), ReadFrontmatterDescription(manifestPath)));
            }
        }

        return skills;
    }

    private static string? ReadFrontmatterDescription(string manifestPath)
    {
        // Stream and stop at the frontmatter fence — the skill body below it can be large
        // and is never needed here.
        using IEnumerator<string> lines = File.ReadLines(manifestPath).GetEnumerator();
        if (!lines.MoveNext() || lines.Current.Trim() != "---")
        {
            return null;
        }

        while (lines.MoveNext())
        {
            string line = lines.Current;
            if (line.Trim() == "---")
            {
                break;
            }

            if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
            {
                string description = line["description:".Length..].Trim();
                return description.IsNotBlank() ? description : null;
            }
        }

        return null;
    }

    private sealed record RepoSkill(string Name, string? Description);
}

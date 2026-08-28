using Hall9k.Connectors.Text;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// Composes a GitHub issue's body and labels deterministically from a task, the reverse
/// direction of <see cref="WorkItemContext"/> (which composes agent context FROM an imported
/// item). Deterministic on purpose: a backlog policy of github-issues is exactly the case where
/// there is no agent in the loop to write anything more considered.
/// </summary>
public static class GitHubIssueBody
{
    /// <summary>
    /// The agent context, then the acceptance criteria as a checklist. Either half may be absent.
    /// <paramref name="truncatedObjective"/> is the task's own objective, in full, when the title
    /// this issue was created with had to be cut to fit GitHub's 256-character limit: the title
    /// alone would otherwise be the only place the objective was recorded, and the characters past
    /// the cut would exist nowhere on GitHub. Pass null when the title was not truncated, so a
    /// normal issue is not given a redundant restatement of its own title.
    /// </summary>
    public static string Compose(
        string? agentContext, IReadOnlyList<string> acceptanceCriteria, string? truncatedObjective = null)
    {
        List<string> sections = [];
        if (truncatedObjective.IsNotBlank())
        {
            sections.Add($"## Objective\n\n{RelayedText.Printable(truncatedObjective.Trim())}");
        }

        if (agentContext.IsNotBlank())
        {
            sections.Add(RelayedText.Printable(agentContext.Trim()));
        }

        if (acceptanceCriteria.Count > 0)
        {
            // Each criterion goes through RelayedText.OneLine, the same rule
            // Hall9k.Daemon.Execution.PullRequestBody applies to a checklist item: a criterion
            // free to carry its own newline could otherwise break out of the list item it is
            // meant to be, opening a heading or a second list underneath it.
            sections.Add(
                "## Acceptance criteria\n\n"
                + string.Join('\n', acceptanceCriteria.Select(c => $"- [ ] {RelayedText.OneLine(c)}")));
        }

        return string.Join("\n\n", sections);
    }

    /// <summary>
    /// A project's backlog routing guidance, read as a comma-separated label list — the only
    /// reading a deterministic author gives it. Free prose ("file under the platform epic") is
    /// meaningless without a reader that can interpret it, which is exactly the agent this policy
    /// does not dispatch; <see cref="JiraWorkItemProvider"/>'s agent-mediated push is where the
    /// same text is handed over verbatim instead.
    /// </summary>
    public static IReadOnlyList<string> Labels(string? routingGuidance) =>
        (routingGuidance ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

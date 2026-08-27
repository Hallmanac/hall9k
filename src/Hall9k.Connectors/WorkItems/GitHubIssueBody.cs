namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// Composes a GitHub issue's body and labels deterministically from a task, the reverse
/// direction of <see cref="WorkItemContext"/> (which composes agent context FROM an imported
/// item). Deterministic on purpose: a backlog policy of github-issues is exactly the case where
/// there is no agent in the loop to write anything more considered.
/// </summary>
public static class GitHubIssueBody
{
    /// <summary>The agent context, then the acceptance criteria as a checklist. Either half may be absent.</summary>
    public static string Compose(string? agentContext, IReadOnlyList<string> acceptanceCriteria)
    {
        List<string> sections = [];
        if (agentContext.IsNotBlank())
        {
            sections.Add(agentContext.Trim());
        }

        if (acceptanceCriteria.Count > 0)
        {
            sections.Add(
                "## Acceptance criteria\n\n" + string.Join('\n', acceptanceCriteria.Select(c => $"- {c}")));
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

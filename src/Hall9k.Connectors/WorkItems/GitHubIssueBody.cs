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
            sections.Add(Checklist(acceptanceCriteria));
        }

        return string.Join("\n\n", sections);
    }

    /// <summary>The heading the acceptance-criteria checklist lives under.</summary>
    private const string CriteriaHeading = "## Acceptance criteria";

    /// <summary>
    /// <paramref name="body"/> with its acceptance-criteria checklist regenerated from
    /// <paramref name="acceptanceCriteria"/>, and everything else — the prose above and below it —
    /// left exactly as it was. Called only when the criteria actually changed: rewriting a checklist
    /// nobody touched would throw away a human's own formatting for nothing.
    /// </summary>
    public static string WithCriteriaChecklist(string? body, IReadOnlyList<string> acceptanceCriteria)
    {
        string text = (body ?? string.Empty).ReplaceLineEndings("\n");
        string checklist = Checklist(acceptanceCriteria);
        (int start, int end) = ChecklistSpan(text);
        // No checklist to regenerate: the issue never had one, or a human removed the heading. It
        // goes at the very end.
        return start < 0 ? Join(text.TrimEnd(), checklist) : Join(text[..start].TrimEnd(), checklist, text[end..].TrimStart('\n'));
    }

    /// <summary>
    /// The checklist section, in the one shape both the create body and a criteria-changing rewrite
    /// write it. Each criterion goes through <see cref="RelayedText.OneLine"/>, the same rule
    /// <c>Hall9k.Daemon.Execution.PullRequestBody</c> applies to a checklist item: a criterion free
    /// to carry its own newline could otherwise break out of the list item it is meant to be,
    /// opening a heading or a second list underneath it.
    /// </summary>
    private static string Checklist(IReadOnlyList<string> acceptanceCriteria) =>
        acceptanceCriteria.Count == 0
            ? string.Empty
            : $"{CriteriaHeading}\n\n"
                + string.Join('\n', acceptanceCriteria.Select(c => $"- [ ] {RelayedText.OneLine(c)}"));

    private static string Join(params string[] parts) =>
        string.Join("\n\n", parts.Where(part => part.Length > 0));

    /// <summary>
    /// Where the acceptance-criteria section starts and ends: from its heading to the end of the
    /// checklist itself — the run of list items under it, blank lines between them included — and
    /// not one character further.
    /// <para>
    /// Ending at the last item rather than at the next heading is the whole point. Heading-less
    /// prose a human wrote underneath the list is theirs, and a span that ran to the next
    /// <c>## </c> — or, when none intervened, to the end of the body — replaced that prose with the
    /// regenerated checklist and deleted it (independent pre-PR review, cycle 1, adversarial lens;
    /// docs/scope.md promises a human's edits to the issue's prose survive, and a rewrite that took
    /// someone's own note with it would make the feature a liability). Prose sitting BETWEEN items
    /// is a case this deliberately does not chase: the run ends at the first line that is neither an
    /// item nor a blank one, so items past that note are left where they are rather than swept up —
    /// a stale line a human can see and delete, never text of theirs this deleted for them.
    /// </para>
    /// </summary>
    private static (int Start, int End) ChecklistSpan(string text)
    {
        int limit = text.Length;
        int start = text.IndexOf(CriteriaHeading, StringComparison.Ordinal);
        if (start < 0)
        {
            return (-1, -1);
        }

        int cursor = LineEnd(text, start, limit);
        // The heading alone when the list is empty or somebody replaced it with prose: the
        // regenerated checklist takes the heading's place and the prose stays put below it.
        int end = cursor;
        while (cursor < limit)
        {
            int lineEnd = LineEnd(text, cursor, limit);
            ReadOnlySpan<char> line = text.AsSpan(cursor, lineEnd - cursor);
            if (IsChecklistItem(line))
            {
                end = lineEnd;
            }
            else if (!line.IsWhiteSpace())
            {
                break;
            }

            cursor = lineEnd;
        }

        return (start, end);
    }

    /// <summary>
    /// One past the newline ending the line <paramref name="from"/> sits on, or
    /// <paramref name="limit"/> when the line runs to the end of the searchable text.
    /// </summary>
    private static int LineEnd(string text, int from, int limit)
    {
        int newline = text.IndexOf('\n', from);
        return newline < 0 || newline >= limit ? limit : newline + 1;
    }

    /// <summary>
    /// Whether a line is a markdown task-list item — the shape <see cref="Checklist"/> writes, and
    /// the same line with its box checked, which is what a human ticking a criterion off on GitHub
    /// leaves behind. Any list bullet is accepted and so is indentation: a rewrite has to recognise
    /// the item it is about to replace however the human reformatted it, or it would leave the old
    /// list standing underneath the new one.
    /// </summary>
    private static bool IsChecklistItem(ReadOnlySpan<char> line)
    {
        ReadOnlySpan<char> trimmed = line.TrimStart();
        return trimmed.Length > 0 && trimmed[0] is '-' or '*' or '+'
            && trimmed[1..].TrimStart().StartsWith("[", StringComparison.Ordinal);
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

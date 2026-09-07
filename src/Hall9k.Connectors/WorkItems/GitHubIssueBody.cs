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

    /// <summary>
    /// The record section's summary line: what a human sees collapsed at the foot of the issue, and
    /// the marker every reader and rewriter here finds the section by. It says outright that the
    /// block is machine-maintained, so nobody edits it expecting the edit to reach a task, and it
    /// names the command that consumes it.
    /// </summary>
    public const string RecordSummary =
        "Hall9k task record (machine-readable, maintained by hall9k; adopt with h9k task add --from-issue)";

    /// <summary>The heading the acceptance-criteria checklist lives under, in both writers.</summary>
    private const string CriteriaHeading = "## Acceptance criteria";

    /// <summary>
    /// <paramref name="body"/> with the task record at the foot of it, collapsed: the human-readable
    /// objective and checklist stay first and untouched, and a record already there is replaced
    /// rather than joined by a second one. Everything above the section survives verbatim, which is
    /// the whole contract — a human's own edits to the issue's prose are theirs to keep (task: a
    /// published task's GitHub issue carries the whole task record).
    /// </summary>
    public static string WithRecord(string? body, TaskRecord record)
    {
        string yaml = record.ToYaml();
        // The fence is sized to the content the same way WorkItemContext sizes the one it quotes an
        // imported body inside: a record whose own context carries a fenced code block would
        // otherwise close this one early, and the block would stop round-tripping.
        string fence = RelayedText.FenceFor(yaml);
        string section =
            $"<details>\n<summary>{RecordSummary}</summary>\n\n{fence}yaml\n{yaml}{fence}\n\n</details>";

        string above = Without(body).TrimEnd();
        return above.Length == 0 ? section : $"{above}\n\n{section}";
    }

    /// <summary>
    /// <paramref name="body"/> with its acceptance-criteria checklist regenerated from
    /// <paramref name="acceptanceCriteria"/>, and everything else — the prose above it, the record
    /// section below it — left exactly as it was. Called only when the criteria actually changed:
    /// rewriting a checklist nobody touched would throw away a human's own formatting for nothing.
    /// </summary>
    public static string WithCriteriaChecklist(string? body, IReadOnlyList<string> acceptanceCriteria)
    {
        string text = (body ?? string.Empty).ReplaceLineEndings("\n");
        string checklist = Checklist(acceptanceCriteria);
        (int start, int end) = ChecklistSpan(text);
        if (start < 0)
        {
            // No checklist to regenerate: the issue never had one, or a human removed the heading.
            // It goes in above the record section rather than at the very end, so the machine-only
            // block stays last where a human reading top-down expects it.
            return Find(text) is { Section: { } section }
                ? Join(text[..section.Start].TrimEnd(), checklist, text[section.Start..])
                : Join(text.TrimEnd(), checklist);
        }

        return Join(text[..start].TrimEnd(), checklist, text[end..].TrimStart('\n'));
    }

    /// <summary>
    /// The YAML inside the record section, or null when the item carries none — which is every
    /// issue anybody filed by hand, and which adopts exactly as it always did. The text comes back
    /// byte for byte as it sits between the fences, so a record read here and parsed is the record
    /// that was written.
    /// </summary>
    public static string? TryReadRecordYaml(string? body)
    {
        string text = (body ?? string.Empty).ReplaceLineEndings("\n");
        return Find(text) is { Yaml: { } yaml } ? text[yaml.Start..yaml.End] : null;
    }

    /// <summary>The body with its record section removed, which is how a fresh one replaces an old one.</summary>
    private static string Without(string? body)
    {
        string text = (body ?? string.Empty).ReplaceLineEndings("\n");
        return Find(text) is { Section: { } section }
            ? (text[..section.Start] + text[section.End..]).TrimEnd()
            : text;
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
    /// Where the record section is, and where the YAML inside it is, or null when the body carries
    /// no record. Found by the summary line rather than by position, so a human who wrote prose
    /// underneath the section — or who moved it — still has their text left alone.
    /// <para>
    /// The end is found by walking the fenced block structurally rather than by taking the first
    /// <c>&lt;/details&gt;</c> after the summary, and that is the whole point of this method
    /// existing. The record's own <c>context</c> is an agent context, and an adopted task's agent
    /// context quotes the issue body it came from — which routinely contains a
    /// <c>&lt;details&gt;</c> section, a <c>&lt;/details&gt;</c>, and an
    /// <c>## Acceptance criteria</c> heading of its own (this feature's own issue, #266, contains
    /// all three). Taking the first closing tag would cut the section off inside its own YAML,
    /// leaving the tail behind on a rewrite and making the block unreadable afterwards. The fence
    /// is sized by <see cref="RelayedText.FenceFor"/> to be longer than any backtick run inside the
    /// YAML, so its closing line is the one thing in here that cannot be counterfeited by the
    /// content.
    /// </para>
    /// </summary>
    private static RecordLocation? Find(string text)
    {
        int summary = text.IndexOf(RecordSummary, StringComparison.Ordinal);
        if (summary < 0)
        {
            return null;
        }

        int open = text.LastIndexOf("<details>", summary, StringComparison.Ordinal);
        if (open < 0)
        {
            return null;
        }

        int firstClose = text.IndexOf("</details>", summary, StringComparison.Ordinal);
        int fenceStart = text.IndexOf("```", summary, StringComparison.Ordinal);
        int fenceLineEnd = fenceStart < 0 ? -1 : text.IndexOf('\n', fenceStart);
        if (fenceStart < 0 || fenceLineEnd < 0 || (firstClose >= 0 && firstClose < fenceStart))
        {
            // No fenced block inside THIS section — a section somebody wrote by hand differently.
            // The closing-tag comparison is what makes that "inside this section" rather than
            // "anywhere below": a fence further down the body, past the section's own end, would
            // otherwise be taken for this section's and stretch it over everything in between.
            // With no YAML there is also no quoted content that could be faking a closing tag, so
            // the first one really is this section's.
            return Fallback(text, open, summary);
        }

        string ticks = new('`', text[fenceStart..].TakeWhile(character => character == '`').Count());
        int fenceClose = text.IndexOf($"\n{ticks}", fenceLineEnd, StringComparison.Ordinal);
        if (fenceClose < 0)
        {
            // An unterminated fence: everything to the end of the section is YAML as far as any
            // reader is concerned, so the same fallback applies for the same reason.
            return Fallback(text, open, summary);
        }

        int close = text.IndexOf("</details>", fenceClose, StringComparison.Ordinal);
        return close < 0
            ? Fallback(text, open, summary)
            : new RecordLocation(
                new Span(open, close + "</details>".Length),
                new Span(fenceLineEnd + 1, fenceClose + 1));
    }

    private static RecordLocation Fallback(string text, int open, int summary)
    {
        int close = text.IndexOf("</details>", summary, StringComparison.Ordinal);
        return new RecordLocation(
            new Span(open, close < 0 ? text.Length : close + "</details>".Length),
            Yaml: null);
    }

    /// <summary>The record section's own span, and the span of the YAML between its fences.</summary>
    private readonly record struct RecordLocation(Span Section, Span? Yaml);

    /// <summary>
    /// Half-open character offsets into the body — offsets rather than a <see cref="Range"/>
    /// because every use here does arithmetic on them.
    /// </summary>
    private readonly record struct Span(int Start, int End);

    /// <summary>
    /// Where the acceptance-criteria section starts and ends: from its heading to the end of the
    /// checklist itself — the run of list items under it, blank lines between them included — and
    /// not one character further. The search never enters the record section — see
    /// <see cref="Find"/> for why the YAML in there can look like a checklist.
    /// <para>
    /// Ending at the last item rather than at the next heading is the whole point. Heading-less
    /// prose a human wrote underneath the list is theirs, and a span that ran to the next
    /// <c>## </c> — or, when none intervened, all the way to the record section — replaced that
    /// prose with the regenerated checklist and deleted it (independent pre-PR review, cycle 1,
    /// adversarial lens; docs/scope.md promises a human's edits to the issue's prose survive, and a
    /// rewrite that took someone's own note with it would make the feature a liability). Prose
    /// sitting BETWEEN items is a case this deliberately does not chase: the run ends at the first
    /// line that is neither an item nor a blank one, so items past that note are left where they
    /// are rather than swept up — a stale line a human can see and delete, never text of theirs
    /// this deleted for them.
    /// </para>
    /// </summary>
    private static (int Start, int End) ChecklistSpan(string text)
    {
        int limit = Find(text) is { Section: { } section } ? section.Start : text.Length;
        int start = text[..limit].IndexOf(CriteriaHeading, StringComparison.Ordinal);
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

using System.Text.RegularExpressions;
using Hall9k.Connectors.Processes;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// The task type/pull-request contract's own imported-context clause: "when the PR references a
/// Jira card or GitHub issue... that context is imported... exactly as --from-issue/--from-jira
/// do today". Shared by every caller that adopts a pull request as agent context — <c>h9k task
/// add --from-pr</c> and the daemon's own auto-pr-review sweep alike (independent pre-PR review,
/// cycle 1, conformance lens: the two had drifted, with the auto-created path silently omitting
/// this import the objective and AGENTS.md both promise it carries) — so the rule is written once
/// rather than reimplemented per caller. Best-effort and never fatal to the adoption: a linked
/// reference this install cannot read (no Jira connection, a private repository, a deleted issue)
/// is dropped, not refused, because the pull request itself is still the thing being adopted.
/// Deliberately bypasses <see cref="WorkItemImporter"/>'s open-only gate: this is a read for
/// context, not an adoption of the linked item as its own task, and the linked issue a pull
/// request closes is very often already closed by the time of review.
/// </summary>
public static class LinkedWorkItemImport
{
    /// <summary>
    /// A GitHub closing keyword ("fixes #42", "closes owner/repo#42") in the pull request's own
    /// title or body, the same vocabulary <c>RelayedText.WithoutClosingKeywords</c> defuses on the
    /// way into agent context.
    /// </summary>
    private static readonly Regex LinkedIssueReference = new(
        @"\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)[:\s]+(?:([\w.-]+/[\w.-]+)#(\d+)|#(\d+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// <paramref name="processRunner"/> is threaded into the GitHub provider this builds for a
    /// linked GitHub issue rather than left at its own default (the same discipline
    /// <see cref="WorkItemConnections.ImporterAsync"/> applies to its own GitHub providers): a
    /// caller carrying an injected <see cref="ProcessRunner"/> — a test double, or a daemon
    /// component's own — must have it actually used here, not silently bypassed for the real
    /// <c>gh</c>. Null (the CLI's own call site) keeps the real one, unchanged.
    /// </summary>
    public static async Task<string?> TryImportContextAsync(
        IQuerySession session, ProjectDetails project, ImportedWorkItem pullRequest, CancellationToken cancellationToken,
        ProcessRunner? processRunner = null)
    {
        string haystack = $"{pullRequest.Title}\n{pullRequest.Body}";
        Match issueMatch = LinkedIssueReference.Match(haystack);
        (WorkItemProvider Provider, string Reference)? linked = issueMatch.Success
            ? (WorkItemProvider.GitHub, $"{(issueMatch.Groups[1].Success ? issueMatch.Groups[1].Value : pullRequest.Reference.Reference.Split('#')[0])}#{(issueMatch.Groups[2].Success ? issueMatch.Groups[2].Value : issueMatch.Groups[3].Value)}")
            : FindJiraKey(haystack, project.JiraProjectKey) is { } jiraKey
                ? (WorkItemProvider.Jira, jiraKey)
                : null;
        if (linked is null)
        {
            return null;
        }

        try
        {
            ImportedWorkItem linkedItem = linked.Value.Provider == WorkItemProvider.Jira
                ? await (await WorkItemConnections.JiraProviderAsync(session, cancellationToken)).ImportAsync(
                    new WorkItemImportRequest(linked.Value.Provider, linked.Value.Reference, project.RepositoryPath),
                    cancellationToken)
                : await new GitHubWorkItemProvider(processRunner).ImportAsync(
                    new WorkItemImportRequest(linked.Value.Provider, linked.Value.Reference, project.RepositoryPath),
                    cancellationToken);
            return "Linked from the pull request, imported alongside it (state not re-checked as open):\n\n"
                + WorkItemContext.Compose(linkedItem);
        }
        catch (DomainException)
        {
            return null;
        }
    }

    /// <summary>
    /// A Jira issue key ("PROJ-123") anywhere in the pull request's own title or body — but only
    /// for the project's own bound board (h9k project set --jira). A bare `[A-Z][A-Z0-9]+-\d+`
    /// pattern matches plenty of non-Jira text too (UTF-8, SHA-256, RFC-7231, CVE-2024-…), and
    /// scoping to the one key this project actually files against is what tells them apart,
    /// rather than firing a lookup for whichever one happens to parse. No board bound means
    /// nothing to scope the match to, so nothing is linked (never guess at unobserved facts,
    /// AGENTS.md) — the same key JiraProjectKey.None already stands for everywhere else.
    /// </summary>
    private static string? FindJiraKey(string haystack, JiraProjectKey projectKey)
    {
        if (!projectKey.HasValue)
        {
            return null;
        }

        Match match = Regex.Match(haystack, $@"\b({Regex.Escape(projectKey.Value)}-\d+)\b");
        return match.Success ? match.Groups[1].Value : null;
    }
}

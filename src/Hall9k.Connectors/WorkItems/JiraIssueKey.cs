using System.Globalization;
using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// A Jira card's key — PROJ-123 — and the forms a human actually has to hand for one: the bare
/// key, the canonical <c>jira:PROJ-123</c> reference Hall9k stores, and the browser URL, which
/// on Jira Cloud is either a /browse/ link or a board URL carrying the card in
/// <c>?selectedIssue=</c>.
/// <para>
/// It is a type rather than a regular expression at three call sites because the same key has to
/// be recognised on the way in (import, link) and written back out the same way every time; and
/// because the project half of it is what a board binding is checked against, which is a
/// question about the key rather than about Jira.
/// </para>
/// </summary>
public sealed record JiraIssueKey(JiraProjectKey Project, int Number)
{
    public string Value => $"{Project.Value}-{Number.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The canonical reference this key is stored as on a task.</summary>
    public ExternalReference Reference => new(WorkItemProvider.Jira, Value);

    public override string ToString() => Value;

    /// <summary>
    /// The key, or a refusal that names every form this accepts. <paramref name="site"/> is the
    /// registered site: a URL from anywhere else is refused rather than having its key taken,
    /// because a stored reference records the key and no host at all, so a card from another
    /// tenant would be filed — and later linked back to — as this tenant's card of the same key.
    /// That is the same rule the GitHub provider applies to enterprise hosts, for the same reason.
    /// </summary>
    public static JiraIssueKey Parse(string? reference, Uri site)
    {
        string trimmed = reference?.Trim() ?? string.Empty;
        if (trimmed.IsBlank())
        {
            throw new DomainValidationException(
                "This needs a Jira card to work with. Pass the key (PROJ-123) or the card's URL.");
        }

        // The canonical stored form, so a reference copied out of h9k task show works verbatim.
        if (trimmed.StartsWith($"{WorkItemProvider.Jira.Value}:", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[(WorkItemProvider.Jira.Value.Length + 1)..].Trim();
        }

        return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? FromUrl(trimmed, site)
                : FromBareKey(trimmed);
    }

    /// <summary>The rule without the throw, for a caller that wants to ask rather than try.</summary>
    public static bool TryParseBareKey(string value, out JiraIssueKey key)
    {
        key = new JiraIssueKey(JiraProjectKey.None, 0);
        int dash = value.LastIndexOf('-');
        if (dash <= 0 || dash == value.Length - 1)
        {
            return false;
        }

        string project = value[..dash].Trim().ToUpperInvariant();
        string number = value[(dash + 1)..].Trim();
        if (!JiraProjectKey.IsWellFormed(project)
            || !int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            || parsed <= 0)
        {
            return false;
        }

        key = new JiraIssueKey(JiraProjectKey.Parse(project), parsed);
        return true;
    }

    private static JiraIssueKey FromBareKey(string value) =>
        TryParseBareKey(value, out JiraIssueKey key)
            ? key
            : throw Unreadable(value);

    private static JiraIssueKey FromUrl(string reference, Uri site)
    {
        if (!Uri.TryCreate(reference, UriKind.Absolute, out Uri? url))
        {
            throw Unreadable(reference);
        }

        if (!url.Host.Equals(site.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainValidationException(
                $"{RelayedText.OneLine(reference)} is on {RelayedText.OneLine(url.Host)}, and the "
                + $"registered Jira connection is {site.Host}. A stored reference records the card key "
                + "with no site, so a card from another tenant would be filed as this one's card of the "
                + "same key. Register that site as its own connection, or pass a card from this one.");
        }

        string[] segments = url.AbsolutePath.Trim('/').Split('/');
        int browse = Array.FindIndex(segments, segment => segment.Equals("browse", StringComparison.OrdinalIgnoreCase));
        if (browse >= 0 && browse + 1 < segments.Length && TryParseBareKey(segments[browse + 1], out JiraIssueKey fromPath))
        {
            return fromPath;
        }

        // A board or backlog URL keeps the open card in the query string, and that is the URL
        // somebody copies far more often than the /browse/ one, because it is what the address
        // bar says while they are looking at the card on the board.
        return QueryValue(url.Query, "selectedIssue") is { } selected
            && TryParseBareKey(selected, out JiraIssueKey fromQuery)
                ? fromQuery
                : throw Unreadable(reference);
    }

    /// <summary>
    /// One query parameter, read by hand rather than through HttpUtility: the parameter this
    /// looks for is a Jira key, so there is no encoding subtlety to get right and no reason for
    /// a connector to pull in a web stack to split on two characters.
    /// </summary>
    private static string? QueryValue(string query, string name)
    {
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            if (equals > 0 && pair[..equals].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[(equals + 1)..]);
            }
        }

        return null;
    }

    private static DomainValidationException Unreadable(string reference) => new(
        $"'{RelayedText.OneLine(reference)}' does not name a Jira card. Use the key (PROJ-123), or the "
        + "card's URL (https://your-org.atlassian.net/browse/PROJ-123, or the board URL with the card "
        + "open).");
}

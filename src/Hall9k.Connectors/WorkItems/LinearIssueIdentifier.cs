using System.Globalization;
using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// A Linear issue's identifier, <c>ENG-123</c>, and the forms a human has to hand for one: the bare
/// identifier, the canonical <c>linear:ENG-123</c> reference Hall9k stores, and the issue URL
/// (<c>https://linear.app/&lt;workspace&gt;/issue/ENG-123/optional-title-slug</c>).
/// <para>
/// What a caller types is only ever a question to put to Linear. The reference Hall9k records is
/// the identifier Linear answered with (<see cref="LinearWorkItemProvider"/>), so a lower-case
/// spelling, a URL, and the bare identifier all land on one stored reference.
/// </para>
/// </summary>
public sealed record LinearIssueIdentifier(string TeamKey, int Number)
{
    public string Value => $"{TeamKey}-{Number.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The canonical reference this identifier is stored as on a task.</summary>
    public ExternalReference Reference => new(WorkItemProvider.Linear, Value);

    public override string ToString() => Value;

    /// <summary>
    /// The identifier, or a refusal that names every form this accepts. <paramref name="workspace"/>
    /// is the registered workspace address: a URL from any other workspace is refused rather than
    /// having its identifier taken, because a stored reference records the identifier and no
    /// workspace at all, so an issue from another workspace would be filed as this workspace's
    /// issue of the same identifier. Jira refuses the other-tenant URL for the same reason.
    /// </summary>
    public static LinearIssueIdentifier Parse(string? reference, Uri workspace)
    {
        string trimmed = reference?.Trim() ?? string.Empty;
        if (trimmed.IsBlank())
        {
            throw new DomainValidationException(
                "This needs a Linear issue to work with. Pass the identifier (ENG-123) or the issue's URL.");
        }

        if (trimmed.StartsWith($"{WorkItemProvider.Linear.Value}:", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[(WorkItemProvider.Linear.Value.Length + 1)..].Trim();
        }

        return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? FromUrl(trimmed, workspace)
                : FromBare(trimmed);
    }

    /// <summary>The rule without the throw, for a caller that wants to ask rather than try.</summary>
    public static bool TryParseBare(string value, out LinearIssueIdentifier identifier)
    {
        identifier = new LinearIssueIdentifier(string.Empty, 0);
        int dash = value.LastIndexOf('-');
        if (dash <= 0 || dash == value.Length - 1)
        {
            return false;
        }

        string team = value[..dash].Trim().ToUpperInvariant();
        string number = value[(dash + 1)..].Trim();
        if (!IsWellFormedTeamKey(team)
            || !int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            || parsed <= 0)
        {
            return false;
        }

        identifier = new LinearIssueIdentifier(team, parsed);
        return true;
    }

    /// <summary>A team key is letters and digits and starts with a letter, which is what makes <c>ENG-123</c> parse and <c>123-4</c> not.</summary>
    private static bool IsWellFormedTeamKey(string key) =>
        key.Length > 0 && char.IsAsciiLetter(key[0]) && key.All(char.IsAsciiLetterOrDigit);

    private static LinearIssueIdentifier FromBare(string value) =>
        TryParseBare(value, out LinearIssueIdentifier identifier) ? identifier : throw Unreadable(value);

    private static LinearIssueIdentifier FromUrl(string reference, Uri workspace)
    {
        if (!Uri.TryCreate(reference, UriKind.Absolute, out Uri? url))
        {
            throw Unreadable(reference);
        }

        string[] segments = url.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        string registered = workspace.AbsolutePath.Trim('/');
        if (!url.Host.Equals(workspace.Host, StringComparison.OrdinalIgnoreCase)
            || segments.Length < 3
            || !segments[1].Equals("issue", StringComparison.OrdinalIgnoreCase))
        {
            throw Unreadable(reference);
        }

        if (!segments[0].Equals(registered, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainValidationException(
                $"{RelayedText.OneLine(reference)} is in the Linear workspace '{RelayedText.OneLine(segments[0])}', "
                + $"and the registered Linear connection is '{registered}'. A stored reference records the "
                + "identifier with no workspace, so an issue from another workspace would be filed as this "
                + "workspace's issue of the same identifier. Pass an issue from the registered workspace; an "
                + "install holds one Linear workspace (h9k connection add linear).");
        }

        return TryParseBare(segments[2], out LinearIssueIdentifier identifier) ? identifier : throw Unreadable(reference);
    }

    private static DomainValidationException Unreadable(string reference) => new(
        $"'{RelayedText.OneLine(reference)}' does not name a Linear issue. Use the identifier (ENG-123), or the "
        + "issue's URL (https://linear.app/your-workspace/issue/ENG-123).");
}

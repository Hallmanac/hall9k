using System.Text.RegularExpressions;

namespace Hall9k.Connectors.Text;

/// <summary>
/// What a Linear issue identifier looks like in free text, taken from the matcher Linear publishes
/// in its linear-release tool (<c>src/extractors.ts</c>): one to seven word characters, a hyphen, and
/// one to nine digits, not followed by <c>.</c> and a digit, matched without regard to case. The
/// shape is all the text alone can tell: whether the team key belongs to a real team is a fact about
/// a workspace, which is why every caller that acts on a match asks a set of team keys as well.
/// <para>
/// Two additions to the published pattern keep a match whole. A leading word character rules out a
/// match that starts mid-token, so an eight-character prefix is not read as its last seven, and a
/// trailing digit rules out the backtracking that would otherwise read <c>ENG-123.5</c> as
/// <c>ENG-12</c> followed by a digit the dot rule never saw.
/// </para>
/// </summary>
internal static class LinearIdentifierShape
{
    private const string Body = @"(?<key>[A-Za-z0-9_]{1,7})-(?<number>[0-9]{1,9})(?![0-9])";

    private const string IssueUrl =
        @"https?://linear\.app/(?<workspace>[^/\s)\]>]+)/issue/" + Body + @"(?:/[A-Za-z0-9._~%-]*)?";

    private const RegexOptions UrlOptions =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    /// <summary>An identifier anywhere in the text.</summary>
    public static readonly Regex Anywhere = new(
        @"(?<![A-Za-z0-9_])" + Body + @"(?!\.[0-9])", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>An identifier starting exactly where the match is started.</summary>
    public static readonly Regex At = new(
        @"\G(?<![A-Za-z0-9_])" + Body + @"(?!\.[0-9])", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The whole string is one identifier, for a link's text or destination.</summary>
    public static readonly Regex Whole = new(
        @"^" + Body + @"$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// A Linear issue address, <c>https://linear.app/&lt;workspace&gt;/issue/ENG-123</c>, with or
    /// without the title slug that follows the identifier.
    /// </summary>
    public static readonly Regex IssueUrlAt = new(@"\G" + IssueUrl, UrlOptions);

    /// <summary>Every Linear issue address in the text.</summary>
    public static readonly Regex IssueUrls = new(IssueUrl, UrlOptions);

    /// <summary>
    /// The identifier in canonical form: the team key in capitals and the number without leading
    /// zeros, which is how <see cref="Hall9k.Connectors.WorkItems.LinearIssueIdentifier.Value"/> spells it.
    /// </summary>
    public static string Canonical(string key, string number) =>
        $"{key.ToUpperInvariant()}-{(number.TrimStart('0') is { Length: > 0 } trimmed ? trimmed : "0")}";

    public static string Canonical(Match match) => Canonical(match.Groups["key"].Value, match.Groups["number"].Value);

    /// <summary>The team key of a canonical identifier.</summary>
    public static string KeyOf(string identifier) => identifier[..identifier.LastIndexOf('-')];

    /// <summary>The canonical identifier a string is, whole, or null when it is not one.</summary>
    public static string? ParseWhole(string text)
    {
        Match match = Whole.Match(text.Trim());
        return match.Success ? Canonical(match) : null;
    }
}

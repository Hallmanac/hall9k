using System.Text.RegularExpressions;

namespace Hall9k.Connectors.Text;

/// <summary>
/// The words Linear acts on when one sits in front of an issue identifier, kept in the one place the
/// list lives because Linear moves it (five changes in seven months at the time of writing): a
/// closing word moves the issue when the pull request opens and again when it merges, a non-closing
/// word still links it and can move it through the team's other statuses, and an unlinking word
/// removes the link. The relation words (<c>relates to</c>, <c>related to</c>) are deliberately not
/// here: Linear documents them as changing no status, which makes them the way hall9k text may name
/// another issue.
/// </summary>
internal static class LinearMagicWords
{
    /// <summary>Closing words, in every form Linear lists, plus the phrase <c>linear issue</c>, which is itself a closing phrase.</summary>
    public static readonly IReadOnlyList<string> Closing =
    [
        "close", "closes", "closed", "closing",
        "fix", "fixes", "fixed", "fixing",
        "resolve", "resolves", "resolved", "resolving",
        "complete", "completes", "completed", "completing",
        "implement", "implements", "implemented", "implementing",
        "linear issue",
    ];

    /// <summary>Words that link without closing. <c>updates</c> is the newest, and is on Linear's changelog and GitLab page rather than its GitHub page.</summary>
    public static readonly IReadOnlyList<string> NonClosing =
    [
        "ref", "refs", "references", "part of", "contributes to", "toward", "towards", "updates",
    ];

    /// <summary>Words that unlink a pull request from an issue, which is a state change of its own, so they are guarded before any identifier, the task's own included.</summary>
    public static readonly IReadOnlyList<string> Unlinking = ["skip", "ignore"];

    /// <summary>One whitespace character that is not a line break, which is what <c>\s</c> is on a single line.</summary>
    public const string LineSpace = @"[^\S\r\n]";

    /// <summary>
    /// A linking word and the run of colons and whitespace on its line that Linear lets sit between it
    /// and an identifier. Linear's published pattern says <c>[\s:]+</c>, so the whitespace is every
    /// Unicode space (a non-breaking space pasted from a rich-text source included) and not only the
    /// space and the tab; a line break is left out because the matcher reads one line at a time.
    /// The <c>unlinking</c> group is set when the word is one of <see cref="Unlinking"/>. Case is ignored, as Linear's own matcher ignores it.
    /// </summary>
    public static readonly Regex LinkingWord = new(
        @"(?<![A-Za-z0-9_])(?:(?<unlinking>" + Alternation(Unlinking) + ")|" + Alternation([.. Closing, .. NonClosing]) + @")(?:" + LineSpace + "|:)+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static string Alternation(IEnumerable<string> words) =>
        string.Join('|', words.Select(word => Regex.Escape(word).Replace("\\ ", LineSpace + "+", StringComparison.Ordinal)));
}

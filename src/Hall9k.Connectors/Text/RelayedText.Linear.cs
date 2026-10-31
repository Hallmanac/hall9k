using System.Text;
using System.Text.RegularExpressions;

namespace Hall9k.Connectors.Text;

/// <summary>
/// The Linear half of what pull request text needs before it leaves the machine. Linear acts on a
/// magic word in a pull request's title or description (<see cref="LinearMagicWords"/>) and links
/// a pull request from a bare identifier in its title, so text Hall9k relays or composes on a
/// Linear-tracked project must not carry either toward an issue the task does not own.
/// <para>
/// It is a separate rule from <see cref="WithoutClosingKeywords"/> on purpose. A Linear identifier
/// has the same shape as a Jira key, so a rule that fired on one would fire on the other, and the
/// callers of the GitHub rule know no project. These functions are only ever called with a task's
/// own identifier and a set of team keys, which a caller that knows it is on a Linear-tracked
/// project supplies.
/// </para>
/// <para>
/// The guard works on the raw text and is deliberately not markdown-aware: research into Linear's
/// matcher (the linear-magic-word-research.md note in the connector's discovery workspace) found
/// that Linear acts on a whole phrase inside a code span or a fenced block, and that only a
/// character between the word and the identifier stopped it. Its published pattern allows only
/// whitespace or a colon between the two, so the guard breaks that adjacency by inserting a
/// neutral word, and does not depend on whether Linear reads code. The inserted word is never
/// <c>issue</c>, because <c>linear issue</c> is itself a closing phrase.
/// </para>
/// </summary>
public static partial class RelayedText
{
    /// <summary>The word inserted between a linking word and an identifier it must not reach.</summary>
    private const string NeutralWord = "ticket";

    private static readonly Regex MarkdownLinkAt = new(
        @"\G\[(?<text>[^\]\n]*)\]\((?<destination>[^)\n]*)\)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>What may join the targets of one run: commas, spaces, tabs, <c>&amp;</c>, and the word <c>and</c>.</summary>
    private static readonly Regex RunSeparator = new(
        @"\G(?:[ \t,&]|(?<![A-Za-z0-9_])and(?![A-Za-z0-9_]))+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The form that asks Linear to create an issue in a team, matched in upper case only.</summary>
    private static readonly Regex NewIssueToken = new(
        @"(?<![A-Za-z0-9_])(?<key>[A-Z][A-Z0-9]{0,6})-NEW(?![A-Za-z0-9_])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Characters that only framed a dropped identifier: whitespace and the punctuation a title puts between a key and its words.</summary>
    private static readonly char[] FramingSeparators = [' ', '\t', ':', ';', ',', '|', '/', '-', '\u2013', '\u2014'];

    private readonly record struct LinearTarget(int Start, int End, string Identifier);

    /// <summary>
    /// Whether the text holds a token that could be a Linear identifier other than
    /// <paramref name="ownIdentifier"/>, or a <c>{TEAM}-NEW</c> token. This is the shape alone and
    /// it fires on <c>SHA-256</c> too: it decides only whether it is worth asking Linear which team
    /// keys exist, so a pull request that names nothing but its own issue never makes the call.
    /// </summary>
    public static bool HasLinearKeyShape(string text, string? ownIdentifier)
    {
        string? own = NormalizeOwn(ownIdentifier);

        // An issue address is read as the identifier it names. Its workspace and its title slug are
        // words that can look like keys (a workspace called acme-2, a slug ending in sha-256), and a
        // pull request that links only its own issue must not be taken to name another one.
        string readable = LinearIdentifierShape.IssueUrls.Replace(text, url => $" {LinearIdentifierShape.Canonical(url)} ");
        foreach (Match match in LinearIdentifierShape.Anywhere.Matches(readable))
        {
            if (!string.Equals(LinearIdentifierShape.Canonical(match), own, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return NewIssueToken.IsMatch(readable);
    }

    /// <summary>
    /// The text with the neutral word <c>ticket</c> put directly before the first target of every
    /// run that follows a linking word and names an identifier it must not reach, and every
    /// <c>{TEAM}-NEW</c> token losing its hyphen so it cannot create an issue.
    /// <para>
    /// A linking word counts, in any case, when only spaces, tabs, or colons separate it on the same
    /// line from a run of targets. A target is an identifier, a Linear issue URL with or without its
    /// slug, or a markdown link to either, and a run is one target or several joined by commas,
    /// whitespace, <c>and</c>, or <c>&amp;</c>. When any target in the run is another issue's, the
    /// word goes before the first such target, so <c>Fixes ENG-123, DES-5</c> on task ENG-123 reads
    /// <c>Fixes ENG-123, ticket DES-5</c>. The unlinking words (<c>skip</c>, <c>ignore</c>) get the
    /// same treatment before any identifier, the task's own included, because they unlink a pull
    /// request. A closing or non-closing word before the task's own identifier alone is left alone,
    /// as are the relation words, which Linear documents as changing no status.
    /// </para>
    /// <para>
    /// <paramref name="teamKeys"/> is the set of team keys the install's Linear connection can see
    /// (upper case), and an identifier counts only when its key is in it, so <c>Fix SHA-256 hashing</c>
    /// stays as written. Null means the read failed or was not possible: the guard then matches by
    /// shape. It is idempotent, because the word it inserts is not a target.
    /// </para>
    /// </summary>
    public static string WithoutLinearMagicWords(string text, string? ownIdentifier, IReadOnlySet<string>? teamKeys)
    {
        string? own = NormalizeOwn(ownIdentifier);
        List<int> insertions = [];
        foreach (Match word in LinearMagicWords.LinkingWord.Matches(text))
        {
            List<LinearTarget> run = ReadRun(text, word.Index + word.Length);
            bool unlinks = word.Groups["unlinking"].Success;
            foreach (LinearTarget target in run)
            {
                if (CountsAsIssue(target.Identifier, teamKeys)
                    && (unlinks || !string.Equals(target.Identifier, own, StringComparison.Ordinal)))
                {
                    insertions.Add(target.Start);
                    break;
                }
            }
        }

        string guarded = Insert(text, insertions);
        return NewIssueToken.IsMatch(guarded)
            ? NewIssueToken.Replace(guarded, match => CountsAsTeam(match.Groups["key"].Value, teamKeys)
                ? $"{match.Groups["key"].Value} NEW"
                : match.Value)
            : guarded;
    }

    /// <summary>
    /// A one-line title with every identifier of another issue dropped, together with the Linear
    /// issue URL or markdown link that carries it and the brackets, backticks, or separator that only
    /// framed it, because a bare identifier in a pull request title links it with no magic word at
    /// all. <c>DES-5: Fix login</c> and <c>[DES-5] Fix login</c> both come out as <c>Fix login</c>,
    /// and <c>ENG-1234 compatibility</c> under task ENG-123 as <c>compatibility</c>. The task's own
    /// identifier is left alone. A title naming no other issue comes back untouched.
    /// <para>
    /// Only meaningful with <paramref name="teamKeys"/> in hand: a title is never rewritten on the
    /// strength of a shape alone, so a caller whose team-key read failed skips this and relies on
    /// <see cref="WithoutLinearMagicWords"/>.
    /// </para>
    /// </summary>
    public static string WithoutOtherLinearIdentifiers(string title, string? ownIdentifier, IReadOnlySet<string> teamKeys)
    {
        string? own = NormalizeOwn(ownIdentifier);
        string current = title;
        bool dropped = false;
        while (FirstOtherIssue(current, own, teamKeys) is { } target)
        {
            current = Removed(current, target);
            dropped = true;
        }

        return dropped ? Regex.Replace(current, " {2,}", " ").Trim() : title;
    }

    private static string? NormalizeOwn(string? ownIdentifier) =>
        ownIdentifier is null ? null : LinearIdentifierShape.ParseWhole(ownIdentifier) ?? ownIdentifier.Trim().ToUpperInvariant();

    private static bool CountsAsIssue(string identifier, IReadOnlySet<string>? teamKeys) =>
        CountsAsTeam(LinearIdentifierShape.KeyOf(identifier), teamKeys);

    private static bool CountsAsTeam(string key, IReadOnlySet<string>? teamKeys) =>
        teamKeys is null
        || teamKeys.Contains(key)
        || teamKeys.Any(known => known.Equals(key, StringComparison.OrdinalIgnoreCase));

    private static string Insert(string text, List<int> positions)
    {
        if (positions.Count == 0)
        {
            return text;
        }

        StringBuilder guarded = new(text.Length + positions.Count * (NeutralWord.Length + 1));
        int copied = 0;
        foreach (int position in positions.Order())
        {
            guarded.Append(text, copied, position - copied).Append(NeutralWord).Append(' ');
            copied = position;
        }

        return guarded.Append(text, copied, text.Length - copied).ToString();
    }

    /// <summary>The targets of one run, starting at <paramref name="start"/>: the first, then each one a separator away from the last.</summary>
    private static List<LinearTarget> ReadRun(string text, int start)
    {
        List<LinearTarget> run = [];
        int cursor = start;
        while (TargetAt(text, cursor) is { } target)
        {
            run.Add(target);
            Match separator = RunSeparator.Match(text, target.End);
            if (!separator.Success)
            {
                break;
            }

            cursor = target.End + separator.Length;
        }

        return run;
    }

    private static LinearTarget? TargetAt(string text, int position)
    {
        if (position >= text.Length)
        {
            return null;
        }

        if (text[position] == '[')
        {
            Match link = MarkdownLinkAt.Match(text, position);
            return link.Success && IdentifierOfLink(link) is { } linked
                ? new LinearTarget(position, position + link.Length, linked)
                : null;
        }

        Match url = LinearIdentifierShape.IssueUrlAt.Match(text, position);
        if (url.Success)
        {
            return new LinearTarget(position, position + url.Length, LinearIdentifierShape.Canonical(url));
        }

        Match bare = LinearIdentifierShape.At.Match(text, position);
        return bare.Success ? new LinearTarget(position, position + bare.Length, LinearIdentifierShape.Canonical(bare)) : null;
    }

    /// <summary>
    /// The identifier a markdown link names: the one its destination is (a bare identifier or a
    /// Linear issue URL), or failing that the one its text is. A link to anything else names none.
    /// </summary>
    private static string? IdentifierOfLink(Match link)
    {
        string destination = link.Groups["destination"].Value.Trim().TrimStart('<');
        int end = destination.IndexOfAny([' ', '\t', '>']);
        destination = end < 0 ? destination : destination[..end];

        Match url = LinearIdentifierShape.IssueUrlAt.Match(destination);
        return url.Success
            ? LinearIdentifierShape.Canonical(url)
            : LinearIdentifierShape.ParseWhole(destination) ?? LinearIdentifierShape.ParseWhole(link.Groups["text"].Value);
    }

    private static LinearTarget? FirstOtherIssue(string text, string? own, IReadOnlySet<string> teamKeys)
    {
        for (int position = 0; position < text.Length;)
        {
            if (TargetAt(text, position) is not { } target)
            {
                position++;
                continue;
            }

            if (CountsAsIssue(target.Identifier, teamKeys)
                && !string.Equals(target.Identifier, own, StringComparison.Ordinal))
            {
                return target;
            }

            position = target.End;
        }

        return null;
    }

    private static string Removed(string text, LinearTarget target)
    {
        int start = target.Start;
        int end = target.End;
        while (start > 0 && end < text.Length && Frames(text[start - 1], text[end]))
        {
            start--;
            end++;
        }

        string before = text[..start];
        string after = text[end..];
        if (before.Trim().Length == 0)
        {
            return after.TrimStart(FramingSeparators);
        }

        if (after.Trim().Length == 0)
        {
            return before.TrimEnd(FramingSeparators);
        }

        string left = before.TrimEnd();
        string right = after.TrimStart();
        if (right.Length > 0 && right[0] is ':' or ',' or ';')
        {
            right = right[1..].TrimStart();
        }

        // Closing punctuation hugs the word before it: "Fixes DES-5." is "Fixes.", not "Fixes .".
        return left.Length > 0 && right.Length > 0 && right[0] is not ('.' or '!' or '?') ? $"{left} {right}" : left + right;
    }

    private static bool Frames(char open, char close) => (open, close) switch
    {
        ('[', ']') or ('(', ')') or ('<', '>') or ('{', '}') or ('`', '`') => true,
        _ => false,
    };
}

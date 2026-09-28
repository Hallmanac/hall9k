using System.Globalization;
using System.Text;

namespace Hall9k.Domain.Shared.ValueObjects;

/// <summary>
/// The subset of <c>git check-ref-format --branch</c> a branch name can violate before it is safe
/// to hand git as a command-line argument, extracted from <c>BranchNameTemplate</c>'s own rendered-name
/// check (security review idea 6be68ee2, process-injection finding 2) so every OTHER carrier of a
/// branch string this platform ever hands to git can be refused the identical way. A rendered
/// template is only one of them: <c>TaskReopened.Branch</c>, <c>TaskRetried.Branch</c>,
/// <c>TaskHandedBack.Branch</c>, <c>TaskClaimed.ResumesBranch</c>,
/// <c>RemoteStackedParentObserved.HeadBranch</c>, and a previous run's own recorded
/// <c>BaseBranch</c> all replicate from another fleet node and reach git through
/// <c>GitWorktreeManager</c>, <c>MergedBranchCleanup</c>, <c>StackedParentWatch</c> or
/// <c>ReviewEngine</c> without ever passing through a project's template — so the one thing that
/// has to hold for every one of them is this check, run at whichever of those sites is about to
/// turn the value into a git argument, not a rule re-derived once per carrier at the inbox.
/// <para>
/// Deliberately without <c>BranchNameTemplate</c>'s own rendered-length ceiling: that ceiling
/// exists to leave room for the run-suffixed retry name a project's own template might collide
/// into (<c>-rXXXX</c>), which is a template-authoring concern, not a git-argument-safety one — an
/// inbound branch carried by a replicated event is never retried with that suffix, and refusing a
/// long-but-otherwise-legal one here would refuse a legitimate retried branch
/// (<c>GitWorktreeManager.ResolveBranchNameAsync</c>'s own collision suffix) for no safety reason.
/// </para>
/// <para>
/// Adds one rule <c>BranchNameTemplate</c>'s own check never needed: a leading <c>+</c> is legal in
/// a git ref name, but a leading <c>+</c> in a fetch or push refspec forces the update instead of
/// asking for a fast-forward — exactly the kind of meaning a value arriving from another fleet node
/// must never be allowed to carry into a git argument built from it.
/// </para>
/// </summary>
public static class GitArgumentValidation
{
    /// <summary>How much of a refused value a log line or a park message is willing to echo back.</summary>
    private const int MaximumRelayedLength = 80;

    /// <summary>
    /// Whether <paramref name="branch"/> is safe to hand git as a branch-name argument.
    /// <paramref name="refusalReason"/> names the rule it broke, in a sentence safe to log or show
    /// a human, whenever this returns <see langword="false"/>; null whenever it returns
    /// <see langword="true"/>.
    /// </summary>
    public static bool IsLegalBranchName(string branch, out string? refusalReason)
    {
        if (branch.Length == 0)
        {
            refusalReason = "it is empty.";
            return false;
        }

        // Enumerated as Rune, not char: a foreach over a string yields UTF-16 code units, and a
        // non-BMP Unicode formatting character (a TAG character such as U+E0041, category Cf) is a
        // surrogate pair whose two halves each categorise as Surrogate rather than Format —
        // invisible to a per-char check of char.GetUnicodeCategory even though the whole character
        // is exactly what that check exists to catch (independent pre-PR review, cycle 3,
        // adversarial, carried over from BranchNameTemplate's own original check).
        foreach (Rune rune in branch.EnumerateRunes())
        {
            // Every character these two rules bar is ASCII, so a rune outside the BMP can never
            // match either — the cast is exact whenever it can possibly matter.
            if (rune.Value <= char.MaxValue)
            {
                char character = (char)rune.Value;

                // char.IsControl covers DEL as well as the C0 range, so both ends of git's own "no
                // control characters" rule are here.
                if (char.IsControl(character) || character is ' ' or '~' or '^' or ':' or '?' or '*' or '[' or '\\')
                {
                    refusalReason = $"{Describe(character)} is a character git does not allow in a ref name.";
                    return false;
                }

                // git allows '"' in a ref name, but GitWorktreeManager interpolates a branch
                // straight into a single ProcessStartInfo.Arguments string, where '"' is the
                // quoting character itself — a name carrying one parses into the wrong argv,
                // breaking every git invocation this value is used in.
                if (character == '"')
                {
                    refusalReason =
                        $"{Describe(character)} is a character git allows in a ref name, but this platform "
                        + "passes a branch through a single quoted command-line argument, which a '\"' would "
                        + "break out of.";
                    return false;
                }
            }

            // Stricter than git's own, deliberately: git takes a Unicode formatting character in a
            // ref name quite happily, but a branch name is printed into terminals, logs,
            // pull-request bodies and this platform's own board, and a bidirectional override makes
            // one name read on screen as another.
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format)
            {
                refusalReason =
                    $"{Describe(rune)} is a Unicode formatting character. git would take it, but a branch "
                    + "name is printed wherever this platform reports work, and a bidirectional override "
                    + "there makes one name read as another.";
                return false;
            }
        }

        if (branch.Contains("..", StringComparison.Ordinal))
        {
            refusalReason = "git does not allow '..' in a ref name.";
            return false;
        }

        if (branch.Contains("@{", StringComparison.Ordinal))
        {
            refusalReason = "git does not allow '@{' in a ref name.";
            return false;
        }

        if (branch is "@")
        {
            refusalReason = "'@' on its own is not a ref name git accepts.";
            return false;
        }

        if (branch[0] == '-')
        {
            refusalReason = "a branch name cannot begin with '-' — git reads it as an option.";
            return false;
        }

        // Legal to git, refused here anyway: a leading '+' in a fetch or push refspec forces the
        // update rather than asking for a fast-forward, and this value reaches git exactly where
        // that meaning applies — a carrier this platform never controls must never be able to opt
        // into it (security review idea 6be68ee2, process-injection finding 2).
        if (branch[0] == '+')
        {
            refusalReason =
                "a branch name cannot begin with '+' — git allows it in a ref name, but a leading '+' in a "
                + "fetch or push refspec forces the update instead of asking for a fast-forward, and this "
                + "value is handed to git exactly where that meaning would apply.";
            return false;
        }

        if (branch[^1] == '.')
        {
            refusalReason = "a ref name cannot end with '.'.";
            return false;
        }

        foreach (string component in branch.Split('/'))
        {
            if (component.Length == 0)
            {
                refusalReason =
                    "a ref name has no empty path components — it cannot start or end with '/', or contain '//'.";
                return false;
            }

            if (component[0] == '.')
            {
                refusalReason =
                    $"'{Printable(component)}' begins with '.', which git does not allow in a ref path component.";
                return false;
            }

            if (component.EndsWith(".lock", StringComparison.Ordinal))
            {
                refusalReason =
                    $"'{Printable(component)}' ends with '.lock', which git reserves for its own lock files.";
                return false;
            }
        }

        refusalReason = null;
        return true;
    }

    /// <summary>
    /// Whether <paramref name="value"/> is shaped like a git object id — 40 hex characters (SHA-1)
    /// or 64 (SHA-256) — the one form a commit carried by a replicated event (a previous run's own
    /// <c>BaseCommit</c>) is safe to hand git positionally, in a <c>git merge-base</c> call that
    /// takes no <c>--</c> of its own (unlike a branch, a commit id can never be option- or
    /// refspec-shaped once it is confirmed to be nothing but hex digits at exactly one of the two
    /// lengths git actually uses).
    /// </summary>
    public static bool IsLegalCommitSha(string value) =>
        value.Length is 40 or 64 && value.All(Uri.IsHexDigit);

    /// <summary>
    /// What a refused value is safe to be quoted as in a log line or a park message: the same
    /// convention <c>BranchNameTemplate</c>'s own relay follows (<c>RelayedPolicy</c>) — printable
    /// ASCII rather than a hand-picked allowlist, so a control character or a bidirectional
    /// override cannot reach whatever this is embedded in, and an unbounded value cannot be echoed
    /// whole.
    /// </summary>
    public static string Printable(string value)
    {
        // Rune, not char: a non-BMP character is a surrogate pair, and reading it as two chars
        // would render it as two '?'s rather than by its own code point.
        StringBuilder visible = new();
        int runeCount = 0;
        bool truncated = false;
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (runeCount >= MaximumRelayedLength)
            {
                truncated = true;
                break;
            }

            visible.Append(Readable(rune));
            runeCount++;
        }

        return truncated ? visible.Append('…').ToString() : visible.ToString();
    }

    private static string Readable(Rune rune) => IsRunePrintable(rune) ? rune.ToString() : "?";

    /// <summary>
    /// One offending character, named so the operator can find it: itself when it is printable, and
    /// its code point when it is not, since <c>'?'</c> for an unprintable character reads as a claim
    /// about a character the value does not contain.
    /// </summary>
    private static string Describe(char character) => Describe(new Rune(character));

    private static string Describe(Rune rune) =>
        IsRunePrintable(rune) ? $"'{rune}'" : $"U+{rune.Value:X4}";

    private static bool IsRunePrintable(Rune rune) => rune.IsAscii && rune.Value is >= ' ' and <= '~';
}

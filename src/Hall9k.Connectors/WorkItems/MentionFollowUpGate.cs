using Hall9k.Connectors.Text;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// Whether a mention follow-up may launch a session for a stored comment: this install's own login
/// must be the one the comment tags, and the comment must not be one that login wrote itself. Pure
/// over the stored comment and the login <c>gh</c> reported, so it re-runs the match against the
/// comment's own body rather than trusting a login the event recorded (an event from an older node
/// records none), and it runs on every path that reaches a launch.
/// <para>
/// <see cref="Reason"/> is a full sentence for the skip event and the log, naming the author and
/// the tagged login separately; empty when <see cref="Proceed"/> is true.
/// </para>
/// </summary>
public sealed record MentionFollowUpGate(bool Proceed, string Reason)
{
    /// <param name="ownLogin">The login <c>gh</c> is authenticated as right now, or null when it could not be read.</param>
    /// <param name="authorLogin">Who wrote the stored comment.</param>
    /// <param name="body">The stored comment's own text.</param>
    public static MentionFollowUpGate Decide(string? ownLogin, string authorLogin, string body)
    {
        if (ownLogin.IsBlank())
        {
            return new MentionFollowUpGate(
                false,
                "this install's own GitHub login could not be read, so there is no way to tell whether the "
                + "comment tags it");
        }

        if (string.Equals(authorLogin, ownLogin, StringComparison.OrdinalIgnoreCase))
        {
            return new MentionFollowUpGate(
                false, $"the comment was written by {ownLogin}, this install's own login, so it is not addressed to it");
        }

        return GitHubReviewAssignments.MentionsLogin(body, ownLogin)
            ? new MentionFollowUpGate(true, string.Empty)
            : new MentionFollowUpGate(
                false,
                $"the comment by {RelayedText.OneLine(authorLogin)} does not tag {ownLogin}, this install's own "
                + "login, so it is addressed to someone else");
    }
}

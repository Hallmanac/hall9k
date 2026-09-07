using Hall9k.Connectors.Text;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// Why a tracker could not say who holds an item — and therefore what could possibly end the hold
/// the gate places on it (idea 64c75e43). An in-process outcome, never persisted as itself
/// (AGENTS.md: enums only for unpersisted in-process outcomes): what the published
/// <c>TrackerClaimHold</c> carries is the lever this chose and the one distinction a reader has to
/// act on, whether the credentials were refused.
/// <para>
/// Three failures with three different remedies, and getting them wrong is worse than saying
/// nothing: telling an operator to wait out an outage when the tracker gave a definitive answer
/// about a key it does not have — or about an account that cannot see the project — names a wait
/// that will never end.
/// </para>
/// </summary>
public enum TrackerReadFailure
{
    /// <summary>Nothing failed; the tracker answered.</summary>
    None,

    /// <summary>The tracker refused these credentials, or none could be produced. Renew the token, sign in again.</summary>
    Credentials,

    /// <summary>
    /// The tracker answered definitively about this item and the answer was not an assignment: the
    /// key does not resolve, or this account cannot see it. Nothing on this install retries into a
    /// different answer, so the remedy is at the tracker, and the tracker's own words already say
    /// which of the two it was (both trackers deliberately answer the same way for a missing item
    /// and an invisible one).
    /// </summary>
    Item,

    /// <summary>The tracker failed to answer at all — a 5xx, a rate limit, a timeout, an unreachable host. Wait it out.</summary>
    Outage,
}

/// <summary>
/// One person a tracker says holds an item, as the tracker itself spells them:
/// <see cref="Identity"/> is what the assignee field actually carries and the only thing ever
/// compared — a Jira <c>accountId</c>, or a GitHub login — and <see cref="Name"/> is the
/// human-readable name beside it when the tracker offered one, honestly null when it did not
/// (AGENTS.md, never guess at unobserved facts). A GitHub login is the whole of what <c>gh</c>
/// answers for an assignee, so <see cref="Name"/> is null there rather than the login repeated.
/// <para>
/// Both fields stay byte-for-byte what the tracker said, because they are what
/// <c>TrackerAssignmentObserved</c> records as the evidence a claim rested on, and an audit field
/// that was quietly rewritten is worth less than one that says what was actually seen. It is
/// <see cref="Described"/> — the only one of them that reaches a human — that is made safe to
/// print (<see cref="Rendered"/>).
/// </para>
/// </summary>
public sealed record TrackerAssignee(string Identity, string? Name)
{
    /// <summary>How this holder is named to a human: the display name where there is one, the raw identity otherwise.</summary>
    public string Described => Rendered(Name.IsNotBlank() ? Name! : Identity);

    /// <summary>
    /// A tracker's own words about who somebody is, made safe to put in a sentence bound for a
    /// terminal (independent pre-PR review, cycle 1, adversarial lens). A Jira
    /// <c>displayName</c> is set by whoever owns that account on a shared tenant, so it is
    /// relayed text exactly like an issue title: a bidirectional override in it reverses what a
    /// refusal appears to say without changing a byte of what is stored, and a newline in it
    /// prints lines of its own choosing under a one-line status row. Bounded for the same reason
    /// <c>GitHubWorkItemProvider.Head</c> bounds what it quotes — a name nobody reads to the end
    /// teaches nothing, and this one is framed inside a row.
    /// <para>
    /// It is also how this install's own identity is rendered
    /// (<see cref="TrackerClaimDecision"/>'s identity clause), since that field is the same kind
    /// of thing from the same JSON.
    /// </para>
    /// </summary>
    internal static string Rendered(string identityText) =>
        RelayedText.Truncate(RelayedText.OneLine(identityText).Trim(), 200);
}

/// <summary>
/// Who a tracker says holds one item right now, or why it could not say (idea 64c75e43). Three
/// outcomes, and only one of them is an error: somebody holds it, nobody does, or the read
/// failed — which the claim gate turns into a claim, a refusal naming the holder, and a
/// fails-closed hold quoting the tracker verbatim.
/// <para>
/// <see cref="Error"/> is the tracker's own sentence, unaltered, because the platform's guess
/// about a permission or an outage is worth less than the tracker's own answer.
/// <see cref="Failure"/> is the one classification made on top of it, and it decides which lever
/// the hold prints; <see cref="AuthenticationRefusal"/> is the single distinction a reader acts on
/// most often, derived from it rather than tracked twice.
/// </para>
/// <para>
/// The list is plural because GitHub issues may carry several assignees and being among them
/// passes; Jira has exactly one, so its list is empty or one long.
/// </para>
/// <para>
/// <see cref="IdentityComparison"/> belongs to the provider that produced the read rather than to
/// the caller comparing it: GitHub logins are case-insensitive, and the platform already matches
/// them that way (<see cref="GitHubReviewAssignments.ParseMostRecentRequestActor"/>), while a Jira
/// <c>accountId</c> is an opaque token nothing licenses folding the case of — two ids differing
/// only in case are two accounts until Atlassian says otherwise, and quietly treating them as one
/// would let the gate pass for somebody else.
/// </para>
/// </summary>
public sealed record TrackerAssigneeRead(
    IReadOnlyList<TrackerAssignee> Assignees,
    StringComparison IdentityComparison = StringComparison.Ordinal,
    string? Error = null,
    TrackerReadFailure Failure = TrackerReadFailure.None)
{
    /// <summary>The item was read and carries no assignee at all.</summary>
    public static TrackerAssigneeRead Nobody(StringComparison identityComparison = StringComparison.Ordinal) =>
        new([], identityComparison);

    /// <summary>The item was read and one person holds it — the Jira shape.</summary>
    public static TrackerAssigneeRead HeldBy(
        string identity, string? name, StringComparison identityComparison = StringComparison.Ordinal) =>
        new([new TrackerAssignee(identity, name)], identityComparison);

    /// <summary>The item was read and any number of people hold it — the GitHub-issue shape.</summary>
    public static TrackerAssigneeRead HeldBy(
        IReadOnlyList<TrackerAssignee> assignees, StringComparison identityComparison = StringComparison.Ordinal) =>
        new(assignees, identityComparison);

    /// <summary>The tracker could not say, and this is the sentence it said instead.</summary>
    public static TrackerAssigneeRead Unreadable(string error, TrackerReadFailure failure) =>
        new([], StringComparison.Ordinal, error, failure);

    /// <summary>Whether this read failed rather than answered — which is what makes the gate fail closed.</summary>
    public bool Failed => Error is not null;

    /// <summary>
    /// Whether the tracker was refusing these credentials rather than failing to answer — the one
    /// distinction that tells "renew the token" apart from every other remedy.
    /// </summary>
    public bool AuthenticationRefusal => Failure == TrackerReadFailure.Credentials;

    /// <summary>Every holder as a human reads them, in the order the tracker listed them.</summary>
    public string DescribeHolders() => string.Join(", ", Assignees.Select(assignee => assignee.Described));
}

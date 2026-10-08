namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// What kind of account GitHub says an author is, in the three outcomes that behave differently
/// here. Unpersisted in-process vocabulary (TASK-MODEL.md §8 allows an enum for exactly that).
/// </summary>
public enum GitHubActorKind
{
    /// <summary>A person: User, and every other actor type that is not a bot or a mannequin.</summary>
    Person,

    /// <summary>An app account (GitHub's Bot actor type), including the Copilot reviewer.</summary>
    Bot,

    /// <summary>An unclaimed placeholder identity an import never mapped to a real account.</summary>
    Mannequin,
}

/// <summary>
/// The one rule for telling a bot's words from a person's, shared by the daemon's closeout reads
/// (<c>GitHubPullRequestInspector</c>) and the CLI's own review-body read
/// (<see cref="GitHubPullRequestReviews"/>), so the two can never disagree about whose review a
/// session is answering. It lives in Connectors because the CLI cannot reference
/// <c>Hall9k.Daemon</c> and both processes need the identical answer.
/// </summary>
public static class GitHubActors
{
    // Copilot's reviewer authors under a small set of known app logins: GraphQL reports
    // the bare form (copilot-pull-request-reviewer), REST the [bot]-suffixed form, and
    // the unified Copilot app surfaces as plain Copilot. Exact match after stripping the
    // suffix — a collaborator whose login merely contains "copilot" is not the reviewer
    // bot, and misclassifying one would hold the run at ReviewPending and spend the
    // automatic closeout budget re-requesting reviews from an account that cannot answer.
    private static readonly string[] CopilotLogins = ["copilot", "copilot-pull-request-reviewer"];

    public static bool IsCopilotLogin(string? login)
    {
        if (login is null)
        {
            return false;
        }

        string bare = login.EndsWith("[bot]", StringComparison.Ordinal)
            ? login[..^"[bot]".Length]
            : login;
        return CopilotLogins.Contains(bare, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The provider's actor type mapped to the three kinds that behave differently here. Only
    /// Bot and Mannequin are named: every other actor type GitHub reports for an author is a
    /// person (User today, EnterpriseUserAccount in an enterprise tenant), so the default is
    /// Person deliberately — an unfamiliar type must not silently vanish from the human thread
    /// count, which is what tells a follow-up that somebody is waiting on an answer. The known
    /// Copilot logins are an extra yes for Bot rather than the rule, because the unified Copilot
    /// app has surfaced under both actor types.
    /// </summary>
    public static GitHubActorKind Classify(string typeName, string login) => typeName switch
    {
        "Bot" => GitHubActorKind.Bot,
        "Mannequin" => GitHubActorKind.Mannequin,
        _ => IsCopilotLogin(login) ? GitHubActorKind.Bot : GitHubActorKind.Person,
    };
}

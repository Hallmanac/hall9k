using Hall9k.Connectors.Credentials;
using Hall9k.Connectors.Processes;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// The read behind a project's <c>tracker-assignee</c> claim gate (idea 64c75e43): every teammate
/// runs their own install against their own database, so the Jira card or the GitHub issue is the
/// only record the machines share — and this makes the tracker's own assignment field the single
/// act that hands out work. A task linked to a gated item is claimed on this install only while
/// the tracker shows that item assigned to this install's own tracker identity, so two teammates'
/// installs cannot both run the same card.
/// <para>
/// Every claim door goes through here rather than reading the tracker itself: the dispatcher's own
/// <c>DispatchEngine.TryClaimAsync</c>, <c>h9k task work</c>, <c>h9k task start</c>, and
/// <c>h9k task assign</c> (which warns and proceeds — the tracker stays the go signal, so the task
/// simply waits in the queue). One place, so the rule and its sentences cannot drift between them.
/// </para>
/// <para>
/// <b>Read-only, always.</b> Nothing here writes to the tracker, and there is no override flag.
/// Loopholes exist — a person can assign the card to themselves and walk away — and are accepted
/// (Brian's framing, 2026-09-06): the point is one shared go signal, not an enforcement boundary.
/// The only thing this reads beyond the assignee field is who this install is, so the one-time
/// content snapshot an adoption took is untouched (Decisions Log #60).
/// </para>
/// <para>
/// <b>Fails closed.</b> A tracker that cannot be read holds the claim rather than releasing it: a
/// gate whose whole purpose is to stop two installs running the same card must not let both
/// through the moment the shared record goes dark. The hold names the tracker's own error verbatim
/// and what ends it, and tells a credential refusal apart from an outage, because those have
/// opposite remedies.
/// </para>
/// </summary>
public sealed class TrackerClaimGate(
    ProcessRunner? processRunner = null, JiraRequester? requester = null, CredentialVault? vault = null)
{
    private readonly ProcessRunner processRunner = processRunner ?? ExternalProcess.Runner;

    /// <summary>
    /// Whether this task's reference is one the gate applies to at all. Only the two backlog item
    /// kinds are gated: a Jira card and a GitHub issue. A task with no reference, one whose
    /// publisher deliberately opted out of tracking (<c>h9k task publish --untracked</c>, which
    /// leaves no reference behind either), and a pr-review task — whose reference is a
    /// <see cref="WorkItemProvider.GitHubPullRequest"/> — all claim exactly as they did before this
    /// setting existed. The pull-request case is the deliberate one: an assignment on GitHub is
    /// already the go signal for that item kind through auto-pr-review (idea e5e98a33), whose own
    /// reviewer-request read is what created the task in the first place, and gating it a second
    /// time on an <em>assignee</em> field nobody sets on a pull request would hold every
    /// pr-review task forever.
    /// </summary>
    public static bool Gates(ClaimGate gate, ExternalReference? reference) =>
        gate == ClaimGate.TrackerAssignee
        && reference is { } item
        && (item.Provider == WorkItemProvider.Jira || item.Provider == WorkItemProvider.GitHub);

    /// <summary>
    /// One fresh read, at the moment of the check — never a cached answer, because the whole
    /// premise is that the tracker's current state is the authority and a stale copy is exactly
    /// what would let two installs both claim.
    /// </summary>
    /// <param name="store">
    /// The store, rather than a caller's session, because recording this install's own Jira
    /// identity on first use is a commit of its own: a check that ends in a refusal still observed
    /// the identity, and folding that append into a caller's transaction would lose it whenever
    /// the caller — the dispatcher's refused claim, above all — never saves.
    /// </param>
    /// <param name="gate">The project's setting. <see cref="ClaimGate.Off"/> answers <see cref="TrackerClaimDecision.NotGated"/> without a single call.</param>
    /// <param name="reference">The task's linked item, or null when it has none.</param>
    /// <param name="workingDirectory">Where <c>gh</c> runs — the project's own repository path, since gh reads the repository from the directory it runs in.</param>
    public async Task<TrackerClaimDecision> CheckAsync(
        IDocumentStore store,
        ClaimGate gate,
        ExternalReference? reference,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (!Gates(gate, reference))
        {
            return TrackerClaimDecision.NotGated;
        }

        ExternalReference item = reference!;
        return item.Provider == WorkItemProvider.Jira
            ? await CheckJiraAsync(store, item, cancellationToken)
            : await CheckGitHubAsync(item, workingDirectory, cancellationToken);
    }

    /// <summary>
    /// The hold a check that <em>threw</em> earns, for a caller that catches rather than letting
    /// an unforeseen failure out of its own loop. Every failure this gate foresees is already a
    /// classified <see cref="TrackerClaimVerdict.Unreadable"/> answer above — a tenant that
    /// refused the credentials, a <c>gh</c> that would not start, a key that does not parse — so
    /// what reaches here is the one that got past all of them: the credential store, the identity
    /// append, or a connector failing in a shape none of those paths turned into a decision.
    /// <para>
    /// It exists because holding is only half of failing closed. A hold nobody can see reads
    /// exactly like an idle queue, and this was the single path whose refusal published no
    /// <see cref="Domain.Features.Tasks.Documents.TrackerClaimHold"/> at all, so
    /// <c>h9k status</c> showed a task queued for no stated reason (Copilot review, PR #260).
    /// </para>
    /// <para>
    /// No item URL and no identity: naming either would mean having read it, and what happened
    /// here is precisely that the read did not finish — the never-guess rule applied to the two
    /// fields a throw leaves unobserved (AGENTS.md).
    /// </para>
    /// </summary>
    /// <param name="reference">The gated item the check was about — <see cref="Gates"/> has already answered for it.</param>
    /// <param name="failure">What was thrown. Its type is named alongside its message, because an unforeseen failure's message alone is often the least informative half.</param>
    /// <param name="observedAt">When this install tried, which is the only moment a failed read observed.</param>
    public static TrackerClaimDecision Threw(
        ExternalReference reference, Exception failure, DateTimeOffset observedAt) =>
        Unreadable(
            reference,
            reference.Provider == WorkItemProvider.Jira ? reference.Key : reference.Reference,
            itemUrl: null,
            $"the assignee read failed rather than answering — {failure.GetType().Name}: "
            + Text.RelayedText.OneLine(failure.Message),
            authenticationRefusal: false,
            TheDaemonLogHasTheRest,
            observedAt);

    /// <summary>
    /// Jira: the identity is the <c>accountId</c> recorded on the registered connection, which is
    /// the only thing Jira's own assignee field carries — never the email that authenticates and
    /// never the display name, both of which a tenant is free to change under us. A connection
    /// registered before that field existed carries none, so the first check that needs one reads
    /// it live from <c>/rest/api/2/myself</c> and records it, which is the same call
    /// <c>h9k connection add jira</c> already makes to prove the credentials.
    /// </summary>
    private async Task<TrackerClaimDecision> CheckJiraAsync(
        IDocumentStore store, ExternalReference reference, CancellationToken cancellationToken)
    {
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        string itemKey = reference.Key;

        ConnectionDetails? connection;
        JiraWorkItemProvider provider;
        try
        {
            await using IQuerySession query = store.QuerySession();
            connection = await WorkItemConnections.FindJiraConnectionAsync(query, cancellationToken);
            if (connection is null)
            {
                return Unreadable(
                    reference, itemKey, null, WorkItemConnections.NoJiraConnection,
                    authenticationRefusal: false, RestoreTheConnection, observedAt);
            }

            provider = new JiraWorkItemProvider(
                WorkItemConnections.Account(connection, vault), requester);
        }
        catch (DomainException exception)
        {
            // Two Jira connections with nothing saying which, or one with no site recorded. Both
            // are this install's own configuration rather than the tenant refusing anything, and
            // both are answered by the refusal WorkItemConnections already wrote for them.
            return Unreadable(
                reference, itemKey, null, exception.Message,
                authenticationRefusal: false, RestoreTheConnection, observedAt);
        }

        Uri? itemUrl = provider.WebUrl(reference);

        if (!JiraIssueKey.TryParseBareKey(itemKey, out JiraIssueKey key))
        {
            return Unreadable(
                reference, itemKey, itemUrl,
                $"'{Text.RelayedText.OneLine(reference.ToString())}' does not read as a Jira key, so there "
                + "is no card whose assignee could be read.",
                authenticationRefusal: false, RelinkTheItem, observedAt);
        }

        string? identity = connection.TrackerAccountId;
        if (identity.IsBlank())
        {
            // The same /myself call h9k connection add jira makes, read through the classifying
            // variant so a tenant that merely failed to answer is not reported as one that refused
            // the credentials (self-review, round one: this used to assert a credential refusal on
            // every failure, including a timeout, and would have sent an operator to renew a token
            // that was working).
            JiraSelfRead self = await provider.ReadSelfAsync(cancellationToken);
            if (self.Self is not { } account)
            {
                return Unreadable(
                    reference, itemKey, itemUrl, self.Failure!.Message,
                    self.FailureKind == TrackerReadFailure.Credentials,
                    Lever(self.FailureKind, RenewJiraToken(connection)), observedAt);
            }

            identity = account.AccountId;
            await RecordJiraIdentityAsync(store, connection.Id, identity, observedAt, cancellationToken);
        }

        TrackerAssigneeRead read = await provider.ReadAssigneeAsync(key, cancellationToken);
        return Conclude(
            reference, itemKey, itemUrl, identity, read, observedAt,
            unreadableLever: Lever(read.Failure, RenewJiraToken(connection)));
    }

    /// <summary>
    /// GitHub: the identity is the login <c>gh</c> is authenticated as, read live on every check
    /// and deliberately never recorded — exactly what
    /// <see cref="GitHubReviewAssignments.CurrentLoginAsync"/> already does for auto-pr-review, and
    /// for the same reason: a stored login would silently stop matching (or start matching someone
    /// else's assignments) the moment this machine's <c>gh auth</c> session changed, and a gate
    /// that goes quiet that way looks exactly like an idle queue.
    /// <para>
    /// An issue may carry several assignees, and being among them passes — a shared card is a real
    /// thing on GitHub, and refusing the whole set because somebody else is also on it would hold
    /// work nobody meant to hold.
    /// </para>
    /// </summary>
    private async Task<TrackerClaimDecision> CheckGitHubAsync(
        ExternalReference reference, string workingDirectory, CancellationToken cancellationToken)
    {
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        string itemKey = reference.Reference;
        GitHubWorkItemProvider issues = new(processRunner);
        Uri? itemUrl = issues.WebUrl(reference);

        GitHubLoginRead login = await new GitHubReviewAssignments(processRunner)
            .ReadCurrentLoginAsync(workingDirectory, cancellationToken);
        if (login.Login is not { } identity)
        {
            return Unreadable(
                reference, itemKey, itemUrl, login.Error ?? "gh did not say which login it is authenticated as.",
                login.AuthenticationRefusal,
                login.AuthenticationRefusal ? SignInToGitHub : RestoreTheGitHubTool, observedAt);
        }

        TrackerAssigneeRead read = await issues.ReadAssigneesAsync(reference, workingDirectory, cancellationToken);
        return Conclude(
            reference, itemKey, itemUrl, identity, read, observedAt,
            unreadableLever: Lever(read.Failure, SignInToGitHub));
    }

    /// <summary>
    /// The one place the three outcomes are decided, shared by both providers so neither can
    /// disagree with the other about what "assigned to me" means.
    /// </summary>
    private static TrackerClaimDecision Conclude(
        ExternalReference reference,
        string itemKey,
        Uri? itemUrl,
        string identity,
        TrackerAssigneeRead read,
        DateTimeOffset observedAt,
        string unreadableLever)
    {
        if (read.Failed)
        {
            return Unreadable(
                reference, itemKey, itemUrl, read.Error!, read.AuthenticationRefusal, unreadableLever,
                observedAt, identity);
        }

        if (read.Assignees.FirstOrDefault(assignee =>
            string.Equals(assignee.Identity, identity, read.IdentityComparison)) is { } match)
        {
            return new TrackerClaimDecision(
                TrackerClaimVerdict.Assigned, reference.Provider.Value, itemKey, itemUrl, identity,
                match.Described, null, false, null, observedAt, match);
        }

        return new TrackerClaimDecision(
            read.Assignees.Count == 0 ? TrackerClaimVerdict.Unassigned : TrackerClaimVerdict.HeldByOther,
            reference.Provider.Value,
            itemKey,
            itemUrl,
            identity,
            read.Assignees.Count == 0 ? null : read.DescribeHolders(),
            null,
            false,
            AssignYourself(itemKey, itemUrl),
            observedAt);
    }

    private static TrackerClaimDecision Unreadable(
        ExternalReference reference,
        string itemKey,
        Uri? itemUrl,
        string error,
        bool authenticationRefusal,
        string lever,
        DateTimeOffset observedAt,
        string? identity = null) =>
        new(TrackerClaimVerdict.Unreadable, reference.Provider.Value, itemKey, itemUrl, identity, null,
            error, authenticationRefusal, lever, observedAt);

    /// <summary>
    /// Record what the tracker just said this install is, on the connection's own stream, so the
    /// next check compares against an observation rather than making the same call again. Its own
    /// session and its own commit, per <see cref="CheckAsync"/>'s <c>store</c> parameter. Racing
    /// another door that read the same identity in the same instant is harmless — the value is
    /// identical, the stream simply carries two identical observations, and nothing here needs an
    /// expected version to be right.
    /// </summary>
    private static async Task RecordJiraIdentityAsync(
        IDocumentStore store,
        Guid connectionId,
        string trackerAccountId,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(
            connectionId, new ConnectionTrackerIdentityObserved(connectionId, trackerAccountId, observedAt));
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// What ends a fails-closed hold, chosen from what actually failed rather than from a single
    /// guess (self-review, round one): a definitive answer about the item — a key that does not
    /// resolve, or a project this account cannot see — will read the same way on every retry, so
    /// telling the operator to wait it out would name a wait that never ends, and telling them to
    /// renew a token nothing refused would send them somewhere the answer is not.
    /// <paramref name="credentialsLever"/> is the provider's own way of saying "authenticate
    /// again", since the two trackers are re-authenticated by different commands.
    /// </summary>
    private static string Lever(TrackerReadFailure failure, string credentialsLever) => failure switch
    {
        TrackerReadFailure.Credentials => credentialsLever,
        TrackerReadFailure.Item => TheTrackersOwnAnswer,
        _ => WaitOutTheOutage,
    };

    /// <summary>
    /// The lever a tracker that simply named somebody else earns. The key is the task's own
    /// recorded reference on its way into a terminal, so it is sanitised here the same way
    /// <see cref="TrackerClaimDecision"/> sanitises it in its own sentences — and, as there, only
    /// at the point of display: what the gate parsed and compared is the raw value.
    /// </summary>
    private static string AssignYourself(string itemKey, Uri? itemUrl)
    {
        string item = TrackerAssignee.Rendered(itemKey);
        return itemUrl is null
            ? $"Assign {item} to yourself in the tracker and the claim proceeds on its own."
            : $"Assign {item} to yourself in the tracker and the claim proceeds on its own: {itemUrl}";
    }

    private static string RenewJiraToken(ConnectionDetails connection) =>
        "The hold ends when the credentials work again — renew the token and register the connection "
        + $"again: h9k connection add jira --site {connection.SiteUrl?.GetLeftPart(UriPartial.Authority) ?? "https://your-org.atlassian.net"} "
        + $"--email {connection.ExternalAccountId}";

    private const string RelinkTheItem =
        "Nothing on this install can end the hold — the reference recorded on this task is not one "
        + "this platform can read as a key, so relink it: h9k task link-jira <id> <KEY> (or "
        + "h9k task link-issue for a GitHub issue).";

    private const string RestoreTheConnection =
        "The hold ends when this install can reach the tracker again — restore the connection "
        + "(h9k connection list to see what is registered, h9k connection add jira to fix it).";

    private const string SignInToGitHub =
        "The hold ends when gh is authenticated again — run 'gh auth login' (Hall9k holds no GitHub "
        + "token of its own, it uses yours).";

    private const string RestoreTheGitHubTool =
        "The hold ends when gh can answer again — check it is installed and the project's repository "
        + "path exists ('gh auth status' from that directory says what it is waiting on).";

    /// <summary>
    /// The lever for a tracker that answered rather than failed. It deliberately restates nothing
    /// and defers to the error it sits beside (self-review, round two): the same classification
    /// covers a key that does not resolve, a project this account cannot see, and a site URL
    /// pointing at a portal rather than the tenant — and every one of those errors already names
    /// its own specifics, so a lever that guessed at them would be wrong for two cases out of
    /// three.
    /// </summary>
    private const string TheTrackersOwnAnswer =
        "The tracker answered rather than failing, and it will answer the same way on every retry, so "
        + "nothing on this install ends the hold — its own words above are what says where to fix it.";

    private const string WaitOutTheOutage =
        "Nothing on this install can end the hold — wait out the outage; the next sweep reads again "
        + "on its own.";

    /// <summary>
    /// The lever for <see cref="Threw"/>, which cannot name a remedy honestly: an unclassified
    /// failure could be a hiccup that clears on the next sweep or a defect that never does, and
    /// the only thing that tells them apart is the stack trace — which the daemon logged and a
    /// one-line status row cannot carry. So it points at where the rest of the answer is rather
    /// than guessing at which of the two this was (AGENTS.md, never guess at unobserved facts).
    /// </summary>
    private const string TheDaemonLogHasTheRest =
        "This is not a failure the gate classifies, so it cannot say what ends the hold — the "
        + "daemon log's own warning for this task carries the full failure; the next sweep reads "
        + "again on its own either way.";
}

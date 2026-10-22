using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// Why a Jira write did not carry out, apart from what it actually said — the split
/// <see cref="JiraWriteExecutor"/> and the daemon's retry sweep both need: a rejected credential
/// is a handled, expected state (an API token that was rotated or revoked), and everything else is
/// a write that needs a different payload, not a different day. An in-process outcome, never
/// persisted (AGENTS.md, coding standards) — what a caller records is
/// <see cref="JiraWriteExecutionException.IsAuthFailure"/> on
/// <see cref="Domain.Features.Tasks.Events.JiraWriteFailed"/>, this enum's own reason text.
/// </summary>
public enum JiraWriteFailureKind
{
    AuthFailure,
    Other,
}

/// <summary>The one exception every <see cref="JiraWriteExecutor"/> call can throw, classified so a caller can tell an expected state from a real refusal.</summary>
public sealed class JiraWriteExecutionException(
    JiraWriteFailureKind kind, string message, bool writeAlreadyRan = false) : Exception(message)
{
    public JiraWriteFailureKind Kind { get; } = kind;

    public bool IsAuthFailure => Kind == JiraWriteFailureKind.AuthFailure;

    /// <summary>
    /// Whether Jira's own create, update or comment call had already answered 2xx when this was
    /// thrown — so what failed is the read-back that verifies the write, not the write. The
    /// distinction is stated in the message already, but a caller deciding what to <em>record</em>
    /// cannot read a sentence: <c>h9k task assign --take</c> would otherwise report "the item could
    /// not be assigned" about a write that landed, and refuse an assignment its own assignee
    /// read-back would very likely have confirmed (independent pre-PR review, cycle 1, adversarial
    /// lens). False by default, which is every failure of a write itself.
    /// </summary>
    public bool WriteAlreadyRan { get; } = writeAlreadyRan;
}

/// <summary>What a create, an update, or a comment came back with once Jira's own answer was read back and verified.</summary>
public sealed record JiraWriteResult(string IssueKey, string Summary);

/// <summary>The doctor's own reading of whether a write to Jira would go through right now.</summary>
public enum JiraAuthProbeResult
{
    Authenticated,
    AuthFailure,
    Unknown,
}

/// <summary>
/// hall9k's sole path to writing Jira (Brian's design, 2026-08-28, superseding the
/// agent-mediated-only ruling; the executor's own transport moved off the Atlassian CLI (twg) onto
/// this REST client, Decisions Log #114, once a b6dfcbe5-shaped retrospective totted up 55 review
/// cycles spent regex-parsing twg's own text envelope for facts a structured HTTP response already
/// carries): every create, update, and comment goes through here, never through an agent's own
/// Jira access. Composition — the issue type, the fields, the comment text — stays an agent's or an
/// operator's judgment; this class is the deterministic, audited half. It refuses nothing about a
/// payload itself (that is <see cref="JiraWritePayload.Validate"/>'s job, checked before this class
/// is ever called) and models nothing about a card's shape — it only calls Jira, reads its own JSON
/// answer back, and classifies a failure as a rejected credential or a real refusal.
/// <para>
/// Built on the same <see cref="JiraAccount"/>/<see cref="JiraRequester"/> seam
/// <see cref="JiraWorkItemProvider"/> already reads through, and the same API version, 2 — v2
/// carries a description or a comment body as a plain string, which is what a composed payload
/// already is, so no ADF (v3's rich-text tree) ever has to be built or parsed on the way in. The
/// registered connection's credential now covers both directions: reading a card and writing one
/// use the identical Basic-auth token, so an install no longer needs a second, separate,
/// browser-based twg login just for writes.
/// </para>
/// <para>
/// One deliberate exception to "everything is v2": the duplicate-card search
/// (<see cref="FindCreatedCardsAsync"/>) calls the newer <c>/rest/api/3/search/jql</c> endpoint rather
/// than the classic <c>/rest/api/2/search</c>/<c>/rest/api/3/search</c> GET/POST pair, which
/// Atlassian has been retiring in favor of it — there is no v2 equivalent of the replacement.
/// Nothing about that endpoint touches rich text (it returns only a candidate's key and its
/// plain-text summary), so this does not reopen the v2-for-text rationale above; it is a plain JQL
/// lookup that happens to live under a different version prefix. None of this could be verified
/// against a live Jira Cloud tenant from this build environment (no network access here): the
/// exact request/response shape, the <c>creator = currentUser()</c> and relative
/// <c>created &gt;= -Nm</c> terms, and <c>summary</c> being returned in the search answer are this
/// class's own best reading of Atlassian's published guidance rather than observed facts — flagged
/// here per AGENTS.md's "never guess at unobserved facts" so whoever first runs this against a real
/// tenant knows exactly which corners to watch.
/// </para>
/// </summary>
public sealed class JiraWriteExecutor(JiraAccount account, JiraRequester? requester = null, TimeProvider? clock = null)
{
    /// <summary>
    /// How far before a task's first create request the duplicate search starts looking: room for
    /// this machine's clock and Jira's to disagree, and for a create that reached Jira a little
    /// before hall9k recorded the request it belongs to.
    /// </summary>
    public static readonly TimeSpan CreateWindowSkew = TimeSpan.FromMinutes(5);

    /// <summary>How many cards the duplicate search asks Jira for in one page.</summary>
    public const int SearchPageSize = 100;

    /// <summary>
    /// How many of the login's own cards on the board the duplicate search reads in the window,
    /// across as many pages as it takes, before it refuses. The window is anchored on a task's
    /// first create request and only grows, so a busy login on a long-lived task would otherwise
    /// be refused for good by the first page alone.
    /// </summary>
    public const int MaxWindowCards = 1000;

    private readonly JiraRequester requester = requester ?? JiraHttp.Requester;
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    /// <summary>
    /// The physical half of the create dedup gate, called before every create (the first attempt and
    /// every later one alike): the keys of the cards on <paramref name="board"/> that this
    /// connection's own login created no earlier than <see cref="CreateWindowSkew"/> before
    /// <paramref name="firstCreateRequestedAt"/> and whose summary equals
    /// <paramref name="summary"/> exactly, earliest first. A create Jira completed but hall9k never
    /// recorded is therefore found rather than duplicated, with nothing written to the card to make
    /// it findable. The window starts at the task's first create request, not at the attempt in
    /// hand, because every submit mints a fresh request time and the card an earlier attempt made
    /// predates it.
    /// <para>
    /// The match is on <c>creator</c>, which is always the login that made the card, rather than
    /// <c>reporter</c>, which a composed payload can set and a person can change. The time bound is
    /// relative (<c>created &gt;= -Nm</c>) because a JQL date literal is read in the searching
    /// profile's timezone, so a formatted timestamp would land hours off for anyone whose profile is
    /// not UTC. Summary is read from the answer rather than searched on: JQL's <c>~</c> is a Lucene
    /// text match, not an equality check, so a query on it could miss a card the exact comparison
    /// here would find. The window is read page by page (<see cref="SearchPageSize"/> at a time,
    /// following Jira's <c>nextPageToken</c>) so how long a task has sat does not by itself make
    /// the window unreadable.
    /// </para>
    /// <para>
    /// An answer or a candidate entry that cannot be read, a page that promises another without a
    /// way to fetch it, and a window holding more than <see cref="MaxWindowCards"/> cards, refuse
    /// rather than return nothing: an unconfirmable check is not a negative one.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<string>> FindCreatedCardsAsync(
        JiraProjectKey board, string summary, DateTimeOffset firstCreateRequestedAt, CancellationToken cancellationToken)
    {
        DateTimeOffset lookFrom = firstCreateRequestedAt - CreateWindowSkew;
        long minutes = Math.Max(1, (long)Math.Ceiling((clock.GetUtcNow() - lookFrom).TotalMinutes));

        string authorization = await AuthorizeAsync(
            $"search {board} for a card already created for this task at {account.Site}", cancellationToken);
        string jql = $"project = \"{board.Value}\" AND creator = currentUser() AND created >= -{minutes}m ORDER BY created ASC";

        List<WindowCard> window = [];
        string? pageToken = null;
        do
        {
            JsonObject body = new()
            {
                ["jql"] = jql,
                ["maxResults"] = SearchPageSize,
                ["fields"] = new JsonArray("summary"),
            };
            if (pageToken is not null)
            {
                body["nextPageToken"] = pageToken;
            }

            JiraResponse response = await SendAsync(
                new JiraRequest(HttpMethod.Post, account.Endpoint("/rest/api/3/search/jql"), authorization, body.ToJsonString()),
                $"search {board} for a card already created for this task",
                cancellationToken);

            WindowRead page = ReadWindow(response.Body);
            if (page.Cards is null || (page.HasMore && page.NextPageToken is null))
            {
                throw RefuseUnconfirmed(
                    board, summary, lookFrom,
                    "Jira's answer to the search came back as something other than the expected "
                    + "{\"issues\": [{\"key\": ..., \"fields\": {\"summary\": ...}}]} shape (not JSON, not an "
                    + "object, an entry missing its key or summary, or another page promised with no way to "
                    + "fetch it).",
                    "if it is not, run the write again");
            }

            window.AddRange(page.Cards);
            if (window.Count > MaxWindowCards || (page.HasMore && window.Count == MaxWindowCards))
            {
                throw RefuseUnconfirmed(
                    board, summary, lookFrom,
                    $"The window holds more than {MaxWindowCards} of this login's cards on the board, more than "
                    + "this search reads, so a card for this task could be sitting past the cut.",
                    "if it is not, create the card in Jira directly and link that, since a create from here "
                    + "stays refused while the window is this full");
            }

            pageToken = page.NextPageToken;
        }
        while (pageToken is not null);

        return
        [
            .. window
                .Where(card => string.Equals(card.Summary, summary, StringComparison.Ordinal))
                .Select(card => card.Key),
        ];
    }

    private static JiraWriteExecutionException RefuseUnconfirmed(
        JiraProjectKey board, string summary, DateTimeOffset lookFrom, string why, string ifNotThere) =>
        new(
            JiraWriteFailureKind.Other,
            $"{why} Refusing to create on an unconfirmed duplicate check. Look on {board} for a card this "
            + $"connection's login created since {lookFrom.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC titled "
            + $"\"{Bounded(summary)}\"; if it is there, link it to this task with h9k task link-jira, and "
            + $"{ifNotThere}.");

    /// <summary>
    /// What <see cref="FindCreatedCardsAsync"/> reads out of one search answer: <c>Cards</c> is null
    /// when the answer cannot be confirmed as the expected shape (never merely when it holds no
    /// cards, which <c>{"issues": []}</c> genuinely proves), <c>HasMore</c> is true when Jira
    /// says there is another page, whatever the page in hand held, and <c>NextPageToken</c> is the
    /// token that fetches it, when Jira gave one.
    /// </summary>
    private sealed record WindowRead(IReadOnlyList<WindowCard>? Cards, bool HasMore, string? NextPageToken);

    private sealed record WindowCard(string Key, string Summary);

    private static WindowRead ReadWindow(string body)
    {
        WindowRead unreadable = new(null, false, null);
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("issues", out JsonElement issues)
                || issues.ValueKind != JsonValueKind.Array)
            {
                return unreadable;
            }

            List<WindowCard> cards = [];
            foreach (JsonElement item in issues.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("key", out JsonElement key)
                    || key.ValueKind != JsonValueKind.String
                    || key.GetString() is not { } keyText
                    || !JiraIssueKey.TryParseBareKey(keyText, out JiraIssueKey parsedKey)
                    || !item.TryGetProperty("fields", out JsonElement fields)
                    || fields.ValueKind != JsonValueKind.Object
                    || !fields.TryGetProperty("summary", out JsonElement summary)
                    || summary.ValueKind != JsonValueKind.String
                    || summary.GetString() is not { } summaryText)
                {
                    return unreadable;
                }

                cards.Add(new WindowCard(parsedKey.Value, summaryText));
            }

            string? nextPageToken = root.TryGetProperty("nextPageToken", out JsonElement token)
                && token.ValueKind == JsonValueKind.String && token.GetString().IsNotBlank()
                    ? token.GetString()
                    : null;
            bool hasMore = nextPageToken is not null
                || (root.TryGetProperty("isLast", out JsonElement isLast) && isLast.ValueKind == JsonValueKind.False);
            return new WindowRead(cards, hasMore, nextPageToken);
        }
        catch (JsonException)
        {
            return unreadable;
        }
    }

    /// <summary>
    /// One card, authored from a composed payload, then read back and verified — never trusted on
    /// Jira's own create response alone, the same discipline
    /// <see cref="GitHubWorkItemProvider.CreateAsync"/> applies to <c>gh issue create</c>.
    /// </summary>
    public async Task<JiraWriteResult> CreateAsync(
        JiraProjectKey project, JiraWritePayload payload, CancellationToken cancellationToken)
    {
        if (!project.HasValue)
        {
            throw new JiraWriteExecutionException(
                JiraWriteFailureKind.Other,
                "No Jira board is bound and the payload named none either. Compose the payload with "
                + "\"projectKey\", or bind one with h9k project set --jira.");
        }

        Dictionary<string, string> fields = payload.Fields is null
            ? []
            : new Dictionary<string, string>(payload.Fields);
        string? summary = ExtractField(fields, "summary");
        string description = ApplyFormat(ExtractField(fields, "description"), payload.EffectiveFormat) ?? string.Empty;

        // Discarded case-insensitively, the same way ExtractField above pulls "summary" and
        // "description" regardless of a composer's own casing choice: a card is always filed
        // against the board hall9k resolved and the work item type it validated (independent
        // pre-PR review, adversarial lens, cycle 1), never a project or issuetype a composer
        // smuggled into "fields" alongside them — and an exact-case-only guard left "issueType"
        // (as opposed to "issuetype") free to reach Jira alongside the reserved node below,
        // which Jira refuses outright as a duplicate field rather than silently overriding
        // (independent pre-PR review, adversarial lens, cycle 10).
        ExtractField(fields, "project");
        ExtractField(fields, "issuetype");

        JsonObject fieldsNode = [];
        AppendFields(fieldsNode, fields);
        fieldsNode["project"] = new JsonObject { ["key"] = project.Value };
        fieldsNode["issuetype"] = new JsonObject { ["name"] = payload.WorkItemType ?? string.Empty };
        fieldsNode["description"] = description;
        if (summary.IsNotBlank())
        {
            fieldsNode["summary"] = summary;
        }

        string authorization = await AuthorizeAsync($"create a card at {account.Site}", cancellationToken);
        JiraResponse response = await SendWriteAsync(
            new JiraRequest(HttpMethod.Post, account.Endpoint("/rest/api/2/issue"), authorization,
                new JsonObject { ["fields"] = fieldsNode }.ToJsonString()),
            "create the card",
            cancellationToken);

        string key = ExtractKey(response.Body)
            ?? throw new JiraWriteExecutionException(
                JiraWriteFailureKind.Other,
                "Jira reported success creating the card but the response carried no key, so nothing "
                + $"here can be verified: {Head(response.Body)}");

        return await VerifyAsync(key, "created", confirmsExistenceOnly: false, writeAlreadyRan: true, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// An existing card's fields, updated and then read back to confirm it is still there. The
    /// read-back this executor can run — a search on the key — only ever proves the card exists,
    /// which is already true before an update runs, so the recorded outcome says exactly that
    /// rather than claiming the changed fields themselves were confirmed: the update's own 2xx
    /// response is what this is actually trusted on. A composed <c>project</c> or <c>issuetype</c>
    /// field is refused before the request is built, never sent: an update never moves a card to a
    /// different board or work item type through a field write.
    /// </summary>
    public async Task<JiraWriteResult> UpdateAsync(string issueKey, JiraWritePayload payload, CancellationToken cancellationToken)
    {
        issueKey = ValidateIssueKey(issueKey, "update");

        Dictionary<string, string> fields = payload.Fields is null
            ? []
            : new Dictionary<string, string>(payload.Fields);
        string? summary = ExtractField(fields, "summary");
        string? description = ExtractField(fields, "description");

        // JiraWritePayload.Validate's own RefuseReassignmentField already refuses a fresh payload
        // naming "project" or "issuetype" here, before it is ever recorded — but
        // JiraWriteCoordinator.RetryPendingAsync replays an already-recorded payload's own
        // PayloadJson without re-validating it (by design — a retry mints no new intent), so a
        // write recorded pending under an older build that still allowed such a field (and actually
        // sent it, the way the retired twg transport's own AppendFields did) would otherwise reach
        // here under the newer build's rules. Silently discarding it at this point — the way this
        // used to work — would let the read-back confirm the card still exists and record a
        // verified JiraWriteSucceeded for content that was never sent, and if the field was the
        // payload's only content, Jira would receive an effectively empty update and the audit
        // trail would record a no-op as a completed write. Refused here instead, the same
        // terminal-failure shape ApplyFormat uses for a stale "html" payload below (independent
        // pre-PR review, adversarial lens, cycle 13).
        RefuseReplayedReassignmentField(ExtractField(fields, "project"), "project");
        RefuseReplayedReassignmentField(ExtractField(fields, "issuetype"), "issuetype");

        JsonObject fieldsNode = [];
        if (summary.IsNotBlank())
        {
            fieldsNode["summary"] = summary;
        }

        if (description.IsNotBlank())
        {
            fieldsNode["description"] = ApplyFormat(description, payload.EffectiveFormat);
        }

        AppendFields(fieldsNode, fields);

        string authorization = await AuthorizeAsync($"update {issueKey} at {account.Site}", cancellationToken);
        await SendWriteAsync(
            new JiraRequest(HttpMethod.Put, account.Endpoint($"/rest/api/2/issue/{issueKey}"), authorization,
                new JsonObject { ["fields"] = fieldsNode }.ToJsonString()),
            $"update {issueKey}",
            cancellationToken);

        return await VerifyAsync(issueKey, "updated", confirmsExistenceOnly: true, writeAlreadyRan: true, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// A direct-by-key read-back with no write attempted first — for a caller that already believes
    /// a card exists (a stuck create's own retry, finding the task linked to one some other route
    /// already recorded, such as an operator's own <c>h9k task link-jira</c>) and owes its own
    /// recorded outcome the identical "prove it, do not infer it" confirmation every write's success
    /// gets here, rather than recording that belief unread.
    /// </summary>
    public Task<JiraWriteResult> VerifyExistsAsync(string issueKey, CancellationToken cancellationToken) =>
        VerifyAsync(ValidateIssueKey(issueKey, "link"), "linked", confirmsExistenceOnly: true, cancellationToken: cancellationToken);

    /// <summary>
    /// A comment on an existing card — never a transition, never a close, exactly the closeout
    /// write this surface exists to carry. Read back the same way <see cref="UpdateAsync"/> is:
    /// existence only, not the comment text itself.
    /// <para>
    /// Unlike a create, a comment has no dedup gate: nothing here can list or search a card's own
    /// comments, so nothing here can tell an earlier attempt's comment apart from a fresh one before
    /// posting. A rejected credential from the write call itself is safe to report as the ordinary
    /// pending-authentication state — nothing happened yet, so the retry sweep's next attempt starts
    /// clean — but a rejected credential from the read-back below is not: the comment has already
    /// landed by that point, and reporting it the same way would leave the pending write standing
    /// for the retry sweep to post the identical comment a second time once the connection is fixed.
    /// Reclassified here as an ordinary (non-auth) failure instead, which clears the pending marker
    /// rather than retrying automatically, and says so plainly: an operator has to look at the board
    /// before deciding whether this needs resubmitting.
    /// </para>
    /// </summary>
    public async Task<JiraWriteResult> CommentAsync(
        string issueKey, string comment, string format, CancellationToken cancellationToken)
    {
        issueKey = ValidateIssueKey(issueKey, "comment on");

        string authorization = await AuthorizeAsync($"comment on {issueKey} at {account.Site}", cancellationToken);
        await SendWriteAsync(
            new JiraRequest(HttpMethod.Post, account.Endpoint($"/rest/api/2/issue/{issueKey}/comment"), authorization,
                new JsonObject { ["body"] = ApplyFormat(comment, format) }.ToJsonString()),
            $"comment on {issueKey}",
            cancellationToken);

        try
        {
            return await VerifyAsync(issueKey, "commented on", confirmsExistenceOnly: true, writeAlreadyRan: true, cancellationToken: cancellationToken);
        }
        catch (JiraWriteExecutionException exception) when (exception.Kind == JiraWriteFailureKind.AuthFailure)
        {
            throw new JiraWriteExecutionException(
                JiraWriteFailureKind.Other,
                $"Jira posted the comment on {issueKey}, but reading it back to verify hit a rejected "
                + "credential rather than any other failure. This is not reported as the ordinary "
                + "pending-authentication state, because the comment already landed: retrying it once "
                + "the connection is fixed would post the identical comment a second time, since a "
                + "comment gives a later attempt nothing to search for that would tell it apart from a "
                + $"fresh one. Check {issueKey} before resubmitting.");
        }
    }

    /// <summary>
    /// Jira's own claim, read back through a fresh, direct-by-key read rather than trusted — the
    /// verified read-back every acceptance criterion for this feature names explicitly. Meaningful
    /// proof for a create, whose whole claim is that the card now exists; for an update or a comment
    /// the identical read can only re-confirm a fact already true before the write ran, so
    /// <paramref name="confirmsExistenceOnly"/> keeps the recorded summary honest about which of the
    /// two it actually is.
    /// </summary>
    private async Task<JiraWriteResult> VerifyAsync(
        string issueKey, string verb, bool confirmsExistenceOnly, bool writeAlreadyRan = false, CancellationToken cancellationToken = default)
    {
        JiraResponse response;
        try
        {
            string authorization = await AuthorizeAsync($"read {issueKey} back at {account.Site}", cancellationToken);
            response = await SendAsync(
                new JiraRequest(HttpMethod.Get, account.Endpoint($"/rest/api/2/issue/{issueKey}?fields=key"), authorization),
                $"read {issueKey} back",
                cancellationToken);
        }
        // A failure of this read-back call is not a failure of the write it is verifying — Jira's
        // own create/update/comment call already ran and answered 2xx by the time this runs, so a
        // transient 5xx, a rate limit, or a read-permission problem hitting this read does not mean
        // the write was refused, only that this could not confirm it. AuthFailure is excluded here:
        // CommentAsync's own catch already reclassifies that case for the one operation (a comment)
        // where retrying automatically would risk a duplicate; Create and Update stay accurately
        // AuthFailure so they keep retrying automatically, which is safe for both (a create's own
        // duplicate-card search, an update's own idempotent re-apply) — and so an excluded AuthFailure
        // carries no JiraWriteExecutionException.WriteAlreadyRan marker either, which is correct
        // for the retrying callers and, for a caller that reads the marker instead, means only
        // that a credential this executor could not read the card back with is one nothing else
        // could confirm it with either.
        catch (JiraWriteExecutionException exception) when (writeAlreadyRan && exception.Kind != JiraWriteFailureKind.AuthFailure)
        {
            string detail = exception.Message.TrimEnd();
            string separator = detail.Length > 0 && detail[^1] is '.' or '!' or '?' ? " " : ". ";
            throw new JiraWriteExecutionException(
                JiraWriteFailureKind.Other,
                $"Jira reported {issueKey} {verb}, but reading it back afterward to verify hit its own "
                + $"failure: {detail}{separator}That describes only the read-back call — the {verb} call "
                + "itself already succeeded, so do not record this as a refusal of the write. "
                + (confirmsExistenceOnly
                    ? "Check the board before writing again."
                    : "Before creating, this executor searches the board for a card this login already "
                        + "created for the task, with the same summary, and adopts it rather than filing a "
                        + "second one, but Jira's own search index updates asynchronously, so a "
                        + "resubmission inside that lag window can still find nothing and file a second "
                        + "card — check the board before resubmitting if you can."),
                writeAlreadyRan: true);
        }

        string? found = ExtractKey(response.Body);
        if (found is null)
        {
            throw new JiraWriteExecutionException(
                JiraWriteFailureKind.Other,
                $"Jira reported {issueKey} {verb}, but reading it back to verify found nothing. The "
                + "write was not recorded — check the board before writing again.",
                writeAlreadyRan);
        }

        return new JiraWriteResult(
            found,
            confirmsExistenceOnly
                ? $"Jira reported {found} {verb}. The read-back confirms the card still exists; it does "
                    + "not re-read the changed field or comment content, so that part is trusted to the "
                    + "write's own successful response."
                : $"Jira reported {found} {verb} and it read back successfully.");
    }

    /// <summary>
    /// A read-only probe for <c>h9k doctor</c>: does an authenticated Jira call go through right
    /// now, with no card touched either way. Distinguishes a rejected credential from anything else
    /// so the fix taught is the right one.
    /// <para>
    /// Calls <c>GET /rest/api/2/myself</c> — the identical endpoint
    /// <see cref="JiraWorkItemProvider.VerifyAccessAsync"/> already exercises and shape-checks at
    /// connection registration — rather than the newer <c>/rest/api/3/search/jql</c> endpoint an
    /// earlier cycle of this probe used, whose exact request/response shape was this class's own
    /// unverified reading of Atlassian's migration guidance (no live tenant to check it against
    /// from this build environment). <c>/myself</c> answers exactly the question this probe asks —
    /// is the registered credential authenticated — and is already observed working, so this
    /// carries no such guess (independent pre-PR review, adversarial lens, cycle 5). It now also
    /// adopts <see cref="VerifyAccessAsync"/>'s own shape check on the answer — a 2xx alone is not
    /// proof, since an identity-aware proxy or an SSO portal in front of the tenant can answer the
    /// unauthenticated request with its own 200 and a login page or an unrelated JSON body, which
    /// without this check reported the green "authenticated" line for a credential that could not
    /// actually write anything (independent pre-PR review, both lenses, cycle 8).
    /// </para>
    /// <para>
    /// Resolves the credential through <see cref="JiraAccount.AuthorizationAsync"/> directly,
    /// deliberately not through <see cref="AuthorizeAsync"/>'s shared wrapper: a
    /// <see cref="DomainException"/> here (an unset environment variable, a deleted credential
    /// file) is left to propagate to <c>JiraDoctor</c>'s own catch, which reports the vault's exact
    /// reason, rather than being folded into <see cref="JiraAuthProbeResult.AuthFailure"/> — a state
    /// this probe reserves for a credential Jira itself examined and rejected (independent pre-PR
    /// review, verify pass, cycle 2: folding both into one enum value made the doctor tell an
    /// operator "Jira rejected the registered credentials" and point at token rotation for a
    /// credential Jira was never even asked about).
    /// </para>
    /// </summary>
    public async Task<JiraAuthProbeResult> ProbeAuthenticationAsync(CancellationToken cancellationToken)
    {
        string authorization = await account.AuthorizationAsync($"sign in to {account.Site}", cancellationToken);
        try
        {
            JiraResponse response = await SendAsync(
                new JiraRequest(HttpMethod.Get, account.Endpoint("/rest/api/2/myself"), authorization),
                "probe authentication",
                cancellationToken);
            return IsSignedInUserDocument(response.Body) ? JiraAuthProbeResult.Authenticated : JiraAuthProbeResult.Unknown;
        }
        catch (JiraWriteExecutionException exception) when (exception.Kind == JiraWriteFailureKind.AuthFailure)
        {
            return JiraAuthProbeResult.AuthFailure;
        }
        catch (JiraWriteExecutionException)
        {
            return JiraAuthProbeResult.Unknown;
        }
    }

    /// <summary>
    /// The same shape check <see cref="JiraWorkItemProvider.VerifyAccessAsync"/> applies to the
    /// identical endpoint: a 2xx alone is not proof of a signed-in credential, since a proxy or an
    /// SSO portal in front of the tenant can answer 200 with a login page or an unrelated JSON body.
    /// <c>accountId</c> is the field that cannot be a coincidence of some other document's shape —
    /// Jira answers <c>/myself</c> with it for every account.
    /// </summary>
    private static bool IsSignedInUserDocument(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("accountId", out JsonElement accountId)
                && accountId.ValueKind == JsonValueKind.String;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The token read for every call, wrapped so a credential the vault cannot resolve — the
    /// environment variable not exported, the stored file removed, a keychain reference on a
    /// machine with no keychain — is reported the same way a rejected credential from Jira itself
    /// is: an <see cref="JiraWriteFailureKind.AuthFailure"/> the coordinator already records as a
    /// pending, automatically-retried write, rather than a raw <see cref="DomainException"/>
    /// escaping this connector as a terminal failure (independent pre-PR review, both lenses,
    /// cycle 1: closeout's own merge comment was being dropped, not retried, whenever h9kd had not
    /// yet inherited the environment its credential names). Ends with the same retry reassurance
    /// <see cref="Explain"/>'s own 401 message ends with, rather than leaving it unsaid here: a
    /// caller composing an operator-facing line from this exception's own message (closeout's merge
    /// notice, the retry sweep's queued-notice log line) trusts that the recorded reason already
    /// says whether and how the write retries, which held for a 401 but not for this vault-resolution
    /// case until this sentence was added (independent pre-PR review, adversarial lens, cycle 2).
    /// <see cref="account"/>'s own refusals end mid-sentence, on a bare command
    /// (<c>h9k connection add jira --help</c>) rather than a full stop, since they are written to be
    /// read on their own — appended to directly, the retry sentence below ran on as if it were the
    /// same sentence as the command (independent pre-PR review, adversarial lens, cycle 3), so it is
    /// punctuated first.
    /// </summary>
    private async ValueTask<string> AuthorizeAsync(string purpose, CancellationToken cancellationToken)
    {
        try
        {
            return await account.AuthorizationAsync(purpose, cancellationToken);
        }
        catch (DomainException exception)
        {
            string reason = exception.Message.TrimEnd();
            string punctuatedReason = reason.Length > 0 && reason[^1] is '.' or '!' or '?'
                ? reason
                : $"{reason}.";
            throw new JiraWriteExecutionException(
                JiraWriteFailureKind.AuthFailure,
                $"Could not resolve the registered credential to {purpose}: {punctuatedReason} This write "
                + "stays recorded and pending; it retries automatically once the connection is fixed.");
        }
    }

    /// <summary>
    /// A caller-supplied issue key, confirmed to be an actual PROJ-123 shape before it is built
    /// into a request URL — the same parse the read side (<see cref="JiraWorkItemProvider"/>)
    /// already requires before it builds the identical <c>/rest/api/2/issue/{key}</c> URL.
    /// Refusing anything else here (a browse URL, a traversal like <c>PROJ-1/../PROJ-2</c>, a
    /// query or fragment character) matters because <see cref="Uri"/> performs dot-segment removal
    /// on the way in: an unvalidated key can retarget the request at an endpoint other than the one
    /// recorded as this write's own intent (independent pre-PR review, adversarial lens, cycle 1).
    /// </summary>
    private static string ValidateIssueKey(string issueKey, string verb) =>
        JiraIssueKey.TryParseBareKey(issueKey, out JiraIssueKey key)
            ? key.Value
            : throw new JiraWriteExecutionException(
                JiraWriteFailureKind.Other,
                $"'{RelayedText.OneLine(issueKey)}' is not a Jira card key (PROJ-123) to {verb} — pass "
                + "the bare key, not a URL or anything else.");

    /// <summary>
    /// A description or a comment body, converted from the format it was composed in to what Jira
    /// v2's plain-string field actually renders — every field Jira v2 accepts is a single wiki-markup
    /// string regardless of the format a payload named, so both formats this class ever sees
    /// (<see cref="JiraWritePayload.Validate"/> refuses any other before a payload is ever recorded)
    /// need a real conversion, not just "markdown". "markdown" — the default
    /// <see cref="JiraWritePayload.EffectiveFormat"/> assumes, and what this repo's own
    /// card-authoring skills produce — goes through <see cref="JiraMarkupText.FromMarkdown"/>.
    /// "plain" goes through <see cref="JiraMarkupText.ToPlainLiteral"/> rather than passing through
    /// unconverted: under the retired twg transport, <c>--description-format plain</c> told
    /// Atlassian's own CLI to carry that text past its wiki-markup parser untouched, and nothing on
    /// this side of the REST swap does that anymore, so unconverted "plain" text would have Jira's
    /// own renderer interpret any wiki-markup-shaped characters it happens to contain (independent
    /// pre-PR review, adversarial lens, cycle 1). That conversion boxes the text in Jira's
    /// <c>{noformat}</c> macro only when it actually carries a character wiki markup assigns meaning
    /// to; text with none — closeout's own merge comment among them — passes through verbatim, which
    /// is what lets a bare URL still auto-link the way "plain" text always rendered before this
    /// conversion existed (independent pre-PR review, adversarial lens, cycle 2).
    /// <para>
    /// Anything other than "markdown" or "plain" is refused here too, not only at
    /// <see cref="JiraWritePayload.Validate"/>: <see cref="JiraWriteCoordinator.RetryPendingAsync"/>
    /// replays an already-recorded payload's own <c>PayloadJson</c> without re-validating it (by
    /// design — a retry mints no new intent), so a payload naming "html" and recorded pending before
    /// <c>AllowedFormats</c> narrowed to drop it would otherwise reach this method under the older
    /// build's rules and fall through to <see cref="JiraMarkupText.ToPlainLiteral"/>, which has no
    /// notion of HTML tags and would post raw HTML source to the card verbatim — the exact
    /// silently-unmet-at-the-transport outcome dropping "html" from composition was meant to prevent
    /// (independent pre-PR review, adversarial lens, cycle 10). Refusing it here as an ordinary,
    /// non-auth failure instead means an install upgrading mid-flight with such a write already
    /// pending gets a named, terminal reason asking for a recomposed payload rather than a broken
    /// card nobody was told about.
    /// </para>
    /// </summary>
    private static string? ApplyFormat(string? text, string format) =>
        text.IsBlank()
            ? text
            : format.Trim().ToLowerInvariant() switch
            {
                "markdown" => JiraMarkupText.FromMarkdown(text),
                "plain" => JiraMarkupText.ToPlainLiteral(text),
                _ => throw new JiraWriteExecutionException(
                    JiraWriteFailureKind.Other,
                    $"\"{format}\" is not a text format this executor can convert for Jira — only "
                    + "\"markdown\" and \"plain\" are. This write's payload was recorded before that "
                    + "narrowing existed, so it cannot simply be retried as-is: recompose it with a "
                    + "supported format instead."),
            };

    /// <summary>
    /// <see cref="UpdateAsync"/>'s own transport-level backstop for the identical replay hole
    /// <see cref="ApplyFormat"/> closes for a stale "html" payload: a fresh payload naming
    /// <paramref name="fieldName"/> inside "fields" is already refused at
    /// <see cref="JiraWritePayload.Validate"/>, but a write recorded pending before that refusal
    /// existed reaches here unvalidated through <see cref="JiraWriteCoordinator.RetryPendingAsync"/>'s
    /// replay. <paramref name="value"/> is <see cref="ExtractField"/>'s own already-decoded read, so
    /// this only has to check it for real content, not decode it again.
    /// </summary>
    private static void RefuseReplayedReassignmentField(string? value, string fieldName)
    {
        if (value.IsBlank())
        {
            return;
        }

        throw new JiraWriteExecutionException(
            JiraWriteFailureKind.Other,
            $"An update does not send \"{fieldName}\" inside \"fields\" — an update has no board or "
            + "work item type of its own to move a card to, so this would be silently dropped rather "
            + "than carried out. JiraWritePayload.Validate refuses this at composition now; this "
            + "write's payload was recorded pending before that refusal existed, so it cannot simply "
            + $"be retried as-is: recompose it without \"{fieldName}\" instead.");
    }

    private static void AppendFields(JsonObject fieldsNode, IReadOnlyDictionary<string, string> fields)
    {
        foreach ((string name, string value) in fields)
        {
            fieldsNode[name] = ParseFieldNode(value);
        }
    }

    /// <summary>
    /// The summary a create of <paramref name="payload"/> will send, read the way
    /// <see cref="CreateAsync"/> reads it (case-insensitively, decoded from its raw JSON), so the
    /// duplicate search compares against exactly what a create would have written.
    /// </summary>
    public static string? ComposedSummary(JiraWritePayload payload) =>
        ExtractField(
            payload.Fields is null ? [] : new Dictionary<string, string>(payload.Fields), "summary");

    /// <summary>
    /// Pull a first-class field (<c>summary</c>, <c>description</c>) out of a composed payload's
    /// fields, case-insensitively — a composing agent's own casing choice ("Description" as well as
    /// "description") should not survive into a second entry alongside this executor's
    /// own dedicated <c>summary</c>/<c>description</c> nodes for the same thing. Removes whichever
    /// casing it found so <see cref="AppendFields"/> never sees it again.
    /// </summary>
    private static string? ExtractField(Dictionary<string, string> fields, string name)
    {
        string? key = fields.Keys.FirstOrDefault(candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));
        if (key is null)
        {
            return null;
        }

        string value = fields[key];
        fields.Remove(key);
        return DecodeFieldText(value);
    }

    /// <summary>
    /// Unwraps a field's own raw JSON text (<see cref="JiraWritePayload.FromJson"/> keeps a composed
    /// field's exact JSON — quotes and all — so a custom field forced as a string survives,
    /// unmangled) back to plain content for the plain-text <c>summary</c>/<c>description</c> nodes.
    /// A value that is not itself valid JSON — a payload built directly rather than through
    /// <see cref="JiraWritePayload.FromJson"/>, this file's own tests among them — passes through
    /// unchanged, the plain text it always was. A JSON <c>null</c> decodes to blank rather than the
    /// literal text "null", mirroring <see cref="JiraWritePayload"/>'s own <c>DecodeFieldText</c>
    /// exactly: that copy is what <c>Validate</c> checks a payload against before its intent is
    /// ever recorded, but this copy is what actually reaches the request body.
    /// </summary>
    private static string DecodeFieldText(string value)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.String => document.RootElement.GetString() ?? value,
                JsonValueKind.Null => string.Empty,
                _ => value,
            };
        }
        catch (JsonException)
        {
            return value;
        }
    }

    /// <summary>
    /// A composed field's stored text is itself valid JSON whenever it came from
    /// <see cref="JiraWritePayload.FromJson"/>, which keeps the exact value a composer wrote rather
    /// than collapsing it to plain text — so it is embedded here as the same typed node (a quoted
    /// string stays a quoted string, a bare number stays a bare number) rather than being
    /// re-escaped as the contents of an outer JSON string. A value that is not itself valid JSON
    /// (a payload built directly rather than through <c>FromJson</c>) is carried through as a bare
    /// string, the plain text it always was.
    /// </summary>
    private static JsonNode? ParseFieldNode(string value)
    {
        try
        {
            return JsonNode.Parse(value);
        }
        catch (JsonException)
        {
            return JsonValue.Create(value);
        }
    }

    /// <summary>
    /// The low-level send for a read: a search or a read-back, where a timeout carries no ambiguity
    /// about whether Jira did anything, because nothing here asked it to. <see cref="SendWriteAsync"/>
    /// is the write-call twin, which treats a timeout very differently.
    /// </summary>
    private async Task<JiraResponse> SendAsync(JiraRequest request, string verb, CancellationToken cancellationToken)
    {
        JiraResponse response;
        try
        {
            response = await requester(request, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new JiraWriteExecutionException(
                JiraWriteFailureKind.Other,
                $"{account.Site} did not answer within {JiraHttp.Deadline.TotalSeconds:0} seconds while "
                + $"trying to {verb}. Check the site is reachable from this machine and try again.");
        }
        catch (HttpRequestException exception)
        {
            throw new JiraWriteExecutionException(
                JiraWriteFailureKind.Other,
                $"Could not reach {account.Site} to {verb}: {RelayedText.OneLine(exception.Message)}. Check "
                + "the site URL on the registered connection (h9k connection list) and that this machine "
                + "can reach it.");
        }

        return response.StatusCode is >= 200 and < 300 ? response : throw Explain(response, verb);
    }

    /// <summary>
    /// The low-level send for a write itself: a create, an update, or a comment. Unlike
    /// <see cref="SendAsync"/>, a timeout here is the one genuinely ambiguous case this whole
    /// feature has to carry forward: the request may have reached Jira and been carried out before
    /// the connection dropped, so this refuses to guess "nothing happened" the way an ordinary
    /// network failure would, mirroring the old process-transport's "exited before its own answer
    /// could be read back" case.
    /// </summary>
    private async Task<JiraResponse> SendWriteAsync(JiraRequest request, string verb, CancellationToken cancellationToken)
    {
        JiraResponse response;
        try
        {
            response = await requester(request, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new JiraWriteExecutionException(
                JiraWriteFailureKind.Other,
                $"{account.Site} did not answer within {JiraHttp.Deadline.TotalSeconds:0} seconds while "
                + $"trying to {verb}, after the request was already sent. This is genuinely ambiguous — "
                + "it may have been carried out before the connection dropped — so do not assume nothing "
                + "happened: for a create, run the same write again — before creating, this executor "
                + "searches the board for a card this login created for the task, with the same summary, "
                + "and adopts it if it exists rather than filing a second one; for an update or a "
                + "comment, check the board before retrying.");
        }
        catch (HttpRequestException exception)
        {
            throw new JiraWriteExecutionException(
                JiraWriteFailureKind.Other,
                $"Could not reach {account.Site} to {verb}: {RelayedText.OneLine(exception.Message)}. This "
                + "is genuinely ambiguous, the same as the timeout above — the connection can drop after "
                + "Jira already received and carried out the request, while the response was still being "
                + "read back, so do not assume nothing happened: for a create, run the same write again — "
                + "before creating, this executor searches the board for a card this login created for the "
                + "task, with the same summary, and adopts it if it exists rather than filing a second one; "
                + "for an update or a comment, check the board before retrying. Check "
                + "the site URL on the registered connection (h9k connection list) and that this machine "
                + "can reach it.");
        }

        return response.StatusCode is >= 200 and < 300 ? response : throw Explain(response, verb);
    }

    /// <summary>
    /// Turn a failed response into the one sentence that says what to do next. A 401 is the one
    /// class this executor treats as a rejected credential rather than an ordinary refusal — the
    /// registered API token was revoked or rotated — everything else (a bad payload, a permission
    /// problem, a rate limit, Jira's own outage) is an ordinary refusal that needs a freshly
    /// composed write or a human looking at Jira directly, not a silent retry.
    /// </summary>
    private JiraWriteExecutionException Explain(JiraResponse response, string verb)
    {
        string reported = Reported(response.Body);
        string suffix = reported.IsBlank() ? string.Empty : $" Jira reported: {reported}";

        return (HttpStatusCode)response.StatusCode == HttpStatusCode.Unauthorized
            ? new JiraWriteExecutionException(
                JiraWriteFailureKind.AuthFailure,
                $"{account.Site} rejected the registered credentials while trying to {verb}.{suffix} The "
                + "API token may have been revoked or rotated: create a fresh one at "
                + "https://id.atlassian.com/manage-profile/security/api-tokens and register the "
                + "connection again with h9k connection add jira. This write stays recorded and pending; "
                + "it retries automatically once the connection is fixed.")
            : new JiraWriteExecutionException(
                JiraWriteFailureKind.Other,
                $"Jira refused to {verb} at {account.Site} (HTTP {response.StatusCode}).{suffix}");
    }

    /// <summary>
    /// What Jira said, in the two shapes it says it: an <c>errorMessages</c> array for most
    /// failures and an <c>errors</c> object keyed by field for validation — the same two shapes
    /// <see cref="JiraWorkItemProvider"/>'s own read-side error handling already reads, duplicated
    /// here rather than shared since extracting a common helper for two call sites this small would
    /// cost more to navigate than the duplication itself.
    /// </summary>
    private static string Reported(string body)
    {
        if (body.IsBlank())
        {
            return string.Empty;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Bounded(body);
            }

            List<string> messages = [];
            if (document.RootElement.TryGetProperty("errorMessages", out JsonElement array)
                && array.ValueKind == JsonValueKind.Array)
            {
                messages.AddRange(
                    from element in array.EnumerateArray()
                    where element.ValueKind == JsonValueKind.String
                    select element.GetString() ?? string.Empty);
            }

            if (document.RootElement.TryGetProperty("errors", out JsonElement errors)
                && errors.ValueKind == JsonValueKind.Object)
            {
                messages.AddRange(
                    from field in errors.EnumerateObject()
                    where field.Value.ValueKind == JsonValueKind.String
                    select $"{field.Name}: {field.Value.GetString()}");
            }

            return messages.Count == 0
                ? Bounded(body)
                : Bounded(string.Join(" ", messages.Where(message => message.IsNotBlank())));
        }
        catch (JsonException)
        {
            return Bounded(body);
        }
    }

    private static string Bounded(string text) => RelayedText.Truncate(RelayedText.OneLine(text).Trim(), 400);

    private static string Head(string body) => Bounded(body).IsBlank() ? "nothing at all" : Bounded(body);

    /// <summary>The key a create or a read-back answers with, or null when the response carries none.</summary>
    private static string? ExtractKey(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("key", out JsonElement key)
                && key.ValueKind == JsonValueKind.String
                ? key.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

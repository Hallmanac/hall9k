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
public sealed class JiraWriteExecutionException(JiraWriteFailureKind kind, string message) : Exception(message)
{
    public JiraWriteFailureKind Kind { get; } = kind;

    public bool IsAuthFailure => Kind == JiraWriteFailureKind.AuthFailure;
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
/// One deliberate exception to "everything is v2": the dedup marker search
/// (<see cref="FindByMarkerAsync"/>) calls the newer <c>/rest/api/3/search/jql</c> endpoint rather
/// than the classic <c>/rest/api/2/search</c>/<c>/rest/api/3/search</c> GET/POST pair, which
/// Atlassian has been retiring in favor of it — there is no v2 equivalent of the replacement.
/// Nothing about that endpoint touches rich text (it returns only a candidate's key), so this does
/// not reopen the v2-for-text rationale above; it is a plain JQL lookup that happens to live under
/// a different version prefix. This could not be verified against a live Jira Cloud tenant from
/// this build environment (no network access here), so the exact request/response shape is this
/// class's own best reading of Atlassian's published migration guidance rather than an observed
/// fact — flagged here per AGENTS.md's "never guess at unobserved facts" so whoever first runs this
/// against a real tenant knows exactly which corner to watch.
/// </para>
/// </summary>
public sealed class JiraWriteExecutor(JiraAccount account, JiraRequester? requester = null)
{
    /// <summary>
    /// A card carrying this exact text in its description is a card hall9k made for this task —
    /// the physical dedup gate (mirroring the GitHub read-back gate): searched for before every
    /// create, so a crash (or any failure) between Jira creating a card and hall9k recording it
    /// narrows the window for a second card on a later attempt rather than closing it outright.
    /// Scoped to the task rather than to one write attempt's own guid: a fresh <c>SubmitAsync</c>
    /// mints a new write id every time, so a marker keyed to that guid could never be found by the
    /// very next attempt it exists to guard — the task is the identity that must not get a second
    /// card, so the task is what the marker names.
    /// </summary>
    public static string Marker(Guid taskId) => $"hall9k-task:{taskId:D}";

    /// <summary>
    /// A JQL clause valid on any tenant and guaranteed to match nothing, used to prove the
    /// registered credential works without touching any real card. A synthetic key (as opposed to
    /// a date far in the future) is rejected by Jira's own key-format validation before the search
    /// ever runs, which would turn a healthy, authenticated install into "could not confirm" — this
    /// compares a real field against a value nothing will ever satisfy instead.
    /// </summary>
    public const string ProbeJql = "created > \"2999-01-01\"";

    /// <summary>
    /// Caps how many of <see cref="FindByMarkerAsync"/>'s own search hits get their own confirming
    /// read. A mature board can return a full page of loosely-matching candidates (JQL's <c>~</c>
    /// is a Lucene text match, not an equality check), and each confirmation is a synchronous call
    /// of its own — left unbounded, a create's dedup gate could cost enough sequential requests to
    /// run past <c>DaemonOptions.PendingJiraWriteCeiling</c> in the worst case (that
    /// option lives in the daemon project this connector cannot reference, so the number here is
    /// duplicated rather than shared). Passed to the search itself as its own page-size limit too,
    /// so the search does not even return more than this many candidates to begin with — the
    /// client-side cap on top is defense in depth against a search that ignores or exceeds it.
    /// </summary>
    private const int MaxMarkerSearchCandidates = 10;

    private readonly JiraRequester requester = requester ?? JiraHttp.Requester;

    /// <summary>
    /// The physical half of the dedup gate: does a card already carry this task's marker. Called
    /// before every create, first attempt and every later one alike, so a create Jira completed but
    /// hall9k never recorded is found rather than duplicated. <c>~</c> is a Lucene text-analysis
    /// match with no ordering guarantee, so the search can return several candidates with the
    /// actual marker-carrying card anywhere among them — every returned candidate, up to
    /// <see cref="MaxMarkerSearchCandidates"/>, is confirmed against its own description field in
    /// search order (<see cref="CandidateCarriesMarkerAsync"/>) until one carries the marker,
    /// rather than trusting only whichever the search happened to rank first.
    /// <para>
    /// A page that comes back exactly full is not trusted as an exhaustive negative even once every
    /// one of those candidates is confirmed clear: the search was capped at that many results, so
    /// the marker-carrying card itself could be sitting just past the cut, unseen and unconfirmed.
    /// This refuses rather than returning null in that case, the same "an unconfirmable check
    /// refuses rather than guesses" doctrine <see cref="CandidateCarriesMarkerAsync"/> already
    /// applies to a single unreadable candidate.
    /// </para>
    /// </summary>
    public async Task<string?> FindByMarkerAsync(Guid taskId, CancellationToken cancellationToken)
    {
        string authorization = await AuthorizeAsync(
            $"search for {Marker(taskId)} at {account.Site}", cancellationToken);
        JsonObject body = new()
        {
            ["jql"] = $"text ~ \"{Marker(taskId)}\"",
            ["maxResults"] = MaxMarkerSearchCandidates,
            ["fields"] = new JsonArray("key"),
        };
        JiraResponse response = await SendAsync(
            new JiraRequest(HttpMethod.Post, account.Endpoint("/rest/api/3/search/jql"), authorization, body.ToJsonString()),
            $"search for {Marker(taskId)}",
            cancellationToken);

        IReadOnlyList<string> candidates = ExtractKeys(response.Body, out bool readable);
        if (!readable)
        {
            throw new JiraWriteExecutionException(
                JiraWriteFailureKind.Other,
                $"The marker search for {Marker(taskId)} could not be confirmed readable — Jira's "
                + "answer came back as something other than the expected {\"issues\": [...]} shape "
                + "(not JSON, not an object, or missing the issues array). Refusing to create on an "
                + $"unconfirmed dedup check; check the board by hand for {Marker(taskId)} and, if it is "
                + "not there, run the write again.");
        }

        foreach (string candidate in candidates.Take(MaxMarkerSearchCandidates))
        {
            if (await CandidateCarriesMarkerAsync(candidate, taskId, cancellationToken))
            {
                return candidate;
            }
        }

        if (candidates.Count >= MaxMarkerSearchCandidates)
        {
            throw new JiraWriteExecutionException(
                JiraWriteFailureKind.Other,
                $"The marker search for {Marker(taskId)} came back with a full page of "
                + $"{MaxMarkerSearchCandidates} candidates, none of which carried the marker — but the "
                + "search itself was capped at that many results, so a marker-carrying card ranked "
                + "beyond the page could exist and never got checked. Refusing to create on an "
                + $"unconfirmed dedup check; check the board by hand for {Marker(taskId)} and, if it is "
                + "not there, run the write again.");
        }

        return null;
    }

    /// <summary>
    /// <c>~</c> is Jira's CONTAINS operator, evaluated through Lucene text analysis rather than as
    /// an equality check, and the marker's own reserved characters (<c>:</c>, <c>-</c>) are
    /// tokenized apart rather than matched whole — so a search for this task's marker can return a
    /// different card whose tokens merely overlap, and nothing about the search itself proves the
    /// candidate actually carries this task's marker. This reads the candidate's own description
    /// field directly, by key, and confirms the exact marker text is present in it before
    /// <see cref="FindByMarkerAsync"/> is allowed to trust the match — parsed out of the response's
    /// own <c>fields.description</c> JSON string, never a raw-text scan of the whole response.
    /// </summary>
    private async Task<bool> CandidateCarriesMarkerAsync(string candidateKey, Guid taskId, CancellationToken cancellationToken)
    {
        string authorization = await AuthorizeAsync(
            $"read {candidateKey} at {account.Site}", cancellationToken);
        JiraResponse response = await SendAsync(
            new JiraRequest(HttpMethod.Get, account.Endpoint($"/rest/api/2/issue/{candidateKey}?fields=description"), authorization),
            $"read {candidateKey} back",
            cancellationToken);

        string? description = ExtractDescription(response.Body, out bool readable);
        if (!readable)
        {
            throw new JiraWriteExecutionException(
                JiraWriteFailureKind.Other,
                $"Jira found a card ({candidateKey}) that may already carry this task's marker, but its "
                + "description could not be read back to confirm it — the response body was not the "
                + "expected shape (not JSON, not an object, or missing the fields object). Refusing to "
                + $"create a second card on an unconfirmed dedup check; check the board by hand for "
                + $"{Marker(taskId)} and, if it is not there, run the write again.");
        }

        return (description ?? string.Empty).Contains(Marker(taskId), StringComparison.Ordinal);
    }

    /// <summary>
    /// One card, authored from a composed payload, then read back and verified — never trusted on
    /// Jira's own create response alone, the same discipline
    /// <see cref="GitHubWorkItemProvider.CreateAsync"/> applies to <c>gh issue create</c>.
    /// </summary>
    public async Task<JiraWriteResult> CreateAsync(
        JiraProjectKey project, JiraWritePayload payload, Guid taskId, CancellationToken cancellationToken)
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
        string description = AppendMarker(ApplyFormat(ExtractField(fields, "description"), payload.EffectiveFormat), Marker(taskId));

        // Composed fields are laid down first, and the three reserved nodes are written after,
        // deliberately overwriting anything a composed payload happened to also name — a card is
        // always filed against the board hall9k resolved and the work item type it validated
        // (independent pre-PR review, adversarial lens, cycle 1), never a project or issuetype a
        // composer smuggled into "fields" alongside them.
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
    /// response is what this is actually trusted on.
    /// </summary>
    public async Task<JiraWriteResult> UpdateAsync(string issueKey, JiraWritePayload payload, CancellationToken cancellationToken)
    {
        issueKey = ValidateIssueKey(issueKey, "update");

        Dictionary<string, string> fields = payload.Fields is null
            ? []
            : new Dictionary<string, string>(payload.Fields);
        string? summary = ExtractField(fields, "summary");
        string? description = ExtractField(fields, "description");

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
                + "comment carries no marker a later attempt could use to tell it apart from a fresh "
                + $"one. Check {issueKey} before resubmitting.");
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
        // marker search, an update's own idempotent re-apply).
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
                    : "The marker search this executor runs first will find the card if it exists rather "
                        + "than filing a second one, but Jira's own search index updates asynchronously, so "
                        + "a resubmission inside that lag window can still find nothing and file a second "
                        + "card — check the board before resubmitting if you can."));
        }

        string? found = ExtractKey(response.Body);
        if (found is null)
        {
            throw new JiraWriteExecutionException(
                JiraWriteFailureKind.Other,
                $"Jira reported {issueKey} {verb}, but reading it back to verify found nothing. The "
                + "write was not recorded — check the board before writing again.");
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
    /// A read-only probe for <c>h9k doctor</c>: does an authenticated Jira search go through right
    /// now, with no card touched either way. Distinguishes a rejected credential from anything else
    /// so the fix taught is the right one.
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
            await SendAsync(
                new JiraRequest(HttpMethod.Post, account.Endpoint("/rest/api/3/search/jql"), authorization,
                    new JsonObject { ["jql"] = ProbeJql, ["maxResults"] = 1, ["fields"] = new JsonArray("key") }.ToJsonString()),
                "probe authentication",
                cancellationToken);
            return JiraAuthProbeResult.Authenticated;
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
    /// </summary>
    private static string? ApplyFormat(string? text, string format) =>
        text.IsBlank()
            ? text
            : string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase)
                ? JiraMarkupText.FromMarkdown(text)
                : JiraMarkupText.ToPlainLiteral(text);

    private static void AppendFields(JsonObject fieldsNode, IReadOnlyDictionary<string, string> fields)
    {
        foreach ((string name, string value) in fields)
        {
            fieldsNode[name] = ParseFieldNode(value);
        }
    }

    /// <summary>
    /// Pull a first-class field (<c>summary</c>, <c>description</c>) out of a composed payload's
    /// fields, case-insensitively — a composing agent's own casing choice ("Description" as well as
    /// "description") should not survive into a second, marker-only entry alongside this executor's
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
    /// The marker appended to whatever description a composed payload already carries, rather than
    /// replacing it: the description is what the card's audience reads, and the marker only has to
    /// be findable by a search, not visible at the top.
    /// </summary>
    private static string AppendMarker(string? description, string marker) =>
        description.IsNotBlank() ? $"{description}\n\n[{marker}]" : $"[{marker}]";

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
                + "happened: for a create, run the same write again — the marker search this executor "
                + "runs first will find the card if it exists rather than filing a second one; for an "
                + "update or a comment, check the board before retrying.");
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

    /// <summary>
    /// Every candidate key a search's answer carries, in the order Jira returned them.
    /// <paramref name="readable"/> is false for a body that could not be confirmed as the expected
    /// <c>{"issues": [...]}</c> shape — not JSON, not an object, or missing the <c>issues</c> array
    /// — which <see cref="FindByMarkerAsync"/> must not read the same way as a genuine
    /// <c>{"issues": []}</c> answer: the two look identical as an empty list, but only the second
    /// actually proves no card carries the marker (independent pre-PR review, both lenses, cycle 1).
    /// </summary>
    private static IReadOnlyList<string> ExtractKeys(string body, out bool readable)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("issues", out JsonElement issues)
                || issues.ValueKind != JsonValueKind.Array)
            {
                readable = false;
                return [];
            }

            List<string> keys = [];
            foreach (JsonElement item in issues.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("key", out JsonElement key) && key.ValueKind == JsonValueKind.String
                    && key.GetString() is { } value)
                {
                    keys.Add(value);
                }
            }

            readable = true;
            return keys;
        }
        catch (JsonException)
        {
            readable = false;
            return [];
        }
    }

    /// <summary>
    /// The <c>fields.description</c> string a v2 issue read answers with — the parsed field itself,
    /// never the response body scanned as raw text, which is what
    /// <see cref="CandidateCarriesMarkerAsync"/>'s own dedup check needs: a marker match has to come
    /// from the card's actual content, not from wherever the marker's characters happen to appear
    /// in the JSON envelope around it.
    /// <para>
    /// <paramref name="readable"/> is false only when the body itself could not be confirmed as the
    /// expected shape — not JSON, not an object, or missing the <c>fields</c> object — never merely
    /// because a card genuinely has no description: a missing or JSON-null <c>description</c> inside
    /// an otherwise-readable <c>fields</c> object is a real, observed fact (the card has no
    /// description) rather than an unconfirmable read, so it returns null and readable stays true.
    /// </para>
    /// </summary>
    private static string? ExtractDescription(string body, out bool readable)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("fields", out JsonElement fields)
                || fields.ValueKind != JsonValueKind.Object)
            {
                readable = false;
                return null;
            }

            readable = true;
            return fields.TryGetProperty("description", out JsonElement description)
                && description.ValueKind == JsonValueKind.String
                ? description.GetString()
                : null;
        }
        catch (JsonException)
        {
            readable = false;
            return null;
        }
    }
}

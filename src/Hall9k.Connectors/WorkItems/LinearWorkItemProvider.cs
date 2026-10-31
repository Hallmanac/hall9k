using System.Net;
using System.Text.Json;
using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// Who a Linear API key belongs to and which workspace it reads, as Linear's <c>viewer</c> and
/// <c>organization</c> answered: the user's <see cref="Id"/> (what the claim gate compares an
/// assignee against), the <see cref="Name"/> a human reads, and the workspace's
/// <see cref="WorkspaceKey"/> (its <c>urlKey</c>, the slug in every Linear address).
/// </summary>
public sealed record LinearViewer(string Id, string Name, string WorkspaceKey);

/// <summary>
/// The viewer read with its refusal kept rather than thrown: exactly one of <see cref="Viewer"/> and
/// <see cref="Failure"/> is set, and <see cref="FailureKind"/> says what could end a hold that rests
/// on it, in the same three classes <see cref="TrackerReadFailure"/> gives Jira and GitHub.
/// <see cref="KeyUnavailable"/> is set when the vault could not produce the key, so Linear was never
/// asked: <see cref="TrackerReadFailure.Credentials"/> alone cannot tell that from Linear refusing it.
/// </summary>
public sealed record LinearViewerRead(
    LinearViewer? Viewer, DomainException? Failure, TrackerReadFailure FailureKind, bool KeyUnavailable = false);

/// <summary>
/// One issue as itself, with the identifiers Linear says it used to carry. <see cref="PreviousIdentifiers"/>
/// is what lets a relink after a move between teams be proven rather than claimed.
/// </summary>
public sealed record LinearIssueRead(ImportedWorkItem Item, IReadOnlyList<string> PreviousIdentifiers);

/// <summary>
/// An issue read with its refusal kept rather than thrown, for a caller (the claim gate) that
/// has to tell a key to renew from an issue to look at from an outage to wait out.
/// </summary>
public sealed record LinearIssueAttempt(LinearIssueRead? Read, DomainException? Failure, TrackerReadFailure FailureKind);

/// <summary>A Linear team as the workspace answered for it: the id of the team, its key, and its name.</summary>
public sealed record LinearTeam(string Id, string Key, string Name);

/// <summary>
/// Linear through its GraphQL API, on the credentials of a registered connection (PLAN.md §10). It
/// speaks to Linear through one <see cref="LinearRequester"/>, the shape <see cref="JiraRequester"/>
/// gives the Jira provider, so every test drives it with fixture responses written from Linear's public
/// schema and none reaches a live workspace.
/// <para>
/// What this class does is the import snapshot, the viewer check that proves a key at registration,
/// the verification behind <c>h9k task link-linear</c>, and the claim gate's assignee read and write.
/// Like Jira's, reading is configuration-agnostic: workflow states are per team and renamable, but each carries a fixed
/// <c>type</c>, and that type is what the mapping below reads, so any team's renames survive.
/// </para>
/// <para>
/// GraphQL changes the failure model. Linear answers HTTP 200 with an <c>errors</c> array for many
/// failures, and may answer 200 with partial <c>data</c> beside it, so a response carrying errors is a
/// failure whatever its status, and no field of its data is trusted.
/// </para>
/// </summary>
public sealed class LinearWorkItemProvider(
    LinearAccount account, LinearRequester? requester = null, TimeProvider? clock = null) : IWorkItemProvider
{
    private const string ViewerQuery =
        "query HallViewer { viewer { id name } organization { urlKey } }";

    /// <summary>
    /// Exactly the fields the import maps, plus <c>previousIdentifiers</c>, which only a relink reads.
    /// <c>issue(id:)</c> takes the human identifier directly (linear.app/developers/graphql).
    /// </summary>
    private const string IssueQuery =
        "query HallIssue($id: String!) { issue(id: $id) { identifier title description url "
        + "previousIdentifiers state { name type } } }";

    /// <summary>
    /// A team looked up by its key through the <c>teams</c> query and a <c>TeamFilter</c> key filter:
    /// <c>team(id:)</c> is not documented to take a key, so it is not used for this.
    /// </summary>
    private const string TeamQuery =
        "query HallTeam($key: String!) { teams(filter: { key: { eq: $key } }, first: 1) { nodes { id key name } } }";

    /// <summary>
    /// The assignee and nothing else, for the claim gate: one fresh read of the single nullable user
    /// on the issue. The user's <c>id</c> is what is compared; <c>name</c> is only for sentences.
    /// The issue's <c>delegate</c> field is deliberately not asked for.
    /// </summary>
    private const string AssigneeQuery =
        "query HallIssueAssignee($id: String!) { issue(id: $id) { identifier assignee { id name } } }";

    /// <summary>
    /// One field, the assignee. <c>assigneeId</c> is the only member of the input, so a take or a
    /// release touches no state, label, or any other field. The variable is nullable, so a release
    /// sends an explicit JSON null.
    /// </summary>
    private const string AssignMutation =
        "mutation HallAssign($id: String!, $assigneeId: String) "
        + "{ issueUpdate(id: $id, input: { assigneeId: $assigneeId }) { success } }";

    private readonly LinearRequester requester = requester ?? LinearHttp.Requester;
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    public WorkItemProvider Provider => WorkItemProvider.Linear;

    /// <summary>The workspace this provider reads, for callers composing their own messages.</summary>
    public string Workspace => account.Workspace;

    /// <summary>
    /// The gated read: a task is seeded from an issue only when its state type says it is planned
    /// work. The adoption policy proper ("only an item positively reported open") lives in
    /// <see cref="WorkItemImporter"/>; this maps Linear's state types so that policy can read them.
    /// </summary>
    public async Task<ImportedWorkItem> ImportAsync(
        WorkItemImportRequest request, CancellationToken cancellationToken)
    {
        return await ReadAsync(ParseIdentifier(request.Reference), cancellationToken);
    }

    /// <summary>
    /// What a human typed, as an identifier: the bare identifier or an issue URL from the registered
    /// workspace. A URL from any other workspace is refused, because a stored reference records no
    /// workspace.
    /// </summary>
    public LinearIssueIdentifier ParseIdentifier(string? reference) =>
        LinearIssueIdentifier.Parse(reference, RegisteredWorkspace());

    /// <summary>
    /// One issue, read as itself rather than as a candidate for adoption: the door that records an
    /// issue that already exists (<c>h9k task link-linear</c>) applies no state gate, exactly as
    /// <c>link-jira</c> does not.
    /// </summary>
    public async Task<ImportedWorkItem> ReadAsync(LinearIssueIdentifier identifier, CancellationToken cancellationToken) =>
        (await ReadIssueAsync(identifier, cancellationToken: cancellationToken)).Item;

    /// <summary>
    /// The issue and its previous identifiers. <paramref name="linkedTask"/> is the task that already
    /// carries this issue, when the caller is reading one it linked earlier: a not-found answer then
    /// names the likely cause (the issue moved to another team, which gives it a new identifier) and
    /// the relink, because the typed identifier is no longer the issue's.
    /// </summary>
    public async Task<LinearIssueRead> ReadIssueAsync(
        LinearIssueIdentifier identifier, string? linkedTask = null, CancellationToken cancellationToken = default)
    {
        LinearIssueAttempt attempt = await TryReadIssueAsync(identifier, linkedTask, cancellationToken);
        return attempt.Read ?? throw attempt.Failure!;
    }

    /// <summary>The same read with the refusal kept and classified instead of thrown.</summary>
    public async Task<LinearIssueAttempt> TryReadIssueAsync(
        LinearIssueIdentifier identifier, string? linkedTask, CancellationToken cancellationToken)
    {
        LinearQuery query = await QueryAsync(
            IssueQuery, new { id = identifier.Value }, $"read {identifier.Value}", identifier.Value, cancellationToken,
            linkedTask);
        if (query.Data is not { } data)
        {
            return new LinearIssueAttempt(null, query.Failure, query.Kind);
        }

        using (query.Document)
        {
            if (!data.TryGetProperty("issue", out JsonElement issue) || issue.ValueKind == JsonValueKind.Null)
            {
                // A 200 with a null issue and no error is how a GraphQL server can say "no such
                // object", so it is the same answer as an explicit not-found error.
                return new LinearIssueAttempt(null, NotFound(identifier.Value, linkedTask), TrackerReadFailure.Item);
            }

            return Map(issue, clock.GetUtcNow()) is { } mapped
                ? new LinearIssueAttempt(mapped, null, TrackerReadFailure.None)
                : new LinearIssueAttempt(
                    null,
                    NotAnIssue(identifier, "the answer carries no identifier, so there is no issue in it"),
                    TrackerReadFailure.Item);
        }
    }

    /// <summary>
    /// <c>linear:ENG-123</c> points at <c>&lt;workspace&gt;/issue/ENG-123</c>. A format rule rather than a
    /// lookup, and one that needs the registered workspace, which is why a Linear reference cannot be
    /// turned into a link on a node with no Linear connection. A reference that is not an identifier
    /// is not dressed up as one.
    /// </summary>
    public Uri? WebUrl(ExternalReference reference) =>
        reference.Provider == WorkItemProvider.Linear
        && account.WorkspaceUrl is { } workspace
        && LinearIssueIdentifier.TryParseBare(reference.Reference, out LinearIssueIdentifier identifier)
            ? new Uri($"{workspace.GetLeftPart(UriPartial.Path).TrimEnd('/')}/issue/{identifier.Value}")
            : null;

    /// <summary>
    /// Prove Linear accepts the key, and report who it belongs to and which workspace it reads. Called
    /// at registration so a mistyped or revoked key is refused while the human is still looking at the
    /// command that caused it. It proves nothing about what the key is allowed to do: a key limited by
    /// permission or by team passes this and can still be refused on a later read or write.
    /// </summary>
    public async Task<LinearViewer> VerifyAccessAsync(CancellationToken cancellationToken)
    {
        LinearViewerRead read = await ReadViewerAsync(cancellationToken);
        return read.Viewer ?? throw read.Failure!;
    }

    /// <summary>The viewer read with its refusal kept and classified instead of thrown.</summary>
    public async Task<LinearViewerRead> ReadViewerAsync(CancellationToken cancellationToken)
    {
        LinearQuery query = await QueryAsync(ViewerQuery, null, "sign in", "the viewer", cancellationToken);
        if (query.Data is not { } data)
        {
            return new LinearViewerRead(null, query.Failure, query.Kind, query.KeyUnavailable);
        }

        using (query.Document)
        {
            // Parsing is not proof. A 200 carrying JSON that is not a viewer is a proxy or a portal
            // answering for Linear, and recording it as a registration would record nothing proven.
            string? id = data.TryGetProperty("viewer", out JsonElement viewer) ? ReadString(viewer, "id") : null;
            string? workspaceKey = data.TryGetProperty("organization", out JsonElement organization)
                ? ReadString(organization, "urlKey")
                : null;
            if (id is null || workspaceKey is null)
            {
                return new LinearViewerRead(
                    null,
                    new DomainValidationException(
                        "Linear answered the sign-in check with something that is not a viewer and a workspace: "
                        + "it carries no user id or no workspace key. That is usually a proxy in front of "
                        + $"{LinearHttp.Endpoint.Host}; check this machine reaches Linear directly."),
                    TrackerReadFailure.Item);
            }

            return new LinearViewerRead(
                new LinearViewer(id, ReadString(viewer, "name") ?? id, workspaceKey), null, TrackerReadFailure.None);
        }
    }

    /// <summary>
    /// The team with this key, as this connection's key can see it, or null when none is visible.
    /// Null cannot tell a team that does not exist from one a team-limited key is not allowed to
    /// see, because Linear answers the same way for both, so a caller refusing on null says both.
    /// A failure to reach Linear or a rejected key is thrown, never mistaken for an absent team.
    /// </summary>
    public async Task<LinearTeam?> FindTeamAsync(LinearTeamKey key, CancellationToken cancellationToken)
    {
        LinearQuery query = await QueryAsync(
            TeamQuery, new { key = key.Value }, $"look up the team {key.Value}", key.Value, cancellationToken);
        if (query is not { Data: { } data })
        {
            throw query.Failure ?? throw new InvalidOperationException("A Linear query without data carries its failure.");
        }

        using (query.Document)
        {
            if (!data.TryGetProperty("teams", out JsonElement teams)
                || teams.ValueKind != JsonValueKind.Object
                || !teams.TryGetProperty("nodes", out JsonElement nodes)
                || nodes.ValueKind != JsonValueKind.Array)
            {
                throw new DomainValidationException(
                    "Linear answered the team lookup with something that is not a list of teams. That is "
                    + $"usually a proxy in front of {LinearHttp.Endpoint.Host}; check this machine reaches Linear directly.");
            }

            // The filter is an exact match, but a node is accepted only when it says it is the team
            // that was asked for, so an answer that ignored the filter cannot bind the wrong team.
            foreach (JsonElement node in nodes.EnumerateArray())
            {
                if (ReadString(node, "id") is { } id
                    && ReadString(node, "key") is { } answered
                    && answered.Equals(key.Value, StringComparison.OrdinalIgnoreCase))
                {
                    return new LinearTeam(id, answered.ToUpperInvariant(), ReadString(node, "name") ?? answered);
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Who Linear says holds the issue right now, or why it could not say. Linear's assignee is one
    /// nullable user, so the answer is empty or one long, and what is compared is the user's id,
    /// ordinally: an opaque token, never folded and never the name. An answer that is not an issue
    /// with an assignee field the gate can trust fails rather than reading as "nobody".
    /// <paramref name="linkedTask"/> is how the not-found sentence names the relink.
    /// </summary>
    public async Task<TrackerAssigneeRead> ReadAssigneeAsync(
        LinearIssueIdentifier identifier, string? linkedTask, CancellationToken cancellationToken)
    {
        LinearQuery query = await QueryAsync(
            AssigneeQuery, new { id = identifier.Value }, $"read the assignee of {identifier.Value}",
            identifier.Value, cancellationToken, linkedTask);
        if (query.Data is not { } data)
        {
            return TrackerAssigneeRead.Unreadable(query.Failure!.Message, query.Kind);
        }

        using (query.Document)
        {
            if (!data.TryGetProperty("issue", out JsonElement issue) || issue.ValueKind == JsonValueKind.Null)
            {
                return TrackerAssigneeRead.Unreadable(
                    NotFound(identifier.Value, linkedTask).Message, TrackerReadFailure.Item);
            }

            // Both members must be present: an issue object without an identifier does not establish
            // which issue it answers for (the same rule as Map), and one without an assignee member is
            // not an answer about the assignee. Reading either absence as "nobody" would let a take
            // overwrite whoever holds it.
            if (issue.ValueKind != JsonValueKind.Object
                || ReadString(issue, "identifier") is not { Length: > 0 }
                || !issue.TryGetProperty("assignee", out JsonElement assignee)
                || assignee.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object))
            {
                return TrackerAssigneeRead.Unreadable(
                    NotAnIssue(identifier, "the answer carries no identifier or no assignee field, so it does not say who holds it").Message,
                    TrackerReadFailure.Item);
            }

            if (assignee.ValueKind == JsonValueKind.Null)
            {
                return TrackerAssigneeRead.Nobody();
            }

            return ReadString(assignee, "id") is { Length: > 0 } id
                ? TrackerAssigneeRead.HeldBy(id, ReadString(assignee, "name"))
                : TrackerAssigneeRead.Unreadable(
                    NotAnIssue(identifier, "its assignee carries no user id, so it does not say who holds it").Message,
                    TrackerReadFailure.Item);
        }
    }

    /// <summary>
    /// Put <paramref name="assigneeId"/> in the issue's assignee field, or clear it when null, with
    /// <c>issueUpdate</c> and nothing else in its input. Returned rather than thrown: a refusal is
    /// Linear's own words, plus the fact that a write needs a key with write permission on the issue's
    /// team, except where the failure was an outage, a missing issue or an unavailable key. Accepted
    /// is not proven; the caller reads the issue back.
    /// </summary>
    public async Task<TrackerAssignmentWrite> SetAssigneeAsync(
        LinearIssueIdentifier identifier, string? assigneeId, CancellationToken cancellationToken)
    {
        LinearQuery query = await QueryAsync(
            AssignMutation, new { id = identifier.Value, assigneeId },
            assigneeId is null ? $"clear the assignee of {identifier.Value}" : $"assign {identifier.Value}",
            identifier.Value, cancellationToken);
        if (query.Data is not { } data)
        {
            // The permission sentence states what a write needs, so it is left off where Linear's answer
            // had nothing to do with the key's permissions: an outage, an issue Linear could not find,
            // and a vault that could not produce the key at all.
            return TrackerAssignmentWrite.Refused(
                query.Kind == TrackerReadFailure.Outage || query.KeyUnavailable || query.Failure is DomainNotFoundException
                    ? query.Failure!.Message
                    : $"{query.Failure!.Message} {NeedsWritePermission}");
        }

        using (query.Document)
        {
            bool success = data.TryGetProperty("issueUpdate", out JsonElement update)
                && update.ValueKind == JsonValueKind.Object
                && update.TryGetProperty("success", out JsonElement flag)
                && flag.ValueKind == JsonValueKind.True;
            return success
                ? TrackerAssignmentWrite.Wrote()
                : TrackerAssignmentWrite.Refused(
                    $"Linear answered the assignment of {identifier.Value} with success false, or with no "
                    + $"success at all. {NeedsWritePermission}");
        }
    }

    private const string NeedsWritePermission =
        "Changing an assignee needs an API key with write permission on the issue's team.";

    private Uri RegisteredWorkspace() =>
        account.WorkspaceUrl
        ?? throw new DomainValidationException(
            "This Linear account has no workspace address recorded, so an issue URL cannot be checked "
            + "against it. Register the connection again: h9k connection add linear");

    /// <summary>
    /// One call and what came back: the <c>data</c> object when it is trustworthy, or the refusal and
    /// its class. A response is trustworthy only with a 2xx status, a JSON object, no <c>errors</c>
    /// array at all (Linear documents HTTP 200 with errors and with partial data), and a <c>data</c>
    /// object. The document is handed back undisposed because <c>Data</c> points into it.
    /// </summary>
    private sealed record LinearQuery(
        JsonDocument? Document, JsonElement? Data, DomainException? Failure, TrackerReadFailure Kind,
        bool KeyUnavailable = false);

    private async Task<LinearQuery> QueryAsync(
        string query, object? variables, string verb, string subject, CancellationToken cancellationToken,
        string? linkedTask = null)
    {
        string authorization;
        try
        {
            authorization = await account.AuthorizationAsync($"{verb} at {account.Workspace}", cancellationToken);
        }
        catch (DomainException exception)
        {
            // The vault could not produce the key at all. Not Linear refusing it, but the same
            // remedy (register the connection again), so it is classified with a refusal. The vault
            // was written for Jira first and names that command in its refusals, so the remedy is
            // restated for Linear here rather than relayed: an agent following "connection add jira"
            // would be sent to a command that needs a site and an email.
            string restated = exception.Message.Replace(
                "h9k connection add jira", "h9k connection add linear", StringComparison.Ordinal);
            return Failed(
                exception is DomainNotFoundException
                    ? new DomainNotFoundException(restated)
                    : new DomainValidationException(restated),
                TrackerReadFailure.Credentials,
                keyUnavailable: true);
        }

        string body = variables is null
            ? JsonSerializer.Serialize(new { query })
            : JsonSerializer.Serialize(new { query, variables });
        LinearResponse response;
        try
        {
            response = await requester(new LinearRequest(LinearHttp.Endpoint, authorization, body), cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(
                new DomainValidationException(
                    $"Linear did not answer within {LinearHttp.Deadline.TotalSeconds:0} seconds while trying to "
                    + $"{verb}. Check this machine can reach {LinearHttp.Endpoint.Host} (a VPN or a proxy is the "
                    + "usual reason it cannot from here but can from the browser) and try again."),
                TrackerReadFailure.Outage);
        }
        catch (HttpRequestException exception)
        {
            return Failed(
                new DomainValidationException(
                    $"Could not reach Linear to {verb}: {RelayedText.OneLine(exception.Message)}. Check that "
                    + $"this machine can reach {LinearHttp.Endpoint.Host}."),
                TrackerReadFailure.Outage);
        }

        JsonDocument? document = TryParse(response.Body);
        if (document is { RootElement: { ValueKind: JsonValueKind.Object } root }
            && root.TryGetProperty("errors", out JsonElement errors)
            && errors.ValueKind != JsonValueKind.Null
            && !(errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() == 0))
        {
            using (document)
            {
                (DomainException failure, TrackerReadFailure kind) = ExplainErrors(response, errors, verb, subject, linkedTask);
                return Failed(failure, kind);
            }
        }

        if (response.StatusCode is < 200 or >= 300)
        {
            document?.Dispose();
            (DomainException failure, TrackerReadFailure kind) = ExplainStatus(response, verb, subject, linkedTask);
            return Failed(failure, kind);
        }

        if (document is { RootElement: { ValueKind: JsonValueKind.Object } answer }
            && answer.TryGetProperty("data", out JsonElement data)
            && data.ValueKind == JsonValueKind.Object)
        {
            return new LinearQuery(document, data, null, TrackerReadFailure.None);
        }

        document?.Dispose();
        return Failed(
            new DomainValidationException(
                $"{LinearHttp.Endpoint.Host} answered with something that is not a GraphQL response while trying "
                + $"to {verb}: {Bounded(response.Body)}. That is usually a proxy or a login page in front of "
                + "Linear."),
            TrackerReadFailure.Item);
    }

    private static LinearQuery Failed(DomainException failure, TrackerReadFailure kind, bool keyUnavailable = false) =>
        new(null, null, failure, kind, keyUnavailable);

    private static JsonDocument? TryParse(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A response with an <c>errors</c> array, told apart into the three classes a gate has to act on:
    /// a rate limit (an outage, with the <c>Retry-After</c> Linear sent), a key Linear does not accept
    /// (credentials), and an issue Linear cannot give (item). Linear's documented error shape carries a
    /// <c>message</c> and an <c>extensions</c> object with a <c>type</c> and <c>code</c>; the code names
    /// below are recognised when present, and the HTTP status is consulted too, because the status of
    /// an authentication failure is not one the documentation states. An error nothing recognises
    /// quotes Linear's own first message on one line.
    /// </summary>
    private (DomainException Failure, TrackerReadFailure Kind) ExplainErrors(
        LinearResponse response, JsonElement errors, string verb, string subject, string? linkedTask)
    {
        List<(string Message, string Code, string Type)> found = [];
        if (errors.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement error in errors.EnumerateArray())
            {
                JsonElement extensions = error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("extensions", out JsonElement e) ? e : default;
                found.Add((
                    ReadString(error, "message") ?? string.Empty,
                    ReadString(extensions, "code") ?? string.Empty,
                    ReadString(extensions, "type") ?? string.Empty));
            }
        }

        string reported = Bounded(found.Count > 0 && found[0].Message.IsNotBlank() ? found[0].Message : errors.ToString());
        bool Has(Func<(string Message, string Code, string Type), bool> predicate) => found.Any(predicate);

        if (response.StatusCode == 429
            || Has(error => error.Code.Equals("RATELIMITED", StringComparison.OrdinalIgnoreCase)
                || error.Type.Contains("ratelimit", StringComparison.OrdinalIgnoreCase)))
        {
            return (RateLimited(response, verb, reported), TrackerReadFailure.Outage);
        }

        // A server error is an outage whatever its errors array says: a gateway's "upstream not found"
        // or an authentication code on a 5xx describes the failure, not an answer from Linear about
        // the key or the issue, and the remedy for an outage is to wait.
        if (response.StatusCode >= 500)
        {
            return (Unavailable(verb, response.StatusCode, reported), TrackerReadFailure.Outage);
        }

        if (response.StatusCode is 401 or 403
            || Has(error => error.Code.Contains("AUTHENTICATION", StringComparison.OrdinalIgnoreCase)
                || error.Type.Contains("authentication", StringComparison.OrdinalIgnoreCase)
                || error.Code.Equals("FORBIDDEN", StringComparison.OrdinalIgnoreCase)))
        {
            return (Rejected(verb, reported), TrackerReadFailure.Credentials);
        }

        if (Has(error => error.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || error.Code.Contains("NOT_FOUND", StringComparison.OrdinalIgnoreCase)))
        {
            return (NotFound(subject, linkedTask, reported), TrackerReadFailure.Item);
        }

        return response.StatusCode == 0
            ? (Unavailable(verb, response.StatusCode, reported), TrackerReadFailure.Outage)
            : (new DomainValidationException($"Linear refused to {verb}.{Suffix(reported)}"), TrackerReadFailure.Item);
    }

    /// <summary>A non-2xx response with no GraphQL errors in it, classified by its status alone.</summary>
    private (DomainException Failure, TrackerReadFailure Kind) ExplainStatus(
        LinearResponse response, string verb, string subject, string? linkedTask)
    {
        string reported = Bounded(response.Body);
        return (HttpStatusCode)response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                (Rejected(verb, reported), TrackerReadFailure.Credentials),
            HttpStatusCode.TooManyRequests =>
                (RateLimited(response, verb, reported), TrackerReadFailure.Outage),
            HttpStatusCode.NotFound =>
                (NotFound(subject, linkedTask, reported), TrackerReadFailure.Item),
            >= HttpStatusCode.InternalServerError =>
                (Unavailable(verb, response.StatusCode, reported), TrackerReadFailure.Outage),
            _ => (new DomainValidationException(
                $"Linear refused to {verb} (HTTP {response.StatusCode}).{Suffix(reported)}"), TrackerReadFailure.Item),
        };
    }

    private static string Suffix(string reported) => reported.IsBlank() ? string.Empty : $" Linear reported: {reported}";

    private DomainValidationException Rejected(string verb, string reported) => new(
        $"Linear rejected the API key while trying to {verb} at {account.Workspace}.{Suffix(reported)} "
        + "Create a personal API key in Linear (Settings, then Security & access, then Personal API keys) "
        + "and register the connection again with h9k connection add linear. A key that used to work has "
        + "usually been revoked or belongs to someone who has left the workspace, and a key limited by "
        + "permission or by team is refused this way for what it is not allowed to see.");

    private static DomainValidationException RateLimited(LinearResponse response, string verb, string reported) => new(
        $"Linear is rate-limiting this key and refused to {verb}"
        + (response.RetryAfter.IsNotBlank()
            ? $"; its Retry-After is {RelayedText.OneLine(response.RetryAfter)}."
            : "; it sent no Retry-After.")
        + Suffix(reported)
        + " Hall9k does not retry a rate-limited Linear call within the same attempt, so wait before asking again.");

    private static DomainValidationException Unavailable(string verb, int status, string reported) => new(
        $"Linear failed while trying to {verb} (HTTP {status}).{Suffix(reported)} That is Linear's side rather "
        + "than this machine's; try again once https://linearstatus.com is clear.");

    /// <summary>
    /// Linear answers the same for an issue that does not exist and one this key cannot see, so both
    /// are named. When the caller is reading an issue a task already carries, the likeliest cause is a
    /// move to another team, which gives the issue a new identifier, and the remedy is the relink.
    /// </summary>
    private DomainNotFoundException NotFound(string identifier, string? linkedTask, string? reported = null) => new(
        linkedTask.IsNotBlank()
            ? $"Linear has no issue {identifier} in {account.Workspace}. The issue may have moved to another "
                + "team, which gives it a new identifier: find the new identifier in Linear, then record it "
                + $"with h9k task link-linear {linkedTask} <new identifier>. Linear answers the same way for "
                + "an issue this key cannot see, so a key limited to other teams looks like this too."
                + Suffix(reported ?? string.Empty)
            : $"Could not find {identifier} in {account.Workspace}: check the identifier, or confirm which team "
                + "it was created in. Linear answers the same way for an issue that does not exist and for one "
                + $"this key cannot see, so if the identifier is right, the key may be limited to other teams.{Suffix(reported ?? string.Empty)}");

    private DomainValidationException NotAnIssue(LinearIssueIdentifier identifier, string reported) => new(
        $"Linear answered for {identifier.Value} with something that is not a Linear issue: {reported}.");

    /// <summary>
    /// An issue answer onto an <see cref="ImportedWorkItem"/>. The identifier Linear answered with wins
    /// over the one that was asked for, because the stored reference must be the issue's own and a
    /// typed lower-case spelling or a URL must not become a second reference to it. An answer that
    /// carries no identifier does not establish which issue this is, so it maps to nothing.
    /// </summary>
    private LinearIssueRead? Map(JsonElement issue, DateTimeOffset observedAt)
    {
        if (issue.ValueKind != JsonValueKind.Object
            || ReadString(issue, "identifier") is not { } answered
            || !LinearIssueIdentifier.TryParseBare(answered, out LinearIssueIdentifier resolved))
        {
            return null;
        }

        List<string> previous = [];
        if (issue.TryGetProperty("previousIdentifiers", out JsonElement array)
            && array.ValueKind == JsonValueKind.Array)
        {
            previous.AddRange(
                from element in array.EnumerateArray()
                where element.ValueKind == JsonValueKind.String
                select element.GetString() ?? string.Empty);
        }

        List<ExternalReference> former = [];
        foreach (string typed in previous)
        {
            if (LinearIssueIdentifier.TryParseBare(typed, out LinearIssueIdentifier identifier)
                && identifier.Reference != resolved.Reference
                && !former.Contains(identifier.Reference))
            {
                former.Add(identifier.Reference);
            }
        }

        Uri? url = ReadString(issue, "url") is { } text
            && Uri.TryCreate(text, UriKind.Absolute, out Uri? parsed)
            && parsed.Scheme == Uri.UriSchemeHttps
                ? parsed
                : WebUrl(resolved.Reference);

        return new LinearIssueRead(
            new ImportedWorkItem(
                resolved.Reference,
                ReadString(issue, "title") ?? string.Empty,
                ReadString(issue, "description") is { } description && description.IsNotBlank() ? description : null,
                MapStatus(issue),
                url,
                observedAt)
            {
                FormerReferences = former,
            },
            previous);
    }

    /// <summary>
    /// Linear's state <c>type</c> onto Hall9k's states, at the boundary where the knowledge to do it
    /// honestly lives. State names are per team and can be renamed, but the type is fixed, so mapping
    /// the type survives any team's workflow. Backlog, unstarted, and started are open work; completed,
    /// canceled, and duplicate are finished; triage is kept apart because the refusal for it points at
    /// an idea; and a type nobody has a rule for keeps its own word and maps to nothing, which the
    /// adoption gate refuses by quoting it. The state's own name rides along as the observed label.
    /// </summary>
    private static WorkItemStatus MapStatus(JsonElement issue)
    {
        if (!issue.TryGetProperty("state", out JsonElement state) || state.ValueKind != JsonValueKind.Object)
        {
            return WorkItemStatus.Unknown;
        }

        string name = ReadString(state, "name") ?? string.Empty;
        string type = ReadString(state, "type") ?? string.Empty;
        return type.ToLowerInvariant() switch
        {
            "backlog" or "unstarted" or "started" => WorkItemStatus.Open.As(name),
            "completed" or "canceled" or "duplicate" => WorkItemStatus.Closed.As(name),
            "triage" => WorkItemStatus.Triage.As(name),
            "" => WorkItemStatus.Unknown,
            _ => WorkItemStatus.Unmapped(type),
        };
    }

    private static string Bounded(string text) => RelayedText.Truncate(RelayedText.OneLine(text).Trim(), 400);

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

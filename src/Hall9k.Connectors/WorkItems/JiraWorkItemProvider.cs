using System.Net;
using System.Text.Json;
using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// Jira Cloud through its REST API, on the credentials of a registered connection (PLAN.md §10,
/// backlog 18). The second implementation of the seam decision #60 built, and the first one that
/// needs construction: <c>gh</c> carries the machine's own login, while a Jira site and token are
/// registered per install, so this provider is built from a <see cref="JiraAccount"/> that
/// <see cref="WorkItemConnections"/> assembles out of a registered connection.
/// <para>
/// What this class does is deliberately narrow, and the shape of that narrowness is the doctrine
/// the feature was designed around: <b>reading Jira is configuration-agnostic and writing it is
/// not</b>. A GET returns the same document whatever a project's issue types are called, so the
/// platform reads cards itself — the import snapshot, and the verification behind
/// <c>h9k task link-jira</c>. Every write — create, update, comment — goes through the
/// compose/execute split instead (Brian's design, 2026-08-28, Decisions Log #99): an agent or an
/// operator composes the payload, and <c>h9k task write-jira</c> is the sole executor, running it
/// through <see cref="TwgJiraExecutor"/> rather than this provider's own REST client. This class
/// makes no write of its own; it reads.
/// </para>
/// <para>
/// API version 2 rather than 3, on purpose. The two are both current; v3 carries rich text as
/// Atlassian Document Format, a JSON tree, while v2 carries it as a string. Hall9k wants text in
/// both directions — a description that becomes agent context, a comment that is one line and a
/// link — so v2 is the version that matches what is actually being moved, and choosing v3 would
/// mean writing an ADF renderer and an ADF builder to arrive back at the strings v2 already
/// gives.
/// </para>
/// </summary>
public sealed class JiraWorkItemProvider(
    JiraAccount account, JiraRequester? requester = null, TimeProvider? clock = null) : IWorkItemProvider
{
    /// <summary>Exactly the fields the import maps. Asking for more would be storing what we do not use.</summary>
    private const string RequestedFields = "summary,description,status";

    private readonly JiraRequester requester = requester ?? JiraHttp.Requester;
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    public WorkItemProvider Provider => WorkItemProvider.Jira;

    /// <summary>The site this provider reads, for callers composing their own messages.</summary>
    public string Site => account.Site;

    public async Task<ImportedWorkItem> ImportAsync(
        WorkItemImportRequest request, CancellationToken cancellationToken)
    {
        JiraIssueKey key = JiraIssueKey.Parse(request.Reference, account.SiteUrl);
        return await ReadAsync(key, cancellationToken);
    }

    /// <summary>
    /// One card, read as itself rather than as a candidate for adoption. The adoption gate — only
    /// an item positively reported open (PLAN.md #60) — lives in <see cref="WorkItemImporter"/>
    /// and applies to importing, which is a decision about starting work. Linking is a different
    /// act: <c>h9k task link-jira</c> records the card an agent just created, and a board whose
    /// first workflow state is called something this platform maps to closed is a strange board,
    /// not a reason to refuse the link. So the verification path calls this directly and records
    /// whatever the card said.
    /// </summary>
    public async Task<ImportedWorkItem> ReadAsync(JiraIssueKey key, CancellationToken cancellationToken)
    {
        string authorization = await account.AuthorizationAsync(
            $"read {key.Value} at {account.Site}", cancellationToken);
        JiraResponse response = await SendAsync(
            new JiraRequest(
                HttpMethod.Get,
                account.Endpoint($"/rest/api/2/issue/{key.Value}?fields={RequestedFields}"),
                authorization),
            key.Value,
            "read",
            cancellationToken);

        return Map(response.Body, key, clock.GetUtcNow());
    }

    /// <summary>
    /// <c>jira:PROJ-123</c> points at <c>&lt;site&gt;/browse/PROJ-123</c>. A format rule rather
    /// than a lookup, and one that needs the registered site — which is exactly why a Jira
    /// reference cannot be turned into a link by the default importer and a GitHub one can.
    /// </summary>
    public Uri? WebUrl(ExternalReference reference) =>
        reference.Provider == WorkItemProvider.Jira
        && JiraIssueKey.TryParseBareKey(reference.Reference, out JiraIssueKey key)
            ? new Uri($"{account.Site}/browse/{key.Value}")
            : null;

    /// <summary>
    /// Prove the registered credentials actually work, and report who they are. Called at
    /// registration so a mistyped site, the account password pasted where an API token belongs,
    /// or a token that was revoked last month is refused while the human is still looking at the
    /// command that caused it — rather than surfacing weeks later inside a dispatched run.
    /// <para>
    /// It returns Jira's own display name for the account rather than echoing back what was
    /// typed, because the useful confirmation is the one that could have come out different.
    /// </para>
    /// </summary>
    public async Task<string> VerifyAccessAsync(CancellationToken cancellationToken)
    {
        string authorization = await account.AuthorizationAsync(
            $"sign in to {account.Site}", cancellationToken);
        JiraResponse response = await SendAsync(
            new JiraRequest(HttpMethod.Get, account.Endpoint("/rest/api/2/myself"), authorization),
            account.AccountEmail,
            // "as" rather than "to", because the subject every failure message interpolates after
            // the verb is the account here rather than a card, and the site is already named in
            // its own right by each of them. Origin incident (2026-08-21): with "sign in to", the
            // first command a new user runs failed with "could not sign in to brian@example.com".
            "sign in as",
            cancellationToken,
            subjectIsCard: false);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(response.Body);
        }
        catch (JsonException exception)
        {
            // A 2xx that is not JSON is a proxy or a portal answering for the tenant. The
            // credentials are unproven, so this reports the honest thing rather than success.
            throw NotASignedInUser(RelayedText.OneLine(exception.Message));
        }

        using (document)
        {
            // Parsing is not proof. A 2xx carrying perfectly good JSON that is not a user document
            // is the same unproven sign-in as a login page: an identity-aware proxy answering
            // {"error":"authentication required","login_url":"…"}, or an empty array, parses
            // cleanly, and every property this method wants is simply absent — so without this the
            // gate would fall through to the AccountEmail default and report a registration that
            // authenticated nothing. accountId is what makes it a user: Jira answers /myself with
            // it for every account, and it is the one field that cannot be a coincidence of some
            // other document's shape. Origin incident (2026-08-22): the pre-PR review of this
            // branch found the check passing on any parseable body, which is the failure this
            // whole call exists to catch, deferred to the middle of a dispatched session. Map()
            // guards its own document the same way and for the same reason.
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw NotASignedInUser(
                    $"the JSON it answered with is {document.RootElement.ValueKind}, not an object");
            }

            if (ReadString(document.RootElement, "accountId") is null)
            {
                throw NotASignedInUser("the JSON it answered with carries no accountId, so it is not a Jira account");
            }

            return ReadString(document.RootElement, "displayName") ?? account.AccountEmail;
        }
    }

    /// <summary>
    /// What a sign-in check that came back 2xx and proved nothing is refused with. It names what
    /// was actually seen, because the site URL is the usual cause and the reader is the person who
    /// just typed it.
    /// </summary>
    private DomainValidationException NotASignedInUser(string reported) => new(
        $"{account.Site} answered the sign-in check with something that is not a Jira account: {reported}. "
        + "That is usually a proxy or an SSO portal in front of the tenant: check the site URL is the "
        + "Jira address itself (https://your-org.atlassian.net).");

    private async Task<JiraResponse> SendAsync(
        JiraRequest request,
        string key,
        string verb,
        CancellationToken cancellationToken,
        bool subjectIsCard = true)
    {
        JiraResponse response;
        try
        {
            response = await requester(request, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient reports its own timeout this way. Separated from a real cancellation so
            // the message can say the site stopped answering rather than that somebody stopped it.
            throw new DomainValidationException(
                $"{account.Site} did not answer within {JiraHttp.Deadline.TotalSeconds:0} seconds while "
                + $"trying to {verb} {key}. Check the site is reachable from this machine (a VPN or a "
                + "proxy is the usual reason it is not from here but is from the browser) and try again.");
        }
        catch (HttpRequestException exception)
        {
            throw new DomainValidationException(
                $"Could not reach {account.Site} to {verb} {key}: {RelayedText.OneLine(exception.Message)}. "
                + "Check the site URL on the registered connection (h9k connection list) and that this "
                + "machine can reach it.");
        }

        return response.StatusCode is >= 200 and < 300
            ? response
            : throw Explain(response, key, verb, subjectIsCard);
    }

    /// <summary>
    /// Turn a failed call into the one sentence that says what to do next. Every branch names the
    /// site and the key, because a message that says only "401" leaves an agent — which is the
    /// caller this exists for — with nothing to reason from, and every branch quotes what Jira
    /// itself said, because the platform's guess about a permission is worth less than the
    /// tenant's own answer.
    /// <para>
    /// Jira's answers go through <see cref="RelayedText"/> on the way out. They contain text from
    /// the tenant (an error naming a field, a project, a card summary), this message is printed
    /// to a terminal, and a tenant is not a trusted author of escape sequences.
    /// </para>
    /// </summary>
    private DomainException Explain(JiraResponse response, string key, string verb, bool subjectIsCard)
    {
        string reported = Reported(response.Body);
        string suffix = reported.IsBlank() ? string.Empty : $" Jira reported: {reported}";

        return (HttpStatusCode)response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new DomainValidationException(
                $"{account.Site} rejected the credentials for {account.AccountEmail} while trying to "
                + $"{verb} {key}.{suffix} Jira Cloud wants an API token rather than an account password: "
                + "create one at https://id.atlassian.com/manage-profile/security/api-tokens and register "
                + "the connection again with h9k connection add jira. A token that used to work has "
                + "usually been revoked or expired."),

            HttpStatusCode.Forbidden => new DomainValidationException(
                $"{account.AccountEmail} is authenticated at {account.Site} but not allowed to {verb} "
                + $"{key}.{suffix} That is a permission on the Jira project rather than anything Hall9k "
                + "can change: check the account can do it in the browser, and note that Jira also "
                + "answers this way once it wants the account to solve a CAPTCHA after failed logins."),

            // A card that is not there and a card this account cannot see are the same answer from
            // Jira, deliberately, so both are named rather than one being picked. The wording is
            // written for the caller that matters most here: an agent that has just created a card
            // and is being told the key it reported does not resolve.
            HttpStatusCode.NotFound when subjectIsCard => new DomainNotFoundException(
                $"Could not find {key} at {account.Site} — check the key, or confirm which project it "
                + $"was created in.{suffix} Jira answers the same way for a card that does not exist and "
                + $"for one {account.AccountEmail} cannot see, so if the key is right, the account this "
                + "connection is registered as may simply not have access to that project."),

            HttpStatusCode.NotFound => new DomainNotFoundException(
                $"{account.Site} has no Jira API where Hall9k looked, so it could not {verb} "
                + $"{key}.{suffix} Check the site URL is the Jira address itself "
                + "(https://your-org.atlassian.net), not a board or a console link."),

            HttpStatusCode.TooManyRequests => new DomainValidationException(
                $"{account.Site} is rate-limiting this account and refused to {verb} {key}.{suffix} "
                + "Hall9k does not retry Jira calls on its own — a blind retry against a rate limit is "
                + "how one card ends up with four identical comments — so wait and run the command again."),

            >= HttpStatusCode.InternalServerError => new DomainValidationException(
                $"{account.Site} failed while trying to {verb} {key} (HTTP {response.StatusCode})."
                + $"{suffix} That is Jira's side rather than this machine's; try again once "
                + "https://status.atlassian.com is clear."),

            _ => new DomainValidationException(
                $"Jira refused to {verb} {key} at {account.Site} (HTTP {response.StatusCode}).{suffix}"),
        };
    }

    /// <summary>
    /// What Jira said, in the two shapes it says it: an <c>errorMessages</c> array for most
    /// failures and an <c>errors</c> object keyed by field for validation. Anything else is
    /// passed through as the body itself, bounded, rather than relabelled — the same discipline
    /// the GitHub provider applies to unrecognised stderr.
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
            // Jira answered with something that is not JSON at all, which is what a proxy or a
            // login page in front of the tenant looks like. Quoted as-is; it is the clue.
            return Bounded(body);
        }
    }

    /// <summary>
    /// Someone else's text, made safe and made short. Bounded because an HTML login page is a
    /// perfectly ordinary thing to get back from a proxy and pasting one into a terminal buries
    /// the sentence that says what to do about it.
    /// </summary>
    private static string Bounded(string text) => RelayedText.Truncate(RelayedText.OneLine(text).Trim(), 400);

    private ImportedWorkItem Map(string json, JiraIssueKey key, DateTimeOffset observedAt)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw NotACardDocument(key, RelayedText.OneLine(exception.Message));
        }

        using (document)
        {
            // A 2xx carrying JSON that is not an object is the same refusal as one carrying no JSON
            // at all, and it has to be said before a property is asked for: TryGetProperty throws
            // outright on an array or a bare string, so an SSO portal answering `[]` would leave as
            // an InvalidOperationException stack trace rather than the sentence that names the cause.
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw NotACardDocument(
                    key,
                    $"the JSON it answered with is {document.RootElement.ValueKind}, not an object");
            }

            // An object is not a card either, and the sign-in check one call up refuses for exactly
            // this reason. An identity-aware proxy answering {"error":"authentication required"}
            // parses cleanly as an object, and then every property below is simply absent, so a
            // card would be assembled out of defaults: the key that was asked for, an empty title,
            // and a status nobody read. That matters most on the path with no second gate behind
            // it. Import refuses anything not positively reported open, but h9k task link-jira
            // records whatever this returns, so the proxy's answer would go onto the task as a
            // verified card the platform never saw, which is the one thing that command exists to
            // prevent. `key` is what makes a document a card: Jira answers it for every issue, and
            // it cannot be a coincidence of some other document's shape. Origin incident
            // (2026-08-22): the pre-PR review of this branch found the guard stopping at the
            // object check, while the sign-in gate's comment already claimed this one covered the
            // same ground.
            if (ReadString(document.RootElement, "key") is not { } canonical)
            {
                throw NotACardDocument(key, "the JSON it answered with carries no key, so there is no card in it");
            }

            // The key the tenant answered with wins over the one that was asked for: Jira moves a
            // card between projects by giving it a new key and keeps the old one resolving, so the
            // canonical answer is the one that will still be right tomorrow. One that does not read
            // as a key is refused rather than quietly swapped back to the asked-for one: which card
            // this is, is precisely what the answer failed to establish.
            if (!JiraIssueKey.TryParseBareKey(canonical, out JiraIssueKey resolved))
            {
                throw NotACardDocument(
                    key, $"the key it answered with, '{Bounded(canonical)}', does not read as a Jira key");
            }

            // Every read asks for these fields by name, so a card always answers with the object.
            // A document without it can say neither what the card is called nor what state it is
            // in, which is the whole of what is being observed here.
            if (!document.RootElement.TryGetProperty("fields", out JsonElement fields)
                || fields.ValueKind != JsonValueKind.Object)
            {
                throw NotACardDocument(
                    key, "the JSON it answered with carries no fields, so there is nothing in it to read as a card");
            }

            return new ImportedWorkItem(
                resolved.Reference,
                ReadString(fields, "summary") ?? string.Empty,
                // A card with no description has none. Blank collapses to null so the agent context
                // says so rather than printing an empty section, and that is the only judgement made:
                // what is there is carried character for character, because the context contract
                // promises the agent the card as written.
                ReadString(fields, "description") is { } description && description.IsNotBlank()
                    ? description
                    : null,
                MapStatus(fields),
                new Uri($"{account.Site}/browse/{resolved.Value}"),
                observedAt);
        }
    }

    private DomainValidationException NotACardDocument(JiraIssueKey key, string reported) => new(
        $"{account.Site} answered for {key} with something that is not a Jira card: {reported}. If "
        + "the site URL points at a proxy or a login page rather than the Jira tenant, that is what "
        + "this looks like.");

    /// <summary>
    /// Jira's workflow vocabulary onto Hall9k's two states, at the boundary where the knowledge
    /// to do it honestly lives (decision #60 names this adapter as the place). The mapping reads
    /// <c>statusCategory</c> rather than the status name, and that is what makes it survive a
    /// board whose states are called "Ready for Ozzie" and "Shipped": every Jira status, however
    /// it is named, belongs to one of three built-in categories — <c>new</c>, <c>indeterminate</c>,
    /// <c>done</c> — and only the third means finished.
    /// <para>
    /// The status's own name rides along as the observed label, so the agent context stamps what
    /// the board actually said ("In Progress (open)") rather than replacing it with the platform's
    /// conclusion. A card whose category is missing or unrecognised keeps its name verbatim and
    /// maps to nothing, which the adoption gate refuses: nobody could say, so nobody guesses.
    /// </para>
    /// </summary>
    private static WorkItemStatus MapStatus(JsonElement fields)
    {
        if (fields.ValueKind != JsonValueKind.Object
            || !fields.TryGetProperty("status", out JsonElement status)
            || status.ValueKind != JsonValueKind.Object)
        {
            return WorkItemStatus.Unknown;
        }

        string name = ReadString(status, "name") ?? string.Empty;
        string? category = status.TryGetProperty("statusCategory", out JsonElement categoryElement)
            && categoryElement.ValueKind == JsonValueKind.Object
                ? ReadString(categoryElement, "key")
                : null;

        return category?.ToLowerInvariant() switch
        {
            "done" => WorkItemStatus.Closed.As(name),
            "new" or "indeterminate" => WorkItemStatus.Open.As(name),
            // Unmapped rather than Parse, because Parse recognises the words "open" and "closed"
            // and this arm has already established that nobody could say. Jira's classic default
            // workflow names a status "Open", so a card answered with no statusCategory — a
            // customised tenant, a proxy, a partial render — would otherwise be adopted as open
            // on the strength of a coincidence of vocabulary, which is precisely the guess the
            // doc above and decision #65 say is refused here. Origin incident (2026-08-22): the
            // second cycle of this branch's pre-PR review found the guard documented and absent.
            _ => WorkItemStatus.Unmapped(name),
        };
    }

    /// <summary>
    /// A string property, or null when it is absent, null, or some other kind. The kind is
    /// checked rather than assumed because <c>GetString()</c> throws on a number, and a tenant
    /// with a customised field type answering with one should produce a card with no summary,
    /// not a stack trace.
    /// </summary>
    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

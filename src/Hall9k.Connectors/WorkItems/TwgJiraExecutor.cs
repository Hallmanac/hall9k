using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// Why a twg call did not carry out the write, apart from what it actually said — the split
/// <see cref="TwgJiraExecutor"/> and the daemon's retry sweep both need: a missing binary and an
/// expired login are handled, expected states (Brian's design, 2026-08-28: the token expires
/// often enough that re-authentication is routine), and everything else is a write that needs a
/// different payload, not a different day. An in-process outcome, never persisted (AGENTS.md,
/// coding standards) — what a caller records is <see cref="TwgExecutionException.IsAuthFailure"/>
/// on <see cref="Domain.Features.Tasks.Events.JiraWriteFailed"/>, this enum's own reason text.
/// </summary>
public enum TwgFailureKind
{
    MissingBinary,
    AuthExpired,
    Other,
}

/// <summary>The one exception every <see cref="TwgJiraExecutor"/> call can throw, classified so a caller can tell an expected state from a real refusal.</summary>
public sealed class TwgExecutionException(TwgFailureKind kind, string message) : Exception(message)
{
    public TwgFailureKind Kind { get; } = kind;

    public bool IsAuthFailure => Kind == TwgFailureKind.AuthExpired;
}

/// <summary>What a create, an update, or a comment came back with once twg's own answer was read back and verified.</summary>
public sealed record TwgWriteResult(string IssueKey, string Summary);

/// <summary>The doctor's own reading of whether a write to Jira would go through right now.</summary>
public enum TwgAuthProbeResult
{
    Authenticated,
    MissingBinary,
    AuthExpired,
    Unknown,
}

/// <summary>
/// hall9k's sole path to writing Jira (Brian's design, 2026-08-28, superseding the
/// agent-mediated-only ruling): every create, update, and comment goes through the Atlassian CLI
/// (twg), never through an agent's own Jira access. Composition — the issue type, the fields, the
/// comment text — stays an agent's or an operator's judgment; this class is the deterministic,
/// audited half. It refuses nothing about a payload itself (that is
/// <see cref="JiraWritePayload.Validate"/>'s job, checked before this class is ever called) and
/// models nothing about a card's shape — it only shells out, reads twg's own JSON answer back,
/// and classifies a failure as an expected auth problem or a real one.
/// <para>
/// twg's exact CLI grammar comes from live <c>twg help</c> and <c>twg help describe</c> on the
/// machine it runs on: work items live under <c>twg jira workitem</c>
/// (<c>create</c>/<c>update</c>/<c>query</c>/<c>get</c>/<c>comment create</c>), never the bare
/// <c>twg jira create/update/search</c> an earlier version of this class assumed and which does
/// not exist on any installed twg (independent pre-PR review, cycle 1) — AGENTS.md's "never guess
/// at unobserved facts" is written for exactly this. <c>create</c> takes <c>--space</c> for the
/// project and first-class <c>--summary</c>/<c>--description</c>; <c>update</c> takes
/// <c>--id</c> rather than a positional key; <c>get</c> takes a positional issue key and is a
/// direct-by-key product-API read, unlike <c>query</c>'s JQL search against an index that updates
/// asynchronously — which is why every write's own read-back verification runs <c>get</c>, not
/// <c>query</c> (independent pre-PR review, cycle 6). <c>--output json</c> never prints raw JSON to
/// stdout by itself — every call still carries a YAML summary envelope naming a temp file that
/// holds the real payload, which is what <see cref="ExtractFirstKey"/> reads. Every call also
/// names <c>--site</c> explicitly and, wherever it carries a description or a comment body,
/// <c>--description-format</c>/<c>--body-format</c> — twg's own defaults for both (whatever
/// ambient tenant the machine resolves to, and HTML) are the wrong default for a payload composed
/// against a specific registered connection in whatever text a project's own card-authoring
/// skills produce, which is markdown more often than not (independent pre-PR review, cycle 2). If
/// a later twg version changes either shape, this file — and only this file — is where the
/// adjustment belongs; every caller above it speaks in <see cref="JiraWritePayload"/> and
/// <see cref="TwgWriteResult"/>, never in twg's own flags.
/// </para>
/// </summary>
public sealed class TwgJiraExecutor(ProcessRunner? runner = null, Uri? site = null)
{
    public const string Binary = "twg";

    /// <summary>
    /// A card carrying this exact text in its description is a card hall9k made for this task —
    /// the physical dedup gate (mirroring the GitHub read-back gate): searched for before every
    /// create, so a crash (or any failure) between twg creating a card and hall9k recording it
    /// narrows the window for a second card on a later attempt rather than closing it outright —
    /// <see cref="FindByMarkerAsync"/> runs a JQL search, and <see cref="VerifyAsync"/>'s own doc
    /// comment explains why that index updates asynchronously, so a retry inside the index-lag
    /// window can still find nothing even though the card genuinely exists (independent pre-PR
    /// review, conformance lens, cycle 9). Scoped to the task rather than to one write attempt's
    /// own guid: a fresh <c>SubmitAsync</c> mints a new write id every time, so a marker keyed to
    /// that guid could never be found by the very next attempt it exists to guard (independent
    /// pre-PR review, cycle 1) — the task is the identity that must not get a second card, so the
    /// task is what the marker names.
    /// </summary>
    public static string Marker(Guid taskId) => $"hall9k-task:{taskId:D}";

    /// <summary>
    /// A JQL clause valid on any tenant and guaranteed to match nothing, used to prove login works
    /// without touching any real card. A synthetic key (as opposed to a date far in the future)
    /// is rejected by Jira's own key-format validation before the search ever runs, which turns a
    /// healthy, authenticated install into "could not confirm" (independent pre-PR review, cycle
    /// 1) — this compares a real field against a value nothing will ever satisfy instead.
    /// </summary>
    private const string ProbeJql = "created > \"2999-01-01\"";

    private readonly ProcessRunner runner = runner ?? ExternalProcess.Runner;

    /// <summary>
    /// The tenant every call this executor makes is told to target explicitly, rather than left to
    /// whatever <c>twg</c>'s own ambient <c>auth.conf</c>/<c>TWG_SITE</c> ends up resolving to on the
    /// machine that runs it. <c>twg --help</c> documents <c>-s, --site &lt;site&gt;</c> as
    /// "auto-loaded" precisely because a bare install needs no flag at all — but a registered Jira
    /// connection (<c>h9k connection add jira --site …</c>) names a specific tenant for a reason,
    /// and without this, every write and its own read-back verification silently ran against the
    /// machine's ambient tenant instead, so a mismatch between the two would still read back as
    /// "verified" (independent pre-PR review, cycle 2). Null is the one honest exception: a caller
    /// with no resolvable connection (an install probing before one is registered) has nothing
    /// truer to hand this than twg's own ambient default.
    /// </summary>
    private readonly Uri? site = site;

    /// <summary>
    /// Caps how many of <see cref="FindByMarkerAsync"/>'s own search hits get their own
    /// confirming <c>get</c> call. <c>text ~</c> tokenizes the marker's own reserved characters
    /// apart (see that method's own doc comment), so a mature board can return a full page of
    /// loosely-matching candidates, and each confirmation is a synchronous twg call of its own —
    /// left unbounded, a create's dedup gate could cost enough sequential process spawns to run
    /// past <see cref="Daemon.DaemonOptions.PendingJiraWriteCeiling"/> in the worst case (that
    /// option lives in the daemon project this connector cannot reference, so the number here is
    /// duplicated rather than shared: 10 confirmations at <see cref="ExternalProcess.Deadline"/>
    /// each stays comfortably under that 30-minute ceiling). Passed to the search itself as
    /// <c>--limit</c> too (verified live against an installed twg: <c>jira workitem query</c>'s
    /// own <c>--limit</c>/<c>-n</c>, "alias for --first"), so the search does not even return more
    /// than this many candidates to begin with — the client-side cap on top is defense in depth
    /// against a query that ignores or exceeds it (independent pre-PR review, adversarial lens,
    /// cycle 3).
    /// </summary>
    private const int MaxMarkerSearchCandidates = 10;

    /// <summary>
    /// The physical half of the dedup gate: does a card already carry this task's marker. Called
    /// before every create, first attempt and every later one alike, so a create that twg
    /// completed but hall9k never recorded is found rather than duplicated. <c>text ~</c> is a
    /// Lucene text-analysis match with no <c>ORDER BY</c> guarantee, so the search can return
    /// several candidates with the actual marker-carrying card anywhere among them — every
    /// returned candidate, up to <see cref="MaxMarkerSearchCandidates"/>, is confirmed against its
    /// own description in search order (<see cref="CandidateCarriesMarkerAsync"/>) until one
    /// carries the marker, rather than trusting only whichever the search happened to rank first
    /// (independent pre-PR review, adversarial lens, cycle 1: a first-hit-only check let a
    /// token-overlapping card that sorted ahead of the real one mask an existing card and file a
    /// duplicate).
    /// <para>
    /// A page that comes back exactly full (<see cref="MaxMarkerSearchCandidates"/> keys, the most
    /// <c>--limit</c> would ever let through) is not trusted as an exhaustive negative even once
    /// every one of those candidates is confirmed clear: the query was capped at that many results,
    /// so the marker-carrying card itself could be sitting just past the cut, unseen and
    /// unconfirmed. This refuses there instead of returning null, the same "an unconfirmable check
    /// refuses rather than guesses" doctrine <see cref="CandidateCarriesMarkerAsync"/> already
    /// applies to a single unreadable candidate (independent pre-PR review, adversarial lens, cycle
    /// 4: a full page silently treated as exhaustive let a marker-carrying card ranked eleventh or
    /// later go unconfirmed and file a duplicate).
    /// </para>
    /// </summary>
    public async Task<string?> FindByMarkerAsync(Guid taskId, string workingDirectory, CancellationToken cancellationToken)
    {
        ProcessResult result = await RunAsync(
            ["jira", "workitem", "query", "--jql", $"text ~ \"{Marker(taskId)}\"", "--limit",
             MaxMarkerSearchCandidates.ToString(), "--output", "json", "--output-summary", "stats"],
            workingDirectory, cancellationToken);
        IReadOnlyList<string> candidates = ExtractAllKeys(result.StandardOutput, out bool confirmedReadable);
        if (!confirmedReadable)
        {
            throw new TwgExecutionException(
                TwgFailureKind.Other,
                $"The marker search for {Marker(taskId)} could not be read back to confirm — the temp "
                + "file twg wrote its answer to was reaped or unreadable before this check could run. "
                + "Refusing to create on an unconfirmed dedup check; check the board by hand for "
                + $"{Marker(taskId)} and, if it is not there, run the write again.");
        }

        foreach (string candidate in candidates.Take(MaxMarkerSearchCandidates))
        {
            if (await CandidateCarriesMarkerAsync(candidate, taskId, workingDirectory, cancellationToken))
            {
                return candidate;
            }
        }

        if (candidates.Count >= MaxMarkerSearchCandidates)
        {
            throw new TwgExecutionException(
                TwgFailureKind.Other,
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
    /// <c>text ~</c> is Jira's CONTAINS operator, evaluated through Lucene text analysis rather
    /// than as an equality check, and the marker's own reserved characters (<c>:</c>, <c>-</c>)
    /// are tokenized apart rather than matched whole — so a search for this task's marker can
    /// return a different card whose tokens merely overlap, and nothing about the search itself
    /// proves the candidate actually carries this task's marker (independent pre-PR review,
    /// conformance and adversarial lenses, cycle 1). This reads the candidate's own description
    /// directly, by key, and confirms the exact marker text is present in it before
    /// <see cref="FindByMarkerAsync"/> is allowed to trust the match — the same "prove it, do not
    /// infer it" discipline <see cref="VerifyAsync"/> already applies to a write's own success.
    /// A plain substring check against the raw answer is enough: the description comes back as
    /// Atlassian Document Format (a JSON tree, not plain text), but the marker's own characters
    /// need no JSON escaping, so it survives unchanged as a "text" node's value and a substring
    /// check against the whole payload finds it without needing to walk that tree.
    /// <para>
    /// A payload that could not be confirmed read — twg's own temp file reaped or unreadable
    /// between the call and this check — throws rather than reads as "no marker": trusting the raw
    /// envelope text in its place would silently fail toward duplication, since the envelope never
    /// contains the description the marker lives in, the opposite of what this dedup gate exists
    /// to prevent (independent pre-PR review, adversarial lens, cycle 3).
    /// </para>
    /// </summary>
    private async Task<bool> CandidateCarriesMarkerAsync(
        string candidateKey, Guid taskId, string workingDirectory, CancellationToken cancellationToken)
    {
        ProcessResult result = await RunAsync(
            ["jira", "workitem", "get", candidateKey, "--fields", "description", "--output", "json", "--output-summary", "stats"],
            workingDirectory, cancellationToken);
        string payload = ReadPayloadJson(result.StandardOutput, out bool confirmedReadable);
        if (!confirmedReadable)
        {
            throw new TwgExecutionException(
                TwgFailureKind.Other,
                $"twg found a card ({candidateKey}) that may already carry this task's marker, but its "
                + "description could not be read back to confirm it — the temp file twg wrote it to was "
                + "reaped or unreadable before this check could run. Refusing to create a second card on "
                + $"an unconfirmed dedup check; check the board by hand for {Marker(taskId)} and, if it "
                + "is not there, run the write again.");
        }

        return payload.Contains(Marker(taskId), StringComparison.Ordinal);
    }

    /// <summary>
    /// One card, authored from a composed payload, then read back and verified — never trusted on
    /// twg's own create answer alone, the same discipline
    /// <see cref="GitHubWorkItemProvider.CreateAsync"/> applies to <c>gh issue create</c>.
    /// </summary>
    public async Task<TwgWriteResult> CreateAsync(
        JiraProjectKey project, JiraWritePayload payload, Guid taskId, string workingDirectory, CancellationToken cancellationToken)
    {
        if (!project.HasValue)
        {
            throw new TwgExecutionException(
                TwgFailureKind.Other,
                "No Jira board is bound and the payload named none either. Compose the payload with "
                + "\"projectKey\", or bind one with h9k project set --jira.");
        }

        Dictionary<string, string> fields = payload.Fields is null
            ? []
            : new Dictionary<string, string>(payload.Fields);
        string? summary = ExtractField(fields, "summary");
        string description = AppendMarker(ExtractField(fields, "description"), Marker(taskId));

        List<string> arguments =
            ["jira", "workitem", "create", "--space", project.Value, "--type", payload.WorkItemType ?? string.Empty,
             "--description", description, "--description-format", payload.EffectiveFormat,
             "--output", "json", "--output-summary", "stats"];
        if (summary.IsNotBlank())
        {
            arguments.Add("--summary");
            arguments.Add(summary);
        }

        AppendFields(arguments, fields);

        ProcessResult result = await RunAsync(arguments, workingDirectory, cancellationToken);
        string key = ExtractFirstKey(result.StandardOutput) is { } extracted && extracted.IsNotBlank()
            ? extracted
            : throw new TwgExecutionException(
                TwgFailureKind.Other,
                "twg jira workitem create exited successfully but printed no card key, so nothing here can "
                + $"be verified: {Head(result.StandardOutput)}");

        return await VerifyAsync(key, workingDirectory, cancellationToken, "created", confirmsExistenceOnly: false);
    }

    /// <summary>
    /// An existing card's fields, updated and then read back to confirm it is still there. The
    /// read-back this executor can run — a search on the key — only ever proves the card exists,
    /// which is already true before an update runs, so the recorded outcome says exactly that
    /// rather than claiming the changed fields themselves were confirmed (independent pre-PR
    /// review, cycle 1): twg's own exit code is what an update is actually trusted on.
    /// </summary>
    public async Task<TwgWriteResult> UpdateAsync(
        string issueKey, JiraWritePayload payload, string workingDirectory, CancellationToken cancellationToken)
    {
        Dictionary<string, string> fields = payload.Fields is null
            ? []
            : new Dictionary<string, string>(payload.Fields);
        string? summary = ExtractField(fields, "summary");
        string? description = ExtractField(fields, "description");

        List<string> arguments = ["jira", "workitem", "update", "--id", issueKey, "--output", "json", "--output-summary", "stats"];
        if (summary.IsNotBlank())
        {
            arguments.Add("--summary");
            arguments.Add(summary);
        }

        if (description.IsNotBlank())
        {
            arguments.Add("--description");
            arguments.Add(description);
            arguments.Add("--description-format");
            arguments.Add(payload.EffectiveFormat);
        }

        AppendFields(arguments, fields);
        await RunAsync(arguments, workingDirectory, cancellationToken);
        return await VerifyAsync(issueKey, workingDirectory, cancellationToken, "updated", confirmsExistenceOnly: true);
    }

    /// <summary>
    /// A direct-by-key read-back with no write attempted first — for a caller that already
    /// believes a card exists (a stuck create's own retry, finding the task linked to one some
    /// other route already recorded, such as an operator's own <c>h9k task link-jira</c>) and owes
    /// its own recorded outcome the identical "prove it, do not infer it" confirmation every
    /// write's success gets here, rather than recording that belief unread (independent pre-PR
    /// review, adversarial lens, cycle 3: <c>JiraWriteSucceeded.IssueKey</c>'s own doc comment
    /// says plainly it is what Jira answered when read back, never what another action once
    /// claimed).
    /// </summary>
    public Task<TwgWriteResult> VerifyExistsAsync(string issueKey, string workingDirectory, CancellationToken cancellationToken) =>
        VerifyAsync(issueKey, workingDirectory, cancellationToken, "linked", confirmsExistenceOnly: true);

    /// <summary>
    /// A comment on an existing card — never a transition, never a close, exactly the closeout
    /// write this surface exists to carry. Read back the same way <see cref="UpdateAsync"/> is:
    /// existence only, not the comment text itself.
    /// <para>
    /// Unlike a create, a comment has no dedup gate: <c>twg</c> exposes no way to list or search a
    /// card's own comments, so nothing here can tell an earlier attempt's comment apart from a
    /// fresh one before posting. An auth failure from the write call itself is safe to report as
    /// the ordinary pending-authentication state — nothing happened yet, so the retry sweep's next
    /// attempt starts clean — but an auth failure from the read-back below is not: the comment has
    /// already landed by that point, and reporting it the same way would leave the pending write
    /// standing for the retry sweep to post the identical comment a second time once <c>twg
    /// login</c> succeeds (independent pre-PR review, adversarial lens, cycle 3). Reclassified here
    /// as an ordinary (non-auth) failure instead, which clears the pending marker rather than
    /// retrying automatically, and says so plainly: an operator has to look at the board before
    /// deciding whether this needs resubmitting.
    /// </para>
    /// </summary>
    public async Task<TwgWriteResult> CommentAsync(
        string issueKey, string comment, string format, string workingDirectory, CancellationToken cancellationToken)
    {
        List<string> arguments =
            ["jira", "workitem", "comment", "create", "--issue-id", issueKey, "--body", comment,
             "--body-format", format, "--output", "json", "--output-summary", "stats"];
        await RunAsync(arguments, workingDirectory, cancellationToken);
        try
        {
            return await VerifyAsync(issueKey, workingDirectory, cancellationToken, "commented on", confirmsExistenceOnly: true);
        }
        catch (TwgExecutionException exception) when (exception.Kind == TwgFailureKind.AuthExpired)
        {
            throw new TwgExecutionException(
                TwgFailureKind.Other,
                $"twg posted the comment on {issueKey}, but reading it back to verify hit an expired or "
                + "missing twg login rather than any other failure. This is not reported as the ordinary "
                + "pending-authentication state, because the comment already landed: retrying it once "
                + "'twg login' succeeds would post the identical comment a second time, since a comment "
                + "carries no marker a later attempt could use to tell it apart from a fresh one. Check "
                + $"{issueKey} before resubmitting.");
        }
    }

    /// <summary>
    /// twg's own claim, read back through a fresh, direct-by-key read rather than trusted — the
    /// verified read-back every acceptance criterion for this feature names explicitly. Meaningful
    /// proof for a create, whose whole claim is that the card now exists; for an update or a
    /// comment the identical read can only re-confirm a fact already true before the write ran, so
    /// <paramref name="confirmsExistenceOnly"/> keeps the recorded summary honest about which of
    /// the two it actually is.
    /// <para>
    /// <c>jira workitem get</c>, not <c>jira workitem query</c>: a query is a JQL search against
    /// Jira's own search index, which updates asynchronously, so a search run milliseconds after a
    /// create can find nothing even though the card genuinely exists — misreading a real success as
    /// a failure and recording a terminal <c>JiraWriteFailed</c> for a card that is actually sitting
    /// on the board (independent pre-PR review, cycle 6). <c>get</c> is documented as a direct
    /// product-API read by issue key, with no index in between.
    /// </para>
    /// </summary>
    private async Task<TwgWriteResult> VerifyAsync(
        string issueKey, string workingDirectory, CancellationToken cancellationToken, string verb,
        bool confirmsExistenceOnly)
    {
        ProcessResult result = await RunAsync(
            ["jira", "workitem", "get", issueKey, "--output", "json", "--output-summary", "stats"],
            workingDirectory, cancellationToken);
        string? found = ExtractFirstKey(result.StandardOutput);
        if (found is null)
        {
            throw new TwgExecutionException(
                TwgFailureKind.Other,
                $"twg reported {issueKey} {verb}, but reading it back to verify found nothing. The write "
                + "was not recorded — check the board before writing again.");
        }

        return new TwgWriteResult(
            found,
            confirmsExistenceOnly
                ? $"twg reported {found} {verb}. The read-back confirms the card still exists; it does not "
                    + "re-read the changed field or comment content, so that part is trusted to twg's own exit code."
                : $"twg reported {found} {verb} and it read back successfully.");
    }

    /// <summary>
    /// A read-only probe for <c>h9k doctor</c>: does an authenticated Jira search go through right
    /// now, with no card touched either way. Distinguishes a missing binary from an expired login
    /// so the fix taught is the right one.
    /// </summary>
    public async Task<TwgAuthProbeResult> ProbeAuthenticationAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            await RunAsync(
                ["jira", "workitem", "query", "--jql", ProbeJql, "--output", "json", "--output-summary", "stats"],
                workingDirectory, cancellationToken);
            return TwgAuthProbeResult.Authenticated;
        }
        catch (TwgExecutionException exception) when (exception.Kind == TwgFailureKind.MissingBinary)
        {
            return TwgAuthProbeResult.MissingBinary;
        }
        catch (TwgExecutionException exception) when (exception.Kind == TwgFailureKind.AuthExpired)
        {
            return TwgAuthProbeResult.AuthExpired;
        }
        catch (TwgExecutionException)
        {
            return TwgAuthProbeResult.Unknown;
        }
    }

    private static void AppendFields(List<string> arguments, IReadOnlyDictionary<string, string> fields)
    {
        foreach ((string name, string value) in fields)
        {
            arguments.Add("--field");
            arguments.Add($"{name}={value}");
        }
    }

    /// <summary>
    /// Pull a first-class field (<c>summary</c>, <c>description</c>) out of a composed payload's
    /// fields, case-insensitively — a composing agent's own casing choice ("Description" as well
    /// as "description") should not survive into a second, marker-only <c>--field</c> alongside
    /// twg's own <c>--summary</c>/<c>--description</c> flags for the same thing (independent
    /// pre-PR review, cycle 1). Removes whichever casing it found so <see cref="AppendFields"/>
    /// never sees it again.
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
    /// Unwraps a field's own raw JSON text (<see cref="JiraWritePayload.FromJson"/> keeps a
    /// composed field's exact JSON — quotes and all — so a custom field forced as a string
    /// survives, unmangled, to twg's own JSON-coercing <c>--field</c>) back to plain content for
    /// <c>--summary</c>/<c>--description</c>, which are ordinary text flags twg never re-parses as
    /// JSON. A value that is not itself valid JSON — a payload built directly rather than through
    /// <see cref="JiraWritePayload.FromJson"/>, this file's own tests among them — passes through
    /// unchanged, the plain text it always was.
    /// </summary>
    private static string DecodeFieldText(string value)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.String
                ? document.RootElement.GetString() ?? value
                : value;
        }
        catch (JsonException)
        {
            return value;
        }
    }

    /// <summary>
    /// The marker appended to whatever description a composed payload already carries, rather
    /// than replacing it: the description is what the card's audience reads, and the marker only
    /// has to be findable by a search, not visible at the top.
    /// </summary>
    private static string AppendMarker(string? description, string marker) =>
        description.IsNotBlank() ? $"{description}\n\n[{marker}]" : $"[{marker}]";

    private async Task<ProcessResult> RunAsync(
        IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        // --site is a root-level twg option, accepted interspersed with a subcommand's own flags
        // (verified against an installed twg), so appending it here rather than threading it
        // through every argument list above is exactly the same "one file, one adjustment" seam
        // this class already keeps for twg's own grammar.
        IReadOnlyList<string> withSite = site is null ? arguments : [.. arguments, "--site", site.Host];

        ProcessResult result;
        try
        {
            result = await runner(Binary, withSite, workingDirectory, cancellationToken);
        }
        // A long composed description or comment can push the whole command line (every field is
        // passed inline, per twg's own grammar) over the OS's own limit — Windows'
        // ERROR_FILENAME_EXCED_RANGE (206) or POSIX's E2BIG (7, reported identically on Linux and
        // macOS) — and .NET reports that refused spawn as the same Win32Exception a missing binary
        // throws. Left unclassified, that misdiagnoses an installed, authenticated twg as not
        // installed at all, contradicting h9k doctor's own probe (whose short command line never
        // hits this), the exact trap GitHubWorkItemProvider.CreateAsync already sidesteps with a
        // body file for gh (independent pre-PR review, cycle 5).
        catch (Win32Exception exception) when (exception.NativeErrorCode is 206 or 7)
        {
            throw new TwgExecutionException(
                TwgFailureKind.Other,
                $"Could not run twg: the command line was too long ({exception.Message}). This is a long "
                + "description or comment on the write, not a missing install — twg is installed; "
                + "shorten the payload and try again.");
        }
        catch (Win32Exception exception)
        {
            throw new TwgExecutionException(
                TwgFailureKind.MissingBinary,
                $"Could not run twg, the Atlassian CLI hall9k writes Jira through: {exception.Message}. "
                + "Install it and run twg login, then try again.");
        }
        // twg itself exited — with an observed exit code — and something it started kept holding
        // its output pipe open past the drain grace, so the answer was never read. Checked ahead
        // of the plain TimeoutException below, which this is one of and would otherwise catch it
        // first: that handler exists for "did this even run", and an exit code proves it did.
        // Exit 0 is the dangerous half — the write itself was very likely carried out even though
        // its answer could not be — so the message says that plainly rather than the generic "did
        // not answer", the same distinction GitHubWorkItemProvider.RunGhAsync already draws
        // (independent pre-PR review, cycle 1).
        catch (ProcessOutputStuckException exception) when (exception.ExitCode == 0)
        {
            throw new TwgExecutionException(
                TwgFailureKind.Other,
                $"{exception.Message} twg reported success before its output got stuck, so this write was "
                + "very likely carried out even though its own answer could not be read back. Do not "
                + "assume nothing happened: for a create, run the same write again — the marker search "
                + "this executor runs first will find the card if it exists rather than filing a second "
                + "one; for an update or a comment, check the board before retrying.");
        }
        catch (ProcessOutputStuckException exception)
        {
            throw new TwgExecutionException(
                TwgFailureKind.Other,
                $"{exception.Message} twg reported failure, so nothing here was carried out; run the same "
                + "command by hand to see what it is waiting on.");
        }
        catch (TimeoutException exception)
        {
            throw new TwgExecutionException(
                TwgFailureKind.Other,
                $"{exception.Message} twg did not answer; run the same command by hand to see what it "
                + "is waiting on.");
        }

        if (result.ExitCode == 0)
        {
            return result;
        }

        throw Explain(result);
    }

    private const int AuthRequiredExitCode = 77;

    /// <summary>
    /// twg reports an expired or missing login through its own JSON error envelope on stdout, exit
    /// code 77, <c>error.code: "AUTH_REQUIRED"</c>, with stderr left empty — verified live against
    /// an installed twg (independent pre-PR review, adversarial lens, cycle 1): an ordinary runtime
    /// failure unrelated to auth (a bad site, an unknown issue key) exits **1** with
    /// <c>error.code: "TWG_COMMAND_FAILED"</c> in the same envelope shape, not 77. None of the
    /// earlier stderr substring checks could ever match an auth failure, because the answer was
    /// never on that stream. The envelope is read
    /// with the same <see cref="ReadPayloadJson"/>/<see cref="StdoutFilePathPattern"/> machinery
    /// <see cref="ExtractFirstKey"/> already uses for a success answer; stderr stays a fallback for
    /// whatever this class has not seen twg do yet (a spawn-level refusal outside twg's own
    /// control, for one). Whichever stream answered, the text is twg's and Jira's rather than
    /// ours — it routinely quotes a composed field value or a card's own adopted text back — so it
    /// goes through <see cref="RelayedText.OneLine"/> before it reaches an exception message the
    /// CLI writes straight to stderr and the coordinator records as a failure reason.
    /// </summary>
    private static TwgExecutionException Explain(ProcessResult result)
    {
        (string? code, string? message) = ReadErrorEnvelope(result.StandardOutput);
        string reported = RelayedText.OneLine(message.IsNotBlank() ? message : result.StandardError).Trim();

        bool authExpired = result.ExitCode == AuthRequiredExitCode
            || string.Equals(code, "AUTH_REQUIRED", StringComparison.OrdinalIgnoreCase)
            || reported.Contains("twg login", StringComparison.OrdinalIgnoreCase)
            || reported.Contains("not authenticated", StringComparison.OrdinalIgnoreCase)
            || reported.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || reported.Contains("authentication required", StringComparison.OrdinalIgnoreCase)
            // Deliberately no bare "401" substring check: a digit sequence appears in plenty of
            // ordinary refusals that have nothing to do with authentication — an issue key
            // (PROJ-401), a custom field id (customfield_10401) — and matching on it converted a
            // permanent refusal into a write retried forever (independent pre-PR review, cycle 1).
            || reported.Contains("token", StringComparison.OrdinalIgnoreCase)
                && reported.Contains("expired", StringComparison.OrdinalIgnoreCase);

        string detail = reported.IsNotBlank() ? reported : Head(result.StandardOutput);

        return authExpired
            ? new TwgExecutionException(
                TwgFailureKind.AuthExpired,
                $"twg is not authenticated (its login expires periodically): {detail}. Run 'twg login' "
                + "in your own terminal — it is a browser-based login twg cannot do unattended — and this "
                + "write will retry automatically once it succeeds.")
            : new TwgExecutionException(TwgFailureKind.Other, $"twg refused the write: {detail}");
    }

    /// <summary>
    /// The <c>error.code</c>/<c>error.message</c> a failing twg call's own envelope carries,
    /// tolerant of there being none at all (a spawn-level refusal never reaches twg's own JSON
    /// output, so it has nothing to read here and falls back to stderr in <see cref="Explain"/>).
    /// </summary>
    private static (string? Code, string? Message) ReadErrorEnvelope(string standardOutput)
    {
        string json = ReadPayloadJson(standardOutput);
        if (json.IsBlank())
        {
            return (null, null);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return (null, null);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out JsonElement error)
                || error.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            string? code = error.TryGetProperty("code", out JsonElement codeElement)
                && codeElement.ValueKind == JsonValueKind.String
                ? codeElement.GetString()
                : null;
            string? message = error.TryGetProperty("message", out JsonElement messageElement)
                && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString()
                : null;
            return (code, message);
        }
    }

    /// <summary>
    /// twg's own YAML summary envelope names the file its real JSON payload was written to
    /// (<c>output_files: stdout: "&lt;path&gt;"</c>) — <c>--output json</c> alone never prints
    /// pure JSON to stdout, whatever <c>--output-summary</c> level is chosen (independent pre-PR
    /// review, cycle 1, verified against an installed, authenticated twg). Bare JSON on stdout is
    /// still tolerated as a fallback in case a later twg version drops the envelope.
    /// </summary>
    private static readonly Regex StdoutFilePathPattern = new(
        """^\s*stdout:\s*"(?<path>[^"]+)"\s*$""", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// The key twg's own answer carries, tolerant of the shapes actually observed: a query's
    /// <c>data.issues[]</c>, a single-key get's own <c>data[]</c> (verified against the installed
    /// binary directly, independent pre-PR review, cycle 7), a batch get's <c>data.items[].data</c>,
    /// or a create's own <c>data.issue</c> — the first entity found with a "key" property, searched
    /// in that order of directness rather than assumed to be any one of them.
    /// </summary>
    private static string? ExtractFirstKey(string envelopeOutput)
    {
        string json = ReadPayloadJson(envelopeOutput);
        if (json.IsBlank())
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            JsonElement data = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("data", out JsonElement dataElement)
                ? dataElement
                : document.RootElement;
            return FindEntity(data) is { } entity && entity.TryGetProperty("key", out JsonElement key)
                ? key.GetString()
                : null;
        }
    }

    /// <summary>
    /// Every candidate key a query's answer carries, in the order twg's own search returned them —
    /// the array <see cref="FindEntity"/> would otherwise only look inside for its first element
    /// (<see cref="FindArray"/> locates that same <c>data.issues[]</c> array without descending
    /// into it), used by <see cref="FindByMarkerAsync"/> so a marker search with several hits gets
    /// every one confirmed rather than only whichever twg happened to rank first.
    /// <paramref name="confirmedReadable"/> is <see cref="FindByMarkerAsync"/>'s own dedup-gate
    /// signal, the same one <see cref="CandidateCarriesMarkerAsync"/> already reads: an unreadable
    /// search answer must not fall back to an empty candidate list, which the dedup gate would
    /// otherwise read as "no card carries this marker" and create a duplicate (independent pre-PR
    /// review, conformance lens, cycle 8).
    /// </summary>
    private static IReadOnlyList<string> ExtractAllKeys(string envelopeOutput, out bool confirmedReadable)
    {
        string json = ReadPayloadJson(envelopeOutput, out confirmedReadable);
        if (json.IsBlank())
        {
            return [];
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            JsonElement data = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("data", out JsonElement dataElement)
                ? dataElement
                : document.RootElement;
            if (FindArray(data) is not { } issues)
            {
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

            return keys;
        }
    }

    /// <summary>
    /// The array <see cref="ExtractAllKeys"/> needs whole, found the same way
    /// <see cref="FindEntity"/> locates its first element — itself, if it is already an array; its
    /// first array-valued property that itself carries a match (<c>data.issues</c>); or, failing
    /// that, its first object-valued property searched the same way — but returned intact rather
    /// than descended into, so every element survives for the caller to walk. An array-valued
    /// property that carries no "key"-bearing element (an empty <c>errors</c> or <c>warnings</c>
    /// list ahead of <c>issues</c> in the envelope's own property order) is skipped rather than
    /// returned, the same way <see cref="FindEntity"/> keeps looking past an array whose own search
    /// turns up nothing (independent pre-PR review, adversarial lens, cycle 5).
    /// </summary>
    private static JsonElement? FindArray(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            return value;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Array && HasKeyElement(property.Value))
                {
                    return property.Value;
                }
            }

            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object && FindArray(property.Value) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Whether any element of <paramref name="array"/> is an object carrying a "key" string —
    /// the same shape <see cref="ExtractAllKeys"/> itself keeps, used by <see cref="FindArray"/>
    /// to tell the array worth returning from one that just happens to sit ahead of it.
    /// </summary>
    private static bool HasKeyElement(JsonElement array)
    {
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("key", out JsonElement key) && key.ValueKind == JsonValueKind.String)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The temp file twg's envelope names may vanish or become unreadable between the
    /// <see cref="File.Exists"/> check and the read — it lives in the system temp tree, which is
    /// subject to reaping — and an I/O failure here must not escape as a raw <see cref="IOException"/>:
    /// every caller of <see cref="ReadPayloadJson"/> sits inside this class's own failure
    /// classification (<see cref="Explain"/>, <see cref="ExtractFirstKey"/>), so an unguarded
    /// throw would bypass <see cref="TwgFailureKind"/> entirely — an auth refusal whose temp file
    /// happened to vanish would otherwise surface as an unclassified exception instead of staying
    /// pending for the retry sweep (independent pre-PR review, adversarial lens, cycle 9). Falling
    /// back to the envelope text itself is the same fallback an outright missing file already gets.
    /// </summary>
    private static string ReadPayloadJson(string envelopeOutput) => ReadPayloadJson(envelopeOutput, out _);

    /// <summary>
    /// The <paramref name="confirmedReadable"/> overload every caller above still ignores except
    /// <see cref="CandidateCarriesMarkerAsync"/> and <see cref="ExtractAllKeys"/>: everywhere else,
    /// a fallback to the raw envelope already fails toward a refusal on its own (it will not parse
    /// as the expected JSON shape, so the caller's own "found nothing" handling takes over), but
    /// the marker search's dedup gate reads "found nothing" as the affirmative permission to
    /// create — so a silent fallback there would read as "no card carries the marker" instead of
    /// "the search's own answer could not be confirmed", the false negative this flag exists to
    /// catch (independent pre-PR review, conformance lens, cycle 8).
    /// </summary>
    private static string ReadPayloadJson(string envelopeOutput, out bool confirmedReadable)
    {
        Match match = StdoutFilePathPattern.Match(envelopeOutput);
        if (!match.Success)
        {
            // No temp file named at all — the bare-JSON-on-stdout fallback this class already
            // tolerates for a future twg version, not a read failure, so envelopeOutput is the
            // whole answer and is trusted as such.
            confirmedReadable = true;
            return envelopeOutput;
        }

        if (File.Exists(match.Groups["path"].Value))
        {
            try
            {
                confirmedReadable = true;
                return File.ReadAllText(match.Groups["path"].Value);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        confirmedReadable = false;
        return envelopeOutput;
    }

    /// <summary>
    /// Depth-first for the first object carrying a "key" string: itself, its first element if it
    /// is an array, its first array-valued property (twg's own <c>data.issues</c> shape), or — a
    /// create's own <c>data.issue.key</c>, verified against the installed binary's create
    /// implementation (independent pre-PR review, cycle 6) — its first object-valued property,
    /// tried only once no array-valued property has already produced a match. Deliberately not a
    /// fully recursive scan of every nested field, which would just as readily return a parent
    /// issue's own "key" nested two levels down inside a subtask's answer.
    /// </summary>
    private static JsonElement? FindEntity(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("key", out JsonElement key) && key.ValueKind == JsonValueKind.String)
        {
            return value;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.GetArrayLength() > 0 ? FindEntity(value[0]) : null;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Array && FindEntity(property.Value) is { } found)
                {
                    return found;
                }
            }

            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object && FindEntity(property.Value) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static string Head(string output)
    {
        string trimmed = RelayedText.OneLine(output).Trim();
        return trimmed.IsBlank() ? "nothing at all" : RelayedText.Truncate(trimmed, 200);
    }
}

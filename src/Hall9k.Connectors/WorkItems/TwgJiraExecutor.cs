using System.ComponentModel;
using System.Text.Json;
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
/// twg's exact CLI grammar comes from live <c>twg help</c> on the machine it runs on; this class
/// assumes the shape the task that built it described: <c>twg jira create/update/search</c> with
/// <c>--output json</c>, a comment carried as an update's own field, and login state read from
/// whether an ordinary read call succeeds. If a later twg version changes that shape, this file —
/// and only this file — is where the adjustment belongs; every caller above it speaks in
/// <see cref="JiraWritePayload"/> and <see cref="TwgWriteResult"/>, never in twg's own flags.
/// </para>
/// </summary>
public sealed class TwgJiraExecutor(ProcessRunner? runner = null)
{
    public const string Binary = "twg";

    /// <summary>
    /// A card carrying this exact text in its description is a card hall9k made for this task —
    /// the physical dedup gate (mirroring the GitHub read-back gate): searched for before every
    /// create, so a crash (or any failure) between twg creating a card and hall9k recording it
    /// cannot produce a second card on a later attempt. Scoped to the task rather than to one
    /// write attempt's own guid: a fresh <c>SubmitAsync</c> mints a new write id every time, so a
    /// marker keyed to that guid could never be found by the very next attempt it exists to guard
    /// (independent pre-PR review, cycle 1) — the task is the identity that must not get a second
    /// card, so the task is what the marker names.
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
    /// The physical half of the dedup gate: does a card already carry this task's marker. Called
    /// before every create, first attempt and every later one alike, so a create that twg
    /// completed but hall9k never recorded is found rather than duplicated.
    /// </summary>
    public async Task<string?> FindByMarkerAsync(Guid taskId, string workingDirectory, CancellationToken cancellationToken)
    {
        ProcessResult result = await RunAsync(
            ["jira", "search", "--jql", $"text ~ \"{Marker(taskId)}\"", "--output", "json"],
            workingDirectory, cancellationToken);
        return ExtractFirstKey(result.StandardOutput);
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
        fields["description"] = AppendMarker(fields.GetValueOrDefault("description"), Marker(taskId));

        List<string> arguments =
            ["jira", "create", "--project", project.Value, "--type", payload.WorkItemType ?? string.Empty, "--output", "json"];
        AppendFields(arguments, fields);

        ProcessResult result = await RunAsync(arguments, workingDirectory, cancellationToken);
        string key = ExtractFirstKey(result.StandardOutput)
            ?? throw new TwgExecutionException(
                TwgFailureKind.Other,
                "twg jira create exited successfully but printed no card key, so nothing here can be "
                + $"verified: {Head(result.StandardOutput)}");

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
        List<string> arguments = ["jira", "update", issueKey, "--output", "json"];
        AppendFields(arguments, payload.Fields ?? new Dictionary<string, string>());
        await RunAsync(arguments, workingDirectory, cancellationToken);
        return await VerifyAsync(issueKey, workingDirectory, cancellationToken, "updated", confirmsExistenceOnly: true);
    }

    /// <summary>
    /// A comment on an existing card — never a transition, never a close, exactly the closeout
    /// write this surface exists to carry. Read back the same way <see cref="UpdateAsync"/> is:
    /// existence only, not the comment text itself.
    /// </summary>
    public async Task<TwgWriteResult> CommentAsync(
        string issueKey, string comment, string workingDirectory, CancellationToken cancellationToken)
    {
        List<string> arguments = ["jira", "update", issueKey, "--comment", comment, "--output", "json"];
        await RunAsync(arguments, workingDirectory, cancellationToken);
        return await VerifyAsync(issueKey, workingDirectory, cancellationToken, "commented on", confirmsExistenceOnly: true);
    }

    /// <summary>
    /// twg's own claim, read back through a fresh search rather than trusted — the verified
    /// read-back every acceptance criterion for this feature names explicitly. Meaningful proof
    /// for a create, whose whole claim is that the card now exists; for an update or a comment
    /// the identical search can only re-confirm a fact already true before the write ran, so
    /// <paramref name="confirmsExistenceOnly"/> keeps the recorded summary honest about which of
    /// the two it actually is.
    /// </summary>
    private async Task<TwgWriteResult> VerifyAsync(
        string issueKey, string workingDirectory, CancellationToken cancellationToken, string verb,
        bool confirmsExistenceOnly)
    {
        ProcessResult result = await RunAsync(
            ["jira", "search", "--jql", $"key = {issueKey}", "--output", "json"], workingDirectory, cancellationToken);
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
            await RunAsync(["jira", "search", "--jql", ProbeJql, "--output", "json"], workingDirectory, cancellationToken);
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
    /// The marker appended to whatever description a composed payload already carries, rather
    /// than replacing it: the description is what the card's audience reads, and the marker only
    /// has to be findable by a search, not visible at the top.
    /// </summary>
    private static string AppendMarker(string? description, string marker) =>
        description.IsNotBlank() ? $"{description}\n\n[{marker}]" : $"[{marker}]";

    private async Task<ProcessResult> RunAsync(
        IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await runner(Binary, arguments, workingDirectory, cancellationToken);
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

        throw Explain(result.StandardError);
    }

    /// <summary>
    /// twg's stderr, classified rather than parsed for content: the only distinction this makes
    /// is expired-or-missing login (a handled, expected state, Brian's design) against everything
    /// else, which the caller is responsible for reporting verbatim. Deliberately no bare "401"
    /// substring check: a digit sequence appears in plenty of ordinary refusals that have nothing
    /// to do with authentication — an issue key (PROJ-401), a custom field id
    /// (customfield_10401) — and matching on it converted a permanent refusal into a write
    /// retried forever (independent pre-PR review, cycle 1). "unauthorized" already covers the
    /// genuine "HTTP 401 Unauthorized" phrasing without that false-positive surface.
    /// </summary>
    private static TwgExecutionException Explain(string standardError)
    {
        string reported = RelayedText.OneLine(standardError).Trim();
        bool authExpired = reported.Contains("twg login", StringComparison.OrdinalIgnoreCase)
            || reported.Contains("not authenticated", StringComparison.OrdinalIgnoreCase)
            || reported.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || reported.Contains("token", StringComparison.OrdinalIgnoreCase)
                && reported.Contains("expired", StringComparison.OrdinalIgnoreCase);

        return authExpired
            ? new TwgExecutionException(
                TwgFailureKind.AuthExpired,
                $"twg is not authenticated (its login expires periodically): {reported}. Run 'twg login' "
                + "in your own terminal — it is a browser-based login twg cannot do unattended — and this "
                + "write will retry automatically once it succeeds.")
            : new TwgExecutionException(TwgFailureKind.Other, $"twg refused the write: {reported}");
    }

    /// <summary>
    /// The key twg's own JSON answered with, tolerant of both shapes it might arrive in: a bare
    /// object (create, update) or an array (search) — the first element's key either way.
    /// </summary>
    private static string? ExtractFirstKey(string json) => ReadFirstElement(json) is { } element ? ReadKey(element) : null;

    private static JsonElement? ReadFirstElement(string json)
    {
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
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.Object => document.RootElement.Clone(),
                JsonValueKind.Array when document.RootElement.GetArrayLength() > 0 =>
                    document.RootElement[0].Clone(),
                _ => null,
            };
        }
    }

    private static string? ReadKey(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty("key", out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Head(string output)
    {
        string trimmed = RelayedText.OneLine(output).Trim();
        return trimmed.IsBlank() ? "nothing at all" : RelayedText.Truncate(trimmed, 200);
    }
}

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
    /// A card carrying this exact text in its description is a card this executor made for the
    /// write named by the guid — the physical dedup gate (mirroring the GitHub read-back gate):
    /// searched for before every create, so a crash between twg creating a card and hall9k
    /// recording it cannot produce a second card on retry.
    /// </summary>
    public static string Marker(Guid writeId) => $"hall9k-write:{writeId:D}";

    /// <summary>A search nothing in a real tenant should ever match, used to prove login works without touching any real card.</summary>
    private const string ProbeJql = "key = HALL9K-DOCTOR-PROBE-00000000000000000000000000000000";

    private readonly ProcessRunner runner = runner ?? ExternalProcess.Runner;

    /// <summary>
    /// The physical half of the dedup gate: does a card already carry this write's marker. Called
    /// before every create, first attempt and retry alike, so a create that twg completed but
    /// hall9k crashed before recording is found rather than duplicated.
    /// </summary>
    public async Task<string?> FindByMarkerAsync(Guid writeId, string workingDirectory, CancellationToken cancellationToken)
    {
        ProcessResult result = await RunAsync(
            ["jira", "search", "--jql", $"text ~ \"{Marker(writeId)}\"", "--output", "json"],
            workingDirectory, cancellationToken);
        return ExtractFirstKey(result.StandardOutput);
    }

    /// <summary>
    /// One card, authored from a composed payload, then read back and verified — never trusted on
    /// twg's own create answer alone, the same discipline
    /// <see cref="GitHubWorkItemProvider.CreateAsync"/> applies to <c>gh issue create</c>.
    /// </summary>
    public async Task<TwgWriteResult> CreateAsync(
        JiraProjectKey project, JiraWritePayload payload, Guid writeId, string workingDirectory, CancellationToken cancellationToken)
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
        fields["description"] = AppendMarker(fields.GetValueOrDefault("description"), Marker(writeId));

        List<string> arguments =
            ["jira", "create", "--project", project.Value, "--type", payload.WorkItemType ?? string.Empty, "--output", "json"];
        AppendFields(arguments, fields);

        ProcessResult result = await RunAsync(arguments, workingDirectory, cancellationToken);
        string key = ExtractFirstKey(result.StandardOutput)
            ?? throw new TwgExecutionException(
                TwgFailureKind.Other,
                "twg jira create exited successfully but printed no card key, so nothing here can be "
                + $"verified: {Head(result.StandardOutput)}");

        return await VerifyAsync(key, workingDirectory, cancellationToken, "created");
    }

    /// <summary>An existing card's fields, updated and then read back to verify the write landed.</summary>
    public async Task<TwgWriteResult> UpdateAsync(
        string issueKey, JiraWritePayload payload, string workingDirectory, CancellationToken cancellationToken)
    {
        List<string> arguments = ["jira", "update", issueKey, "--output", "json"];
        AppendFields(arguments, payload.Fields ?? new Dictionary<string, string>());
        await RunAsync(arguments, workingDirectory, cancellationToken);
        return await VerifyAsync(issueKey, workingDirectory, cancellationToken, "updated");
    }

    /// <summary>A comment on an existing card — never a transition, never a close, exactly the closeout write this surface exists to carry.</summary>
    public async Task<TwgWriteResult> CommentAsync(
        string issueKey, string comment, string workingDirectory, CancellationToken cancellationToken)
    {
        List<string> arguments = ["jira", "update", issueKey, "--comment", comment, "--output", "json"];
        await RunAsync(arguments, workingDirectory, cancellationToken);
        return await VerifyAsync(issueKey, workingDirectory, cancellationToken, "commented on");
    }

    /// <summary>
    /// twg's own claim, read back through a fresh search rather than trusted — the verified
    /// read-back every acceptance criterion for this feature names explicitly.
    /// </summary>
    private async Task<TwgWriteResult> VerifyAsync(
        string issueKey, string workingDirectory, CancellationToken cancellationToken, string verb)
    {
        ProcessResult result = await RunAsync(
            ["jira", "search", "--jql", $"key = {issueKey}", "--output", "json"], workingDirectory, cancellationToken);
        string? found = ExtractFirstKey(result.StandardOutput);
        return found is not null
            ? new TwgWriteResult(found, $"twg reported {found} {verb} and it read back successfully.")
            : throw new TwgExecutionException(
                TwgFailureKind.Other,
                $"twg reported {issueKey} {verb}, but reading it back to verify found nothing. The write "
                + "was not recorded — check the board before writing again.");
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
    /// else, which the caller is responsible for reporting verbatim.
    /// </summary>
    private static TwgExecutionException Explain(string standardError)
    {
        string reported = RelayedText.OneLine(standardError).Trim();
        bool authExpired = reported.Contains("twg login", StringComparison.OrdinalIgnoreCase)
            || reported.Contains("not authenticated", StringComparison.OrdinalIgnoreCase)
            || reported.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || reported.Contains("401", StringComparison.Ordinal)
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

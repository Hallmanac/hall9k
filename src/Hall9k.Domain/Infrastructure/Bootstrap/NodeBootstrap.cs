using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Marten.Linq.MatchesSql;

namespace Hall9k.Domain.Infrastructure.Bootstrap;

public sealed record BootstrapContext(Guid OwnerId, Guid NodeId, Guid ConnectionId);

/// <summary>
/// First-use registration of Owner, Node, and the default GitHub connection (PLAN.md §6.2:
/// an owner record exists even when there's exactly one). Idempotent — subsequent calls
/// find the existing records. h9kd install performs the same bootstrap (S1-12).
/// </summary>
public static class NodeBootstrap
{
    public static async Task<BootstrapContext> EnsureAsync(IDocumentSession session, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        OwnerDetails? owner = (await session.Query<OwnerDetails>()
            .Take(1).ToListAsync(cancellationToken)).FirstOrDefault();
        Guid ownerId = owner?.Id ?? DomainId.New();
        if (owner is null)
        {
            OwnerRegistered registered = OwnerDecider.Register(
                ownerId,
                GitConfig("user.name") ?? Environment.UserName,
                GitConfig("user.email"),
                now);
            session.Events.StartStream<OwnerAggregate>(ownerId, registered);
        }

        string machineName = Environment.MachineName;
        NodeDetails? node = (await session.Query<NodeDetails>()
            .Where(n => n.MachineName == machineName)
            .Take(1).ToListAsync(cancellationToken)).FirstOrDefault();
        Guid nodeId = node?.Id ?? DomainId.New();
        if (node is null)
        {
            NodeRegistered registered = NodeDecider.Register(
                nodeId, ownerId, machineName, OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux", now);
            session.Events.StartStream<NodeAggregate>(nodeId, registered);
        }

        // The GitHub connection specifically, not whichever connection happens to be first.
        // A project binds to a connection for its repository (PLAN.md §10), and since Jira
        // connections joined the same list an unfiltered "take one" would hand a project the
        // Jira account as its repository credential the moment a Jira connection was registered
        // first. WorkItemProvider is a value object and Marten cannot translate a comparison
        // against one, so the filter is written as SQL against the stored string, the way every
        // other value-object filter in this repo is.
        ConnectionDetails? connection = (await session.Query<ConnectionDetails>()
            .Where(c => c.MatchesSql("d.data ->> 'provider' = ?", WorkItemProvider.GitHub.Value))
            .Take(1).ToListAsync(cancellationToken)).FirstOrDefault();
        Guid connectionId = connection?.Id ?? DomainId.New();
        if (connection is null)
        {
            GitHubAccountIdentity? identity = ParseGhIdentity(GhUserJson());
            ConnectionRegistered registered = ConnectionDecider.Register(
                connectionId, ownerId, WorkItemProvider.GitHub,
                identity?.Login ?? Environment.UserName, CredentialReference.GhCli, now);
            session.Events.StartStream<ConnectionAggregate>(connectionId, registered);

            // The one call this genesis bootstrap already makes to gh (to name the connection's
            // account at all) already answers the numeric id too, so this is free: no second gh
            // invocation, just a second fact kept from the one already made (idea 202383dc, A2b).
            if (identity is { } observed)
            {
                session.Events.Append(
                    connectionId, new ConnectionGitHubIdentityObserved(connectionId, observed.Id, observed.Login, now));
            }
        }

        return new BootstrapContext(ownerId, nodeId, connectionId);
    }

    /// <summary>
    /// Refreshes this install's own GitHub numeric id and login on the bootstrap connection's
    /// stream, appended only when something actually changed since the last observation
    /// (<see cref="ConnectionDecider.ObserveGitHubIdentity"/>) — read again at <c>h9k project add</c>,
    /// the one moment nothing else naturally triggers a GitHub read for the way Jira's own
    /// <c>TrackerClaimGate</c> reads lazily on first claim-gate check (idea 202383dc, A2b). A
    /// daemon-start call was tried and reverted (<c>NodeContext.InitializeAsync</c>'s own comment on
    /// the removal): it has no <c>ProcessRunner</c> seam, so calling it unconditionally there shelled
    /// to the real <c>gh</c> on every integration test's bootstrap too. Best-effort: a <c>gh</c> that
    /// cannot answer (not installed, not authenticated, offline) leaves the connection's
    /// already-recorded identity exactly as it was, never guessed at (AGENTS.md).
    /// <para>
    /// Returns whether <c>gh</c> answered with a real identity just now, regardless of whether the
    /// connection stream could actually be aggregated: on a connection this same session's own
    /// <see cref="EnsureAsync"/> call just started, live aggregation reads the database, not this
    /// session's own not-yet-saved pending events (the identical fact <c>ProjectJoinCommand.RunAsync</c>
    /// flushes before aggregating for), so a caller gating on "is this install's GitHub account
    /// confirmed" needs this return value rather than a re-read of the connection afterward —
    /// otherwise a brand-new install's very first bootstrap would read as unconfirmed even with
    /// <c>gh</c> fully authenticated, since nothing has reached the database yet.
    /// </para>
    /// </summary>
    public static async Task<bool> RefreshGitHubIdentityAsync(
        IDocumentSession session, Guid connectionId, CancellationToken cancellationToken)
    {
        if (ParseGhIdentity(GhUserJson()) is not { } observed)
        {
            return false;
        }

        ConnectionAggregate? connection = await session.Events.AggregateStreamAsync<ConnectionAggregate>(
            connectionId, token: cancellationToken);
        if (connection is not null && connection.Provider == WorkItemProvider.GitHub)
        {
            try
            {
                if (ConnectionDecider.ObserveGitHubIdentity(connection, observed.Id, observed.Login, DateTimeOffset.UtcNow) is { } appended)
                {
                    session.Events.Append(connectionId, appended);
                }
            }
            // gh's own active session answering as a different, already-confirmed account is not
            // this install's connection silently rebinding to it (ConnectionDecider.ObserveGitHubIdentity's
            // own refusal) — best-effort exactly like a gh that cannot answer at all: this call's
            // own doc comment already promises the connection's already-recorded identity is left
            // exactly as it was, never guessed at, and an unrelated `gh auth switch` must not break
            // an ordinary h9k project add/join over it.
            catch (DomainValidationException)
            {
            }
        }

        return true;
    }

    private static string? GitConfig(string key) => RunQuick("git", $"config {key}");

    internal sealed record GitHubAccountIdentity(long Id, string Login);

    private static string? GhUserJson() => RunQuick("gh", "api user");

    /// <summary>
    /// Split from the raw gh call so it is unit-testable against recorded gh output without a
    /// process in the loop, mirroring every other GitHub-JSON mapper in this codebase
    /// (<c>GitHubReviewAssignments.ParseReviewRequested</c>, for one).
    /// </summary>
    internal static GitHubAccountIdentity? ParseGhIdentity(string? json)
    {
        if (json.IsBlank())
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("id", out JsonElement idElement)
                && idElement.ValueKind == JsonValueKind.Number
                && idElement.TryGetInt64(out long id)
                && root.TryGetProperty("login", out JsonElement loginElement)
                && loginElement.ValueKind == JsonValueKind.String
                && loginElement.GetString() is { } login
                && login.IsNotBlank()
                    ? new GitHubAccountIdentity(id, login)
                    : null;
        }
        // gh answering with valid JSON that simply does not carry the shape expected (a proxy's
        // own error body, a future gh version renaming a field) is exactly as unreadable as
        // malformed JSON — TryGetInt64/GetString throw InvalidOperationException rather than
        // returning false for a property of the wrong kind, so both exception types land here
        // rather than only the JSON-syntax one (a defect this method shipped with, found and fixed
        // in this same task's own self-review).
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The 3-second bound is real only because both streams are drained on background callbacks
    /// rather than a blocking <c>ReadToEnd()</c> ahead of <c>WaitForExit</c> — that ordering waits
    /// on end-of-file from the pipe, which a <c>gh</c> hung on network or credential state never
    /// sends, so the nominal timeout below it was never reached (found in this same task's own
    /// self-review). Reading both streams, not just stdout, also avoids the classic redirected-
    /// process deadlock: an unread stderr pipe can fill and block the child from exiting at all.
    /// </summary>
    private static string? RunQuick(string fileName, string arguments)
    {
        try
        {
            using Process process = new();
            process.StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            StringBuilder standardOutput = new();
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    standardOutput.AppendLine(e.Data);
                }
            };
            process.ErrorDataReceived += (_, _) => { };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(3000))
            {
                TryKill(process);
                return null;
            }

            // WaitForExit(int) can return before the async callbacks above have drained the last
            // of the pipes; the parameterless overload blocks until they have, and returns
            // immediately here since the process has already exited.
            process.WaitForExit();

            string output = standardOutput.ToString().Trim();
            return process.ExitCode == 0 && output.IsNotBlank() ? output : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Nothing here is recoverable and nothing here is the caller's problem.
        }
    }
}

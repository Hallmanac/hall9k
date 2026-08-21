using System.Diagnostics;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Infrastructure.Ids;
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
            ConnectionRegistered registered = ConnectionDecider.Register(
                connectionId, ownerId, WorkItemProvider.GitHub,
                GhLogin() ?? Environment.UserName, CredentialReference.GhCli, now);
            session.Events.StartStream<ConnectionAggregate>(connectionId, registered);
        }

        return new BootstrapContext(ownerId, nodeId, connectionId);
    }

    private static string? GitConfig(string key) => RunQuick("git", $"config {key}");

    private static string? GhLogin() => RunQuick("gh", "api user -q .login");

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
            process.Start();
            string output = process.StandardOutput.ReadToEnd().Trim();
            return process.WaitForExit(3000) && process.ExitCode == 0 && output.IsNotBlank()
                ? output
                : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}

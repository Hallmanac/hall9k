using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;

namespace Hall9k.Tests.TestSupport;

/// <summary>Records a claude-code connection and binds a project to one, through the same deciders the CLI calls.</summary>
internal static class ClaudeCodeBindingSeed
{
    public static async Task<Guid> RegisterConnectionAsync(
        IDocumentStore store, string configDirectory, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Guid id = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<ConnectionAggregate>(
            id, ConnectionDecider.Register(
                id, DomainId.New(), WorkItemProvider.ClaudeCode, configDirectory, CredentialReference.ClaudeConfigDirectory, now));
        await session.SaveChangesAsync(cancellationToken);
        return id;
    }

    public static async Task BindAsync(
        IDocumentStore store, Guid projectId, Guid? connectionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        ProjectAggregate project = await session.Events.AggregateStreamAsync<ProjectAggregate>(projectId, token: cancellationToken)
            ?? throw new InvalidOperationException($"No project {projectId} to bind.");
        session.Events.Append(projectId, ProjectDecider.ChangeSettings(
            project,
            verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            skipPermissions: Optional<bool>.None,
            contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            now,
            DomainId.New(),
            claudeCodeConnectionId: Optional<Guid?>.Of(connectionId)));
        await session.SaveChangesAsync(cancellationToken);
    }
}

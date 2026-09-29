using Hall9k.Connectors.Prompts;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Learning.Queries;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The local owner's fleet for the CLI callers that compose a work prompt (<c>h9k task work</c>,
/// <c>start</c>, <c>delegate</c>, and <c>task show</c>'s starting context), read the way
/// <see cref="TaskAssignCommand.ResolveNodeIdAsync"/> reads it: the project's own ledger chain
/// through <see cref="ILedgerChainReader.ComputeAsync"/>, then the owner's fleet within it. Unlike
/// that placement door, an unreadable chain here is not an error to refuse over: a prompt still has
/// to compose, so it yields no fleet, and no fleet fences every replicated note (fail closed).
/// <para>
/// Lazy and read at most once per source: the answer is asked for only when a field about to be
/// rendered carries a replicated sender (<see cref="ReplicatedNote.CarriesSender"/>, a lesson with a
/// sender, a blocker summary with one), so a solo project never walks the ledger for a prompt.
/// </para>
/// </summary>
internal sealed class LocalFleetSource(IQuerySession session, ProjectDetails project, Guid ownerId)
{
    private Task<LocalFleet?>? _fleet;

    public Task<LocalFleet?> GetAsync(CancellationToken cancellationToken) =>
        _fleet ??= ReadAsync(cancellationToken);

    /// <summary>The fleet only when <paramref name="task"/> has a retry, handback or handoff sender to judge; null otherwise, which is also what a task with only native text needs.</summary>
    public async ValueTask<LocalFleet?> GetIfCarriedByAsync(TaskDetails task, CancellationToken cancellationToken) =>
        ReplicatedNote.CarriesSender(task) ? await GetAsync(cancellationToken) : null;

    /// <summary>The shape <see cref="BlockerHandoffFencing.ApplyAsync"/> asks for.</summary>
    public Func<CancellationToken, Task<LocalFleet?>> Reader => GetAsync;

    /// <summary>The shape <see cref="LessonPromptFeed"/> asks for: the node ids alone.</summary>
    public LocalFleetReader LessonReader => async token => (await GetAsync(token))?.NodeIds;

    private async Task<LocalFleet?> ReadAsync(CancellationToken cancellationToken)
    {
        string? fingerprint = await OwnerRootFingerprintResolver.ResolveAsync(session, ownerId, cancellationToken);
        return await LocalFleet.ReadAsync(new GitLedgerChainReader(), project.RepositoryPath, fingerprint, cancellationToken);
    }
}

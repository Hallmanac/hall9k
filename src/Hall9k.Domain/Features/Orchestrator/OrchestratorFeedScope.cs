namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// Which project an admitted event belongs to, and which of that project's tasks if any — what
/// <see cref="OrchestratorFeedSelection"/> asks its caller for, because answering it means
/// reading documents and the selection itself reads nothing.
/// </summary>
public sealed record OrchestratorFeedScope(Guid ProjectId, Guid? TaskId);

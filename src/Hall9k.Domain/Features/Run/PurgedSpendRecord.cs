using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// One <c>TokensRecorded</c> or <c>PublicationTokensRecorded</c> event's own spend, carried
/// forward past a project purge (task: an archived project can be purged) so
/// <see cref="PeriodSpend.ReadAsync"/>'s live sum never silently drops it. A purge hard-deletes
/// every event on every stream it owns — <c>mt_events.stream_id</c> cascades from
/// <c>mt_streams.id</c>, so even excluding these two event types from that delete would not save
/// them once their own stream row goes — so <c>ProjectPurgeEngine</c> writes one of these, as a
/// plain document rather than a projection of an event stream, in the very same transaction as
/// the deletes, for every such event on a stream it is about to erase. <see cref="RecordedAt"/> is
/// copied verbatim from the event it replaces, so the same period-start filter that ages live
/// spend out of <see cref="PeriodSpend"/> ages this out too, the moment it no longer matters.
/// <see cref="Model"/> is already resolved to <see cref="AgentModel.Unknown"/> for a null model,
/// the same sentinel <see cref="Hall9k.Domain.Features.Run.Events.TokensRecorded.Model"/>'s own
/// null already means, so a reader here never has to repeat that resolution.
/// </summary>
public sealed record PurgedSpendRecord(Guid Id, DateTimeOffset RecordedAt, long TotalInputTokens, AgentModel Model);

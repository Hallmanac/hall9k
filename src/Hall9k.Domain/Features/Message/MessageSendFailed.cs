namespace Hall9k.Domain.Features.Message;

/// <summary>This node tried to push an envelope to its own outbox and the transport refused or
/// failed — the seq is still reserved on this stream; a later successful push is a
/// <see cref="MessageResent"/> of the same seq, not a new one. Carries the envelope's own content
/// (never just the seq and the failure reason) because the push never landed anywhere else this
/// node could re-read it from — without it, a resend would have no bytes to rebuild. <see
/// cref="ProjectId"/> resolves a pre-M2 <see cref="Guid.Empty"/> <see cref="MessageQueued.ProjectId"/>
/// the same way <see cref="MessageSent.ProjectId"/> does — a failed first attempt still fixes which
/// project this message belongs to, so a later retry never has to re-decide it.</summary>
public sealed record MessageSendFailed(
    Guid FromNodeId, long Seq, string FromOwner, string To, string? About, string Kind, string Body,
    string Reason, DateTimeOffset At, Guid ProjectId);

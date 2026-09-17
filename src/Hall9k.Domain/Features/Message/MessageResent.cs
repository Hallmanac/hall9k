namespace Hall9k.Domain.Features.Message;

/// <summary>A previously failed send landed on retry, at the same seq it always held.
/// <see cref="ProjectId"/> resolves a pre-M2 <see cref="Guid.Empty"/> <see cref="MessageQueued.ProjectId"/>
/// the same way <see cref="MessageSent.ProjectId"/> and <see cref="MessageSendFailed.ProjectId"/> do
/// — a legacy message adopted while already <see cref="MessageSendFailed"/> must still resolve the
/// sentinel on the retry that finally lands it, the identical resolution <see cref="MessageSent"/>
/// gives a legacy message that lands on its first try; without it, an adopted legacy message that
/// happened to fail its first attempt kept <see cref="Guid.Empty"/> forever, even after a successful
/// resend (independent pre-PR review, cycle 4, adversarial lens, medium).</summary>
public sealed record MessageResent(Guid FromNodeId, long Seq, DateTimeOffset At, Guid ProjectId);

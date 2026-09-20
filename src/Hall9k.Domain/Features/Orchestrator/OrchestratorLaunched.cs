namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// An orchestrator window declared itself live for one project on one node (idea 89471598, piece
/// 1). Appended only by <c>h9k orchestrator register</c>, which the launch anchor calls as the
/// window's very first act, so the audit trail exists without the operator doing anything.
/// <para>
/// Presence needs an explicit registration rather than a lookup because no registry can answer
/// it: Claude Code's own <c>~/.claude/sessions</c> directory cannot tell the orchestrator window
/// from the discovery, refinement, or planning sessions that also run in the same project home
/// (Brian has had two or three at once), and another vendor's CLI may keep no registry at all. A
/// process id is the one identity every vendor's window has.
/// </para>
/// <para>
/// <see cref="ProcessStartedAt"/> is the one fact here nobody typed: it is read off this
/// machine's own process table at registration time, and is <see langword="null"/> when that read
/// failed (a process this user cannot query) rather than guessed — AGENTS.md's "never guess at
/// unobserved facts". <see cref="OrchestratorLiveness"/> is what reads it, to tell this window
/// still running from an unrelated later process the operating system handed the same recycled
/// pid.
/// </para>
/// </summary>
public sealed record OrchestratorLaunched(
    Guid NodeId,
    Guid ProjectId,
    string SessionName,
    int ProcessId,
    string Cli,
    DateTimeOffset? ProcessStartedAt,
    DateTimeOffset LaunchedAt);

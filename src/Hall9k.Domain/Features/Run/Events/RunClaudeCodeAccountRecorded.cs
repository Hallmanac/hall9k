namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The Claude Code account this run runs under, frozen at dispatch: the claude-code connection its project
/// was bound to then, or null for the default login. Every later spawn for the run (resumes, review passes,
/// verify and final passes, fix, recovery, and the pre-flight) uses this value, never the project's current
/// binding, because a resumed session's transcript lives in the config directory that started it.
/// <para>
/// Node-scoped, unlike <see cref="RunDispatched"/> beside it: the connection is this machine's own, so this
/// names it by id and never carries the directory or the email. A run with no such event was dispatched
/// before the binding existed and runs under the default login it started under.
/// </para>
/// </summary>
public sealed record RunClaudeCodeAccountRecorded(
    Guid Id,
    Guid? ConnectionId,
    DateTimeOffset RecordedAt);

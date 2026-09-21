namespace Hall9k.Domain.Features.Run;

/// <summary>
/// One process a local launch is accountable for (idea b9b09779, piece 5): something a launch step
/// started and will have to end, or the <c>h9k</c> process walking the plan that starts them. PID
/// and start time together are the identity, never the pid alone (Decisions Log #2): a launch can
/// sit up for hours while a reviewer walks the change, which is more than long enough for the
/// operating system to hand that pid to something else entirely before anything comes back to
/// stop it.
/// </summary>
/// <param name="Command">The command as it was actually run, port substitution included — what was started, not what the skill wrote.</param>
public sealed record LocalLaunchProcess(int ProcessId, DateTimeOffset StartedAt, string Command);

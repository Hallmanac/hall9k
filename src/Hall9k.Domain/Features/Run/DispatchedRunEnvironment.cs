namespace Hall9k.Domain.Features.Run;

/// <summary>
/// The environment variable every headless agent this platform spawns carries, naming its own run
/// (task: a dispatched session cannot drive the project's own lifecycle). Stamped once at each spawn
/// site — <c>Hall9k.Daemon.Execution.ClaudeExecutor.SpawnAsync</c> for every daemon dispatch (build,
/// fix, review, verify, final pass, recovery, courier, and anything else that seam ever spawns) and
/// <c>Hall9k.Cli.Commands.HeadlessLaunch.SpawnDetached</c> for both of its callers (<c>h9k task
/// start</c>'s own agent, <c>h9k task delegate</c>'s contractor) — never by each of the eighteen
/// <c>AgentSpawnRequest</c> builders individually, so nothing has to remember to add it. Inherited by
/// every descendant the spawned process starts, including every Bash-tool child, the same way
/// <c>HALL9K_DETACHED_SESSION</c> already rides a headless launch's environment.
/// <para>
/// Lives in <c>Hall9k.Domain</c> because both a daemon dispatch and a CLI-launched headless session
/// set it, and the CLI's own interceptor (<c>Hall9k.Cli.Infrastructure</c>) reads it back — the
/// Reference graph lets <c>Hall9k.Daemon</c> and <c>Hall9k.Cli</c> each depend on
/// <c>Hall9k.Domain</c>, but neither on the other, so a constant only one of them could reach would
/// leave the other duplicating it.
/// </para>
/// <para>
/// Distinct from <c>HALL9K_INTERACTIVE_RUN_ID</c> (Decisions Log #264, <c>InteractiveSessionLiveness</c>):
/// that variable names an operator's own attached claim and every attendance rule in this platform
/// is pinned to it meaning exactly that. This one flows only from the daemon or a deliberate headless
/// launch into the agent it spawns and its own descendants — never from an attended session into
/// anything it starts — so nothing attended ever carries it, and an operator's own <c>h9k task
/// work</c>, a pasted prompt, or <c>--direct-launch</c> session never does either.
/// </para>
/// </summary>
public static class DispatchedRunEnvironment
{
    public const string RunIdVariable = "HALL9K_DISPATCHED_RUN_ID";
}

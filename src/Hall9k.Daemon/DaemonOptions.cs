namespace Hall9k.Daemon;

public sealed class DaemonOptions
{
    public const string SectionName = "Hall9k";

    /// <summary>Node-level cap on simultaneously claimed/running tasks (PLAN.md §6.4 guidance).</summary>
    public int MaxConcurrentRuns { get; set; } = 3;

    /// <summary>Fallback sweep interval; the doorbell usually wakes the loop sooner.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan LeaseTimeout { get; set; } = TimeSpan.FromSeconds(60);
}

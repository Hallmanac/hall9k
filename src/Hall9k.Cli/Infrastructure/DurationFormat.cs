namespace Hall9k.Cli.Infrastructure;

/// <summary>
/// The one rendering of a <see cref="TimeSpan"/> every duration-carrying surface shares — the
/// passage section (<c>TaskShowCommand</c>), the queued section's per-row wait and heading total,
/// and the throughput block (task: h9k status reports throughput beside spend) — so a reader never
/// sees "2h05m" on one screen and "2.08h" on another for the identical span.
/// </summary>
internal static class DurationFormat
{
    public static string Short(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours}h{duration.Minutes:00}m"
        : duration.TotalMinutes >= 1
            ? $"{(int)duration.TotalMinutes}m{duration.Seconds:00}s"
            : $"{duration.TotalSeconds:0.#}s";

    /// <summary>Hours to one decimal place, the throughput block's own unit (the reference baseline: "median 6.8 hours, p90 22.7 hours").</summary>
    public static string Hours(TimeSpan duration) => $"{duration.TotalHours:0.0}h";
}

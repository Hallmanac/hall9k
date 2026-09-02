namespace Hall9k.Domain.Shared.ValueObjects;

/// <summary>
/// The window a node's token-spend budget resets on (backlog: spend-governor step three).
/// A closed vocabulary of two, both UTC: a day starts at midnight, a week starts Monday
/// midnight (ISO). Unknown means "not recognized", the same idiom every other value object
/// here uses for an unusable value read back from a config file or environment variable.
/// </summary>
public sealed record SpendPeriod
{
    public static readonly SpendPeriod Day = new("day");
    public static readonly SpendPeriod Week = new("week");
    public static readonly SpendPeriod Unknown = new("");

    public string Value { get; }

    private SpendPeriod(string value) => Value = value;

    public static SpendPeriod FromInput(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "day" => Day,
        "week" => Week,
        _ => Unknown,
    };

    public bool IsWellFormed => this == Day || this == Week;

    public override string ToString() => Value;

    /// <summary>
    /// The UTC instant this period most recently started, as of <paramref name="instant"/>: the
    /// day's own midnight, or — for a week — the Monday at or before it (ISO week, never the
    /// culture-dependent Sunday start .NET's own <see cref="DayOfWeek"/> otherwise implies).
    /// </summary>
    public DateTimeOffset StartOf(DateTimeOffset instant)
    {
        DateTimeOffset utc = instant.ToUniversalTime();
        DateTimeOffset dayStart = new(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero);
        if (this == Day)
        {
            return dayStart;
        }

        int daysSinceMonday = ((int)dayStart.DayOfWeek + 6) % 7;
        return dayStart.AddDays(-daysSinceMonday);
    }

    /// <summary>When the period containing <paramref name="instant"/> rolls into the next one.</summary>
    public DateTimeOffset NextRolloverAfter(DateTimeOffset instant) =>
        StartOf(instant).AddDays(this == Day ? 1 : 7);
}

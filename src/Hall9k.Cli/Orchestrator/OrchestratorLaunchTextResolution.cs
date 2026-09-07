using Hall9k.Domain.Features.Orchestrator;

namespace Hall9k.Cli.Orchestrator;

/// <summary>
/// The read/replace logic <c>h9k orchestrator launch-text show/set</c> and <c>measure</c> share
/// over a scope's own list of <see cref="LaunchText"/> records — node (<c>OperatingSettings</c>)
/// or project (<c>ProjectAggregate</c>/<c>ProjectDetails</c>) reads the list, calls these, and
/// writes the result back through whichever storage that scope owns.
/// </summary>
public static class OrchestratorLaunchTextResolution
{
    /// <summary>
    /// The stored entry for <paramref name="cli"/>, or the computed default when nothing is
    /// stored yet and <see cref="LaunchTextDefaults"/> knows how to synthesize one for it.
    /// </summary>
    public static LaunchText? Resolve(
        IReadOnlyList<LaunchText> stored, string cli, string workingDirectory, string openingMessage)
    {
        string normalized = LaunchText.NormalizeCli(cli);
        LaunchText? existing = stored.FirstOrDefault(entry => LaunchText.NormalizeCli(entry.Cli) == normalized);
        return existing ?? LaunchTextDefaults.For(normalized, workingDirectory, openingMessage);
    }

    /// <summary>Replaces (or adds) the entry for <paramref name="cli"/> with new text, clearing any stale measurement.</summary>
    public static IReadOnlyList<LaunchText> WithText(IReadOnlyList<LaunchText> stored, string cli, string text)
    {
        string normalized = LaunchText.NormalizeCli(cli);
        List<LaunchText> updated = [.. stored.Where(entry => LaunchText.NormalizeCli(entry.Cli) != normalized)];
        updated.Add(new LaunchText(normalized, text));
        return updated;
    }

    /// <summary>Stamps a fresh measurement onto the entry for <paramref name="cli"/>, keeping its text.</summary>
    public static IReadOnlyList<LaunchText> WithMeasurement(
        IReadOnlyList<LaunchText> stored, LaunchText measured, int turnOneTokens, DateTimeOffset measuredAt)
    {
        string normalized = LaunchText.NormalizeCli(measured.Cli);
        List<LaunchText> updated = [.. stored.Where(entry => LaunchText.NormalizeCli(entry.Cli) != normalized)];
        updated.Add(measured with { Cli = normalized, MeasuredTurnOneTokens = turnOneTokens, MeasuredAt = measuredAt });
        return updated;
    }
}

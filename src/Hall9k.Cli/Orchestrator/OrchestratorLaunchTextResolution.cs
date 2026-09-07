using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Infrastructure.Extensions;
using Hall9k.Domain.Shared.Exceptions;

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

    /// <summary>
    /// The stored entry for <paramref name="cli"/>, or <see langword="null"/> when nothing has
    /// ever been recorded — never the computed default <see cref="Resolve"/> falls back to.
    /// <c>h9k orchestrator measure</c> uses this rather than <see cref="Resolve"/>: the computed
    /// default is documented as "rendered, not stored, until a human or the
    /// orchestrator-recipe-generator skill records a real setting with launch-text set"
    /// (<see cref="LaunchTextDefaults"/>), and stamping a measurement onto it would materialize
    /// that rendering into <c>config.json</c>/the project's own stream, freezing it against a
    /// later release's own flag changes (independent pre-PR review, cycle 3, both lenses).
    /// </summary>
    public static LaunchText? ResolveStored(IReadOnlyList<LaunchText> stored, string cli)
    {
        string normalized = LaunchText.NormalizeCli(cli);
        return stored.FirstOrDefault(entry => LaunchText.NormalizeCli(entry.Cli) == normalized);
    }

    /// <summary>
    /// Replaces (or adds) the entry for <paramref name="cli"/> with new text, clearing any stale
    /// measurement. Both the node and the project <c>launch-text set</c> commands share this one
    /// path, so the blank checks live here rather than only on <c>ProjectDecider.ChangeSettings</c>
    /// — otherwise the node path, which writes straight to <c>config.json</c> with no decider in
    /// front of it, would accept a blank CLI name or a blank launch line the project path already
    /// refuses (independent pre-PR review, cycle 1, both lenses).
    /// </summary>
    public static IReadOnlyList<LaunchText> WithText(IReadOnlyList<LaunchText> stored, string cli, string text)
    {
        if (cli.IsBlank())
        {
            throw new DomainValidationException("A launch-text entry needs a CLI name.");
        }

        if (text.IsBlank())
        {
            throw new DomainValidationException($"The launch text for '{cli}' cannot be blank.");
        }

        string normalized = LaunchText.NormalizeCli(cli);
        List<LaunchText> updated = [.. stored.Where(entry => LaunchText.NormalizeCli(entry.Cli) != normalized)];
        updated.Add(new LaunchText(normalized, text));
        return updated;
    }

    /// <summary>
    /// Stamps a fresh measurement onto the entry for <paramref name="cli"/>, keeping its text —
    /// but only when <paramref name="stored"/>'s own current entry for that CLI still carries the
    /// exact text <paramref name="measured"/> was probed against. <paramref name="stored"/> is
    /// expected to be freshly reloaded rather than the pre-probe snapshot <paramref
    /// name="measured"/> came from, so a concurrent <c>launch-text set</c> for this same CLI that
    /// landed while the probe ran (up to <see cref="OrchestratorMeasureProbe"/>'s own timeout) is
    /// visible here as a text mismatch. Re-stamping <paramref name="measured"/>'s own pre-probe
    /// text onto that fresher entry would silently revert the concurrent edit and record a
    /// measurement against a line that no longer exists — <paramref name="applied"/> is
    /// <see langword="false"/> and <paramref name="stored"/> is returned unchanged instead, so the
    /// caller can tell the operator the measurement was discarded rather than writing it
    /// (independent pre-PR review, cycle 1, both lenses).
    /// </summary>
    public static IReadOnlyList<LaunchText> WithMeasurement(
        IReadOnlyList<LaunchText> stored, LaunchText measured, int turnOneTokens, DateTimeOffset measuredAt, out bool applied)
    {
        string normalized = LaunchText.NormalizeCli(measured.Cli);
        LaunchText? current = stored.FirstOrDefault(entry => LaunchText.NormalizeCli(entry.Cli) == normalized);
        if (current is null || !string.Equals(current.Text, measured.Text, StringComparison.Ordinal))
        {
            applied = false;
            return stored;
        }

        applied = true;
        List<LaunchText> updated = [.. stored.Where(entry => LaunchText.NormalizeCli(entry.Cli) != normalized)];
        updated.Add(current with { MeasuredTurnOneTokens = turnOneTokens, MeasuredAt = measuredAt });
        return updated;
    }
}

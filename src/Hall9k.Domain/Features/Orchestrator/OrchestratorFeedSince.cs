using System.Globalization;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// What <c>h9k orchestrator feed --since</c> accepts (idea 89471598, piece 2): a relative
/// duration back from now — <c>45m</c>, <c>6h</c>, <c>3d</c>, <c>2w</c> — or an absolute instant
/// a reader can type (<c>2026-09-19</c>, <c>2026-09-19T14:00:00-04:00</c>).
/// <para>
/// A relative duration is the form an orchestrator actually wants ("what happened while I was
/// away"), and an absolute one is what a second window needs to line its own read up against a
/// first. Anything else is refused by name rather than read as zero: silently reading an
/// unparseable <c>--since</c> as "everything" would dump a project's whole history at somebody
/// who asked for an hour of it.
/// </para>
/// </summary>
public static class OrchestratorFeedSince
{
    /// <summary>The instant <paramref name="value"/> names, resolved against
    /// <paramref name="now"/> for a relative form.</summary>
    public static DateTimeOffset Parse(string? value, DateTimeOffset now)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.IsBlank())
        {
            throw new DomainValidationException(
                "--since needs a time. Use a duration back from now (45m, 6h, 3d, 2w) or an instant "
                + "(2026-09-19, 2026-09-19T14:00:00-04:00).");
        }

        if (TryRelative(trimmed, now, out DateTimeOffset relative))
        {
            return relative;
        }

        return DateTimeOffset.TryParse(
                trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset absolute)
            ? absolute
            : throw new DomainValidationException(
                $"'{Relayed(trimmed)}' is not a time --since understands. Use a duration back from now "
                + "(45m, 6h, 3d, 2w) or an instant (2026-09-19, 2026-09-19T14:00:00-04:00).");
    }

    private static bool TryRelative(string value, DateTimeOffset now, out DateTimeOffset resolved)
    {
        resolved = default;
        char unit = char.ToLowerInvariant(value[^1]);
        if (unit is not ('m' or 'h' or 'd' or 'w')
            || !int.TryParse(value[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out int quantity))
        {
            return false;
        }

        // NumberStyles.None already refuses a sign, so the quantity is non-negative here; zero is
        // still legal and means "since this instant", which reads nothing and is an honest answer
        // rather than a refusal.
        long minutes = unit switch
        {
            'm' => quantity,
            'h' => quantity * 60L,
            'd' => quantity * 60L * 24,
            _ => quantity * 60L * 24 * 7,
        };

        // A quantity large enough to run off the end of the calendar (int.MaxValue weeks) means
        // "all of it" — clamped rather than thrown, because TimeSpan.FromMinutes and the
        // subtraction below would each throw on their own and a caller asking for an absurd
        // window still deserves the whole history rather than a stack trace.
        double sinceEpoch = (now - DateTimeOffset.MinValue).TotalMinutes;
        resolved = minutes >= sinceEpoch ? DateTimeOffset.MinValue : now - TimeSpan.FromMinutes(minutes);
        return true;
    }

    /// <summary>
    /// What a refused value is safe to be quoted as — the <see cref="Project.AutoPrReviewSpeed"/>
    /// convention: this comes off a command line and the refusal is printed to a terminal, so a
    /// control character or a bidirectional override in it cannot reach the refusal explaining
    /// it, and an unbounded argument cannot be echoed whole.
    /// </summary>
    private const int MaximumRelayedLength = 40;

    private static string Relayed(string value)
    {
        string visible = new([.. value.Take(MaximumRelayedLength).Select(Legible)]);
        return value.Length > MaximumRelayedLength ? visible + "…" : visible;
    }

    private static char Legible(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or ':' or '.' or '+' ? character : '?';
}

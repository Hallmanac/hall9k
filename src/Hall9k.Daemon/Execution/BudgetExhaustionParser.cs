namespace Hall9k.Daemon.Execution;

/// <summary>
/// Recognizes the one error shape the platform treats as external and clock-recoverable
/// rather than a generic agent failure (Decisions Log #40): Claude Code's own usage-limit
/// message, exactly as the CLI writes it into the terminal result's text — "Claude AI usage
/// limit reached|&lt;epoch&gt;" in headless mode, or the unpiped "... usage limit reached.
/// Your limit will reset at ..." wording. Matched case-insensitively on the phrase both forms
/// share, and nothing else.
/// <para>
/// Deliberately narrow, per the never-guess rule: <see cref="IsBudgetExhausted"/> answers true
/// only when the text names the usage limit specifically. A generic <c>is_error</c> result
/// with no recognizable text, or one whose text says something else, stays the generic
/// failure it is — the origin diagnosis needed the full pattern (simultaneous cross-run
/// deaths, no wall-clock jump, daemon unaffected), not one error's wording, and trading
/// "wrong cause: suspend" for "wrong cause: budget" would be the same defect with a new label.
/// </para>
/// </summary>
public static class BudgetExhaustionParser
{
    private const string Marker = "usage limit reached";

    public static bool IsBudgetExhausted(string? summary) =>
        summary is not null && summary.Contains(Marker, StringComparison.OrdinalIgnoreCase);
}

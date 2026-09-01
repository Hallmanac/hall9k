using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Cli.Infrastructure;

/// <summary>
/// The clearing idiom every review-cap option shares (<c>h9k project set</c>, <c>h9k task
/// set-review-caps</c>, Decisions Log #108): absent means left alone, 'default' clears the
/// override so the level above decides again, and anything else must parse as a whole number —
/// the decider itself refuses anything lower than its own floor (<c>ReviewCapValidation</c>:
/// 1 everywhere except the task-level per-run caps, which take 0 as the documented takeover lever).
/// </summary>
internal static class ClearableCapOption
{
    public static Optional<int?> Parse(string? input, string optionName)
    {
        if (input is null)
        {
            return Optional<int?>.None;
        }

        if (input.Trim().Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return Optional<int?>.Of(null);
        }

        return int.TryParse(input.Trim(), out int value)
            ? Optional<int?>.Of(value)
            : throw new DomainValidationException($"{optionName} expects a whole number or 'default', got '{input}'.");
    }
}

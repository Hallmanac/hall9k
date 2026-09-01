using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Shared.ValueObjects;

/// <summary>
/// Shared by every review-cap setter — <c>ProjectDecider.ChangeSettings</c> and
/// <c>TaskDecider.OverrideReviewCaps</c> (Decisions Log #108) — since a present-with-null value
/// always clears the override, so only a genuinely present, non-null value is ever checked.
/// <see cref="RefuseNonPositiveCap"/> is the general floor of 1, used everywhere a cap has no
/// takeover role: the node and project levels (neither can target a specific live run) and the
/// task-level lifetime budget (its own park reads "spent", not "at or below the track's cycle
/// count", so 0 there is just an unreachable budget, not a takeover). <see cref="RefuseNegativeCap"/>
/// is the task-level floor of 0 for the three per-run caps, where 0 is the documented takeover
/// lever: cycles-since-last-grant can never be negative, so a cap of 0 always parks at the very
/// next cap check, regardless of the track's own count.
/// </summary>
internal static class ReviewCapValidation
{
    public static void RefuseNonPositiveCap(Optional<int?> cap, string name)
    {
        if (cap is { HasValue: true, Value: { } value } && value < 1)
        {
            throw new DomainValidationException($"{name} must be at least 1.");
        }
    }

    public static void RefuseNegativeCap(Optional<int?> cap, string name)
    {
        if (cap is { HasValue: true, Value: { } value } && value < 0)
        {
            throw new DomainValidationException($"{name} must be at least 0.");
        }
    }
}

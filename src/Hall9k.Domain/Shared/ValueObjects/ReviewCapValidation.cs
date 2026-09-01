using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Shared.ValueObjects;

/// <summary>
/// Shared by every review-cap setter — <c>ProjectDecider.ChangeSettings</c> and
/// <c>TaskDecider.OverrideReviewCaps</c> (Decisions Log #108) — since a project and a task
/// override the identical four caps under the identical rule: a present-with-null value clears
/// the override, so only a genuinely present, non-null value is checked. Zero is refused rather
/// than accepted as "trip on the very next cycle" — that takeover shape is reached by setting the
/// override AT OR BELOW the track's current cycle count, which a cap of 1 already achieves for
/// any run past its first cycle.
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
}

using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// Shared by every review-stage-composition setter — <c>Hall9k.Cli.Commands.ConfigSetCommand</c>
/// (node), <c>Hall9k.Domain.Features.Project.Handlers.ProjectDecider.ChangeSettings</c> (project),
/// and <c>Hall9k.Domain.Features.Tasks.Handlers.TaskDecider.Add</c>/<c>Revise</c> (task) — so the
/// three levels refuse the same way rather than three hand-written copies drifting apart. A
/// composition that removes a load-bearing guarantee (<see cref="ReviewStageComposition.WaivesFinalFullPassGuarantee"/>
/// or <see cref="ReviewStageComposition.DropsALens"/>) is refused unless
/// <paramref name="acknowledged"/> says the consequence was accepted — the platform advises, the
/// human overrides, never a silent degrade (task: removing a load-bearing guarantee names the
/// decision it overrides at set time and requires the consequence to be acknowledged). The
/// acknowledgment itself is recorded on the same event the composition change lands on, reusing
/// that event's own who/when, the <c>TaskPublished.UntrackedAttested</c> attestation idiom.
/// </summary>
public static class ReviewStageCompositionValidation
{
    /// <summary>
    /// The shared "vet a level's own raw CLI input" entry point for every setter — blank or the
    /// clearing word "default" (the <see cref="Shared.ValueObjects.AgentModel"/> convention; the
    /// node level's own <c>ConfigSetCommand</c> never calls this, since it has no clearing word,
    /// the same as the four review-cycle caps) returns null, meaning "no override, defer to the
    /// level below"; anything else must parse to one of the five recognized compositions
    /// (<see cref="ReviewStageComposition.Parse"/>, so a typo is refused with the recognized
    /// values quoted) and, if it removes a load-bearing guarantee, be acknowledged.
    /// </summary>
    public static string? VetInput(string? input, bool acknowledged, string optionName)
    {
        if (input.IsBlank() || input.Trim().Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        ReviewStageComposition parsed = ReviewStageComposition.Parse(input);
        RefuseWithoutAcknowledgment(parsed, acknowledged, optionName);
        return parsed.Value;
    }

    public static void RefuseWithoutAcknowledgment(ReviewStageComposition composition, bool acknowledged, string optionName)
    {
        if (acknowledged || !composition.RequiresAcknowledgment)
        {
            return;
        }

        throw new DomainValidationException(Consequence(composition, optionName));
    }

    /// <summary>
    /// What a decider's own event should actually record for its acknowledgment flag: true only
    /// when <paramref name="normalizedComposition"/> is a value that genuinely needed one — a
    /// human who passes <c>--accept-reduced-review</c> alongside a safe composition (FullPipeline,
    /// say) must not have that recorded as though a real guarantee were traded away and accepted,
    /// the same never-assert-an-unobserved-fact clamp <c>TaskPublished.UntrackedAttested</c>
    /// already applies to a flag the gate never actually needed.
    /// </summary>
    public static bool AcknowledgmentActuallyNeeded(string? normalizedComposition, bool acknowledged) =>
        acknowledged && normalizedComposition is { } value && ReviewStageComposition.FromInput(value).RequiresAcknowledgment;

    private static string Consequence(ReviewStageComposition composition, string optionName)
    {
        string guarantee = composition.Value switch
        {
            "None" =>
                "removes Decisions Log #92's guarantee (nothing merges on scoped-review-or-no-review "
                + "alone) entirely — no reviewer will ever read this diff, on any run this level governs",
            "SkipFinalPass" =>
                "waives Decisions Log #92's mandatory fresh-context re-read immediately before merge — "
                + "nothing rereads the terminal fix; the build/test gate still runs full regardless",
            _ => string.Empty,
        };

        string lens = composition.Value switch
        {
            "AdversarialOnly" =>
                "drops the conformance lens — it alone catches whether the work meets its objective, its "
                + "acceptance criteria, and repo doctrine (Decisions Log #59); the adversarial lens is never "
                + "told what the work was supposed to do, so it does not sample that",
            "ConformanceOnly" =>
                "drops the adversarial lens — it alone hunts defect classes independent of stated intent "
                + "(Decisions Log #59; origin PR #21, four Copilot passes surfaced an injection risk that "
                + "conformance review alone had missed across several cycles)",
            "None" =>
                "drops both lenses — neither one's own attention budget (Decisions Log #59) ever samples "
                + "this diff",
            _ => string.Empty,
        };

        string consequence = guarantee.IsNotBlank() && lens.IsNotBlank()
            ? $"{guarantee}, and {lens}"
            : guarantee.IsNotBlank() ? guarantee : lens;

        return $"{optionName} {composition.Value} {consequence}. The platform advises, the human "
            + $"overrides, but never silently: pass --accept-reduced-review to confirm.";
    }
}

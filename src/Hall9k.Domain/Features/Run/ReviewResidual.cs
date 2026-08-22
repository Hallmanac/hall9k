namespace Hall9k.Domain.Features.Run;

/// <summary>
/// A finding a review track ended on without a reviewer ever confirming it was resolved
/// (Decisions Log #63). Residuals are the price of the severity gate and of scope routing, and
/// recording them is what keeps a merge-ready verdict honest: "settled, three residuals fixed
/// and two routed" is a different claim from "clean", and the run stream should be able to
/// tell them apart years later.
/// </summary>
public sealed record ReviewResidual(
    ReviewLens Lens,
    int Cycle,
    ReviewSeverity Severity,
    ReviewFindingScope Scope,
    ReviewResidualDisposition Disposition,
    string Location);

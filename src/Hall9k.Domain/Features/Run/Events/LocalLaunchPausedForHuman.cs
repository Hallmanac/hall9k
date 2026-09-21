namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The launch reached a step nothing can run for the reviewer and stopped there (idea b9b09779,
/// piece 5). <see cref="StepNumber"/> is the step waiting to be done, not the last one completed,
/// so a <c>--continue</c> resumes from the one after it.
/// </summary>
/// <param name="Instruction">Exactly what the reviewer has to do, as the run skill wrote it — never a paraphrase, since the platform did not understand this step well enough to run it.</param>
public sealed record LocalLaunchPausedForHuman(
    Guid Id,
    Guid LaunchId,
    int StepNumber,
    string Instruction,
    DateTimeOffset PausedAt);

namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// The check said the notes affect the task, so the daemon took it out of the queue and back to Draft and started a plan
/// (cap of one challenge round) to refine it. Appended in the same save as the dequeue, the return to draft, and the plan's
/// start, under a version fence, so the step cannot begin its refinement twice.
/// </summary>
/// <param name="Id">The task's id.</param>
/// <param name="PlanId">The plan that carries the draft through refinement, challenge, and publish.</param>
/// <param name="BegunAt">When it began.</param>
public sealed record FeedbackStepRefinementBegun(Guid Id, Guid PlanId, DateTimeOffset BegunAt);

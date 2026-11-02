using Hall9k.Domain.Features.Plan;
using Hall9k.Domain.Features.Plan.Events;

namespace Hall9k.Tests.TestSupport;

/// <summary>
/// Folds a plan event held as an object, for a test that builds a plan from the events a decider returned. The aggregate
/// itself exposes only its typed <c>Apply</c> methods, which is all production folds through.
/// </summary>
internal static class PlanAggregateExtensions
{
    public static void ApplyEvent(this PlanAggregate plan, object @event)
    {
        switch (@event)
        {
            case PlanStarted started:
                plan.Apply(started);
                break;
            case PlanRefinementDispatched dispatched:
                plan.Apply(dispatched);
                break;
            case PlanChallengeRequested requested:
                plan.Apply(requested);
                break;
            case PlanDraftMoved moved:
                plan.Apply(moved);
                break;
            case PlanParked parked:
                plan.Apply(parked);
                break;
            case PlanResumed resumed:
                plan.Apply(resumed);
                break;
            case PlanDraftPublished published:
                plan.Apply(published);
                break;
            case PlanDraftAbandoned abandoned:
                plan.Apply(abandoned);
                break;
            case PlanPublished ended:
                plan.Apply(ended);
                break;
            case PlanCancelled cancelled:
                plan.Apply(cancelled);
                break;
            case PlanDiscoveryDispatched discovery:
                plan.Apply(discovery);
                break;
            case PlanDiscoveryParked discoveryParked:
                plan.Apply(discoveryParked);
                break;
            case PlanDraftsCut cut:
                plan.Apply(cut);
                break;
            case PlanStopped stopped:
                plan.Apply(stopped);
                break;
            default:
                break;
        }
    }
}

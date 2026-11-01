namespace Hall9k.Domain.Features.Plan;

/// <summary>
/// The default number of challenge rounds a plan runs for one draft. The daemon's <c>PlanRoundCap</c> option defaults to
/// this and is the only place the cap is set, with no node, project, or task setting; <c>h9k plan show</c>, which cannot
/// read a daemon option, prints this default beside each draft's round.
/// </summary>
public static class PlanRoundCap
{
    /// <summary>Two: none of the 27 verdicts recorded before plans existed was a flat do not publish, and only one draft ever had a second challenge.</summary>
    public const int Default = 2;
}

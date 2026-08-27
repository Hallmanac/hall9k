namespace Hall9k.Domain.Features.Run;

/// <summary>
/// The residual counts a settling run reports (Decisions Log #63), counted per <i>defect</i>
/// rather than per recorded residual. The run stream accumulates a residual every time a finding
/// is routed or shipped unreviewed, and one defect can leave more than one: a routing that
/// failed and was retried leaves both records, and so does a routing that failed twice.
/// Counting the records would tell a human "1 routed, 1 not routed" about a single defect that
/// does have a draft bug task, which is the opposite of the honesty the residual record exists
/// for. Fixing unreviewed leaves more than one record for the same reason: both tracks can end
/// on one defect, and one terminal cycle can state one place in two finding blocks.
/// <para>
/// The four counts collapse within themselves, never against each other. A defect one track
/// fixed unreviewed and another exported met both ends, and both are worth reporting.
/// </para>
/// </summary>
/// <param name="FixedUnreviewed">Distinct defects fixed in a track's terminal cycle that no reviewer read again.</param>
/// <param name="Routed">Distinct defects exported to a draft bug task.</param>
/// <param name="RoutingFailed">Distinct defects whose draft bug task could not be created, and never was.</param>
/// <param name="RideAlong">
/// Distinct ride-alongs (Decisions Log #87) never folded into a fix session the run happened to
/// dispatch for another reason — recorded, and never fixed in this pull request.
/// </param>
public sealed record ReviewResidualTally(int FixedUnreviewed, int Routed, int RoutingFailed, int RideAlong);

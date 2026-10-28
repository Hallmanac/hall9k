using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks.Events;
using JasperFx.Events;

namespace Hall9k.Domain.Features.Tasks.Queries;

/// <summary>
/// The classification half of <see cref="TaskPassageQuery"/> (idea 6e6d0200, Card 6): which phases of a
/// task a human ran, a delegated agent ran, the platform ran on its own, or an override skipped.
/// Folded on read from the same events the timings above fold, with nothing added to any event, so
/// every existing stream classifies with no backfill.
/// </summary>
public static partial class TaskPassageQuery
{
    /// <summary>
    /// Whether a run is held by a person rather than dispatched to an agent. Three facts together,
    /// because no one of them is the key: the self-claim sentinel (<see cref="RunDispatched.NodeId"/> is
    /// <see cref="Guid.Empty"/>) is also what <c>h9k task start</c> records on a headless build, and so
    /// is <see cref="TaskClaimed.InteractiveMode"/>. A deliberate headless start is told apart by the
    /// flag where it exists, and by the build role suffix <c>h9k task start</c> has recorded on every
    /// dispatch since it landed, so a run from before the flag still reads as automatic. An
    /// <c>h9k task work</c> claim records <see cref="SessionRoleName.InteractiveClaim"/> or nothing.
    /// </summary>
    public static bool IsHumanHeld(RunDispatched dispatched) =>
        dispatched.NodeId == Guid.Empty
        && !dispatched.IsDeliberateHeadlessStart
        && !EndsWithBuildRole(dispatched.SessionName);

    private static bool EndsWithBuildRole(string sessionName) =>
        sessionName == SessionRoleName.Build
        || sessionName.EndsWith($"-{SessionRoleName.Build}", StringComparison.Ordinal);

    /// <summary>
    /// The claim a run was dispatched under and what, if anything, handed the task back to the
    /// platform just before it: a human-asked reopen (<c>h9k pr resolve</c>) or <c>h9k task handback</c>.
    /// A human-asked reopen that a sentinel claim then follows is a drive, not a hand-off, which the
    /// caller settles by ignoring the hand-off on a human-held run.
    /// </summary>
    private static Dictionary<Guid, (ClaimOwner Owner, PhaseHandOff? HandOff)> FoldClaims(IReadOnlyList<IEvent> taskEvents)
    {
        Dictionary<Guid, (ClaimOwner Owner, PhaseHandOff? HandOff)> claims = [];
        PhaseHandOff? pending = null;
        foreach (IEvent recorded in taskEvents)
        {
            switch (recorded.Data)
            {
                case TaskReopened { Automatic: false }:
                    pending = PhaseHandOff.PullRequestResolve;
                    break;
                case TaskReopened:
                    pending = null;
                    break;
                case TaskHandedBack:
                    pending = PhaseHandOff.Handback;
                    break;
                case TaskClaimed claimed:
                    claims[claimed.RunId] = (new ClaimOwner(claimed.OwnerId, claimed.OwnerRootFingerprint), pending);
                    pending = null;
                    break;
            }
        }

        return claims;
    }

    /// <summary>
    /// The pure fold. Null for a pr-review task (auto-pr-review's Now speed and a reviewer's own
    /// <c>h9k pr review</c> lap both dispatch under the sentinel, and neither is work a person drove
    /// or delegated). Each run contributes, in stream order: its build or, on a follow-up, its
    /// closeout lap; its gates; its review cycles; its pull request open. Work before the run's first
    /// <see cref="AgentSessionCompleted"/> on a human-held run is the person's (or the contractor's,
    /// once a <see cref="RunPhaseDelegated"/> precedes it), including a gate run from <c>h9k task
    /// verify</c>, which the command only allows before that point. Everything else is automatic, bar
    /// what <c>h9k task skip-to-pr</c> skipped and the pull request it opened, which is the person's.
    /// </summary>
    private static PhaseClassification? ClassifyPhases(
        IReadOnlyList<IEvent> taskEvents, IReadOnlyList<RunEventSet> runs, TaskType taskType)
    {
        if (taskType == TaskType.PrReview)
        {
            return null;
        }

        Dictionary<Guid, (ClaimOwner Owner, PhaseHandOff? HandOff)> claims = FoldClaims(taskEvents);
        List<ClassifiedPhase> phases = [];
        int reviewCycleNumber = 0;
        int closeoutLapNumber = 0;

        foreach (RunEventSet run in runs)
        {
            RunDispatched? dispatched = run.Events.Select(recorded => recorded.Data).OfType<RunDispatched>().FirstOrDefault();
            if (dispatched is null)
            {
                continue;
            }

            bool humanHeld = IsHumanHeld(dispatched);
            int sessionCompletedAt = run.Events
                .Select((recorded, index) => (recorded.Data, index))
                .Where(entry => entry.Data is AgentSessionCompleted)
                .Select(entry => entry.index)
                .DefaultIfEmpty(int.MaxValue)
                .First();
            bool delegated = humanHeld && run.Events
                .Take(sessionCompletedAt)
                .Any(recorded => recorded.Data is RunPhaseDelegated);
            PhaseActor leadActor = (humanHeld, delegated) switch
            {
                (false, _) => PhaseActor.Automatic,
                (true, true) => PhaseActor.Delegated,
                _ => PhaseActor.Human,
            };

            bool hasClaim = claims.TryGetValue(run.RunId, out (ClaimOwner Owner, PhaseHandOff? HandOff) claim);
            ClaimOwner owner = hasClaim ? claim.Owner : new ClaimOwner(dispatched.OwnerId, null);
            PhaseHandOff? handOff = humanHeld || !hasClaim ? null : claim.HandOff;

            phases.Add(dispatched.IsFollowUp
                ? new ClassifiedPhase(PhaseKind.CloseoutLap, leadActor, run.RunId, owner, ++closeoutLapNumber, HandOff: handOff)
                : new ClassifiedPhase(PhaseKind.Build, leadActor, run.RunId, owner, HandOff: handOff));

            // A resumed-at-pull-request-open retry never re-records the override, but its settlement
            // says the review it resumes was overridden.
            bool overridden = dispatched.ResumedReviewSettlement?.Settlement == ReviewSettlement.Overridden;
            HashSet<PhaseActor> gateActors = [];
            Dictionary<int, int> cyclePhaseIndex = [];
            int lastCyclePhaseIndex = -1;

            for (int index = 0; index < run.Events.Count; index++)
            {
                switch (run.Events[index].Data)
                {
                    case VerificationPassed or VerificationFailed or VerificationSkipped:
                        PhaseActor gateActor = humanHeld && index < sessionCompletedAt ? leadActor : PhaseActor.Automatic;
                        if (gateActors.Add(gateActor))
                        {
                            phases.Add(new ClassifiedPhase(PhaseKind.Gates, gateActor, run.RunId, owner));
                        }

                        break;
                    case ReviewDispatched reviewDispatched when !cyclePhaseIndex.ContainsKey(reviewDispatched.Cycle):
                        phases.Add(new ClassifiedPhase(
                            PhaseKind.ReviewCycle, PhaseActor.Automatic, run.RunId, owner, ++reviewCycleNumber));
                        lastCyclePhaseIndex = phases.Count - 1;
                        cyclePhaseIndex[reviewDispatched.Cycle] = lastCyclePhaseIndex;
                        break;
                    case ReviewHumanFixApplied applied:
                        int fixedIndex = cyclePhaseIndex.GetValueOrDefault(applied.Cycle, lastCyclePhaseIndex);
                        if (fixedIndex >= 0)
                        {
                            phases[fixedIndex] = phases[fixedIndex] with { FixedByHand = true };
                        }

                        break;
                    case ReviewSkippedToPullRequest skipped:
                        overridden = true;
                        foreach (string entry in skipped.Skipped)
                        {
                            if (entry == ReviewSkipPlan.Gates)
                            {
                                phases.Add(new ClassifiedPhase(PhaseKind.Gates, PhaseActor.Skipped, run.RunId, owner));
                            }
                            else if (ReviewSkipPlan.IsTrack(entry))
                            {
                                phases.Add(new ClassifiedPhase(PhaseKind.ReviewCycle, PhaseActor.Skipped, run.RunId, owner));
                            }

                            // The final full pass and the settling gate are listed too, but the passage
                            // has no phase of its own for either, so the record keeps naming them and
                            // the classification stays to the phases it counts.
                        }

                        break;
                    case PullRequestOpened:
                        phases.Add(overridden
                            ? new ClassifiedPhase(
                                PhaseKind.PullRequestOpen, PhaseActor.Human, run.RunId, owner, UnderSkipOverride: true)
                            : new ClassifiedPhase(PhaseKind.PullRequestOpen, PhaseActor.Automatic, run.RunId, owner));
                        break;
                }
            }
        }

        return new PhaseClassification(phases);
    }
}

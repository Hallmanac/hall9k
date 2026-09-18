using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// A daemon SIGKILLed while a run polls for the node-wide host-coupled-gate permit never gets to
/// run <see cref="VerificationRunner.AcquireHostCoupledGatePermitAsync"/>'s own <c>finally</c>, so
/// no <see cref="RunHostCoupledGateWaitEnded"/> is ever appended and
/// <see cref="RunDetails.HostCoupledGateWaitStartedAt"/> stays set for the rest of the run's life
/// (independent pre-PR review, cycle 3, adversarial lens, low). <see cref="GateStarted"/> is
/// concrete, observed proof that ended in fact regardless: a gate cannot start spawning until any
/// pending permit acquisition for this same run has already returned. Proves the projection clears
/// the stale flag on that event, so it can never mask the "review cycle N" line
/// <c>TaskPhaseComposer.Review</c> falls back to once both <c>ActiveGate</c> and the run's active
/// sessions are empty again.
/// </summary>
public sealed class RunHostCoupledGateWaitProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_gate_starting_clears_a_wait_the_daemon_never_recorded_the_end_of()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = projection.Create(new FakeEvent<RunDispatched>(new RunDispatched(
            id, DomainId.New(), DomainId.New(), DomainId.New(), 1, DomainId.New(),
            "/tmp/wt", "task/host-coupled-wait", ExecutorMode.Subscription, Now)));

        projection.Apply(
            new FakeEvent<RunHostCoupledGateWaitStarted>(new RunHostCoupledGateWaitStarted(id, Now)), view);
        view.HostCoupledGateWaitStartedAt.Should().Be(Now, "the wait is genuinely in progress");

        // No RunHostCoupledGateWaitEnded in between — the SIGKILL-before-restart shape this test
        // exists to prove clears anyway.
        projection.Apply(
            new FakeEvent<GateStarted>(new GateStarted(id, "host", 4242, Now, Now)), view);

        view.HostCoupledGateWaitStartedAt.Should().BeNull(
            "GateStarted could not have fired unless the permit acquisition it followed already " +
            "returned, so the wait ended in fact even though no RunHostCoupledGateWaitEnded says so");
        view.ActiveGate.Should().NotBeNull("the gate itself is still recorded as running, unaffected by the fix");
    }
}

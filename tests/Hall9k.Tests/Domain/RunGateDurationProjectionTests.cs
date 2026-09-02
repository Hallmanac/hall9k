using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Gate wall-clock duration through the run read models (task: gate wall-clock duration is
/// recorded and surfaced): every gate's own duration from the most recently recorded
/// verification, replaced whole rather than accumulated, and a stream written before the field
/// existed reads as unknown, never as a claimed zero.
/// </summary>
public sealed class RunGateDurationProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_passing_verification_records_every_gates_own_duration()
    {
        Guid id = DomainId.New();
        RunListItem view = new RunListItemProjection().Create(new FakeEvent<RunDispatched>(Dispatched(id)));

        GateDuration[] durations =
        [
            new GateDuration("build", TimeSpan.FromSeconds(42), Passed: true),
            new GateDuration("test", TimeSpan.FromMinutes(3), Passed: true),
        ];
        new RunListItemProjection().Apply(
            new FakeEvent<VerificationPassed>(new VerificationPassed(id, Now, null, false, null, null, durations)), view);

        view.GateDurations.Should().Equal(durations);
    }

    [Fact]
    public void A_failing_verification_records_the_gates_that_actually_ran_including_the_failed_one()
    {
        Guid id = DomainId.New();
        RunListItem view = new RunListItemProjection().Create(new FakeEvent<RunDispatched>(Dispatched(id)));

        GateDuration[] durations =
        [
            new GateDuration("build", TimeSpan.FromSeconds(42), Passed: true),
            new GateDuration("test", TimeSpan.FromSeconds(9), Passed: false),
        ];
        new RunListItemProjection().Apply(
            new FakeEvent<VerificationFailed>(new VerificationFailed(id, ["test"], Now, durations)), view);

        view.GateDurations.Should().Equal(durations);
        view.GateDurations!.Last().Passed.Should().BeFalse("the gate that stopped the line is named, not dropped");
    }

    [Fact]
    public void A_later_pass_replaces_the_earlier_one_wholesale_rather_than_accumulating()
    {
        Guid id = DomainId.New();
        RunListItem view = new RunListItemProjection().Create(new FakeEvent<RunDispatched>(Dispatched(id)));
        RunListItemProjection projection = new();

        projection.Apply(
            new FakeEvent<VerificationFailed>(
                new VerificationFailed(id, ["test"], Now, [new GateDuration("test", TimeSpan.FromSeconds(9), Passed: false)])),
            view);
        projection.Apply(
            new FakeEvent<VerificationPassed>(
                new VerificationPassed(
                    id, Now, null, false, null, null, [new GateDuration("test", TimeSpan.FromSeconds(7), Passed: true)])),
            view);

        view.GateDurations.Should().ContainSingle().Which.Should().Be(
            new GateDuration("test", TimeSpan.FromSeconds(7), Passed: true));
    }

    [Fact]
    public void A_verification_recorded_before_the_field_existed_reads_as_unknown_never_as_zero()
    {
        Guid id = DomainId.New();
        RunListItem view = new RunListItemProjection().Create(new FakeEvent<RunDispatched>(Dispatched(id)));

        new RunListItemProjection().Apply(
            new FakeEvent<VerificationPassed>(new VerificationPassed(id, Now)), view);

        view.GateDurations.Should().BeNull(
            "a stream written before this field existed has no observed durations — never a claimed empty list");
    }

    private static RunDispatched Dispatched(Guid id) => new(
        id, DomainId.New(), DomainId.New(), DomainId.New(), 1, DomainId.New(),
        "/tmp/wt", "task/verify", ExecutorMode.Subscription, Now);
}

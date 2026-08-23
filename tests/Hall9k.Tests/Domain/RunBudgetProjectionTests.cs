using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Token-budget exhaustion through the run read models (Decisions Log #40): a park that
/// carries the window-reset reason without failing the run, and a retry that clears it the
/// same way a review park resolution clears ReviewParked's — leaving no stale reason behind.
/// </summary>
public sealed class RunBudgetProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 22, 31, 0, TimeSpan.Zero);

    [Fact]
    public void Run_details_parks_with_the_window_reset_reason_and_stays_unfinished()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = LiveRun(projection, id);

        projection.Apply(new FakeEvent<RunBudgetExhausted>(
            new RunBudgetExhausted(id, "Claude AI usage limit reached|1762952400", Now)), view);

        view.State.Should().Be(RunState.BudgetParked);
        view.ParkedReason.Should().Be("token budget exhausted - resumes when the subscription window resets");
        view.FinishedAt.Should().BeNull("parked is a waiting state, not an ending");
        view.FailureReason.Should().BeNull("the observed message is not read as a failure");
    }

    [Fact]
    public void Run_details_clears_the_park_when_the_retry_resumes_it()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = LiveRun(projection, id);

        projection.Apply(new FakeEvent<RunBudgetExhausted>(
            new RunBudgetExhausted(id, "Claude AI usage limit reached|1762952400", Now)), view);
        projection.Apply(new FakeEvent<RunResumed>(new RunResumed(id, 4999, Now)), view);

        view.State.Should().Be(RunState.Running, "the retry sweep clears the hold with no human act");
        view.ParkedReason.Should().BeNull("h9k status must stop showing the reason once the retry is live");
    }

    [Fact]
    public void Run_list_item_walks_running_to_budget_parked_and_back()
    {
        RunListItemProjection projection = new();
        Guid id = DomainId.New();
        RunListItem view = projection.Create(new FakeEvent<RunDispatched>(new RunDispatched(
            id, DomainId.New(), DomainId.New(), DomainId.New(), 1, DomainId.New(),
            "/wt/x", "task/x", ExecutorMode.Subscription, Now)));
        projection.Apply(new FakeEvent<RunProcessStarted>(new RunProcessStarted(id, 4482, Now)), view);

        projection.Apply(new FakeEvent<RunBudgetExhausted>(
            new RunBudgetExhausted(id, "Claude AI usage limit reached|1762952400", Now)), view);
        view.State.Should().Be(RunState.BudgetParked);

        projection.Apply(new FakeEvent<RunResumed>(new RunResumed(id, 4999, Now)), view);
        view.State.Should().Be(RunState.Running);
    }

    private static RunDetails LiveRun(RunDetailsProjection projection, Guid id)
    {
        RunDetails view = projection.Create(new FakeEvent<RunDispatched>(new RunDispatched(
            id, DomainId.New(), DomainId.New(), DomainId.New(), 1, DomainId.New(),
            "/wt/x", "task/x", ExecutorMode.Subscription, Now)));
        projection.Apply(new FakeEvent<RunProcessStarted>(new RunProcessStarted(id, 4482, Now)), view);
        return view;
    }
}

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
/// The model a session ran on is an observed fact on the run stream (Decisions Log #33),
/// which is what turns spend-by-model from a guess into a query, given the per-run token
/// accounting from log #30. The corollary matters just as much: a run dispatched before
/// the chain existed recorded no model, and replays as Unknown rather than as whatever we
/// would guess it must have been.
/// </summary>
public sealed class RunModelRecordTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_aggregate_records_the_model_of_the_build_review_and_fix_sessions()
    {
        Guid id = DomainId.New();
        Guid reviewSession = DomainId.New();
        RunAggregate run = new();

        run.Apply(Dispatched(id, AgentModel.FromInput("claude-opus-5")));
        run.Model.Value.Should().Be("claude-opus-5");

        run.Apply(new ReviewDispatched(id, reviewSession, 1, 5001, Now, Now, AgentModel.Sonnet));
        run.ActiveReviewSessionModel.Should().Be(AgentModel.Sonnet);

        run.Apply(new ReviewCompleted(id, 1, ReviewVerdict.NeedsFixes, Now));
        run.LastReviewSessionModel.Should().Be(
            AgentModel.Sonnet, "the resume target's model is kept; a re-prompt records it rather than re-resolving");
        run.ActiveReviewSessionModel.Should().Be(AgentModel.Unknown, "no session is in flight between legs");

        run.Apply(new ReviewFixDispatched(id, DomainId.New(), 1, 5002, Now, Now, AgentModel.Haiku));
        run.ActiveReviewSessionModel.Should().Be(
            AgentModel.Haiku, "fix is its own role and resolves its own model");
        run.Model.Value.Should().Be("claude-opus-5", "the build session's record is never overwritten by a later leg");
    }

    [Fact]
    public void A_verdict_reprompt_records_the_model_the_resumed_session_already_runs_on()
    {
        Guid id = DomainId.New();
        Guid reviewSession = DomainId.New();
        RunAggregate run = new();
        run.Apply(Dispatched(id, AgentModel.FromInput("claude-opus-5")));
        run.Apply(new ReviewDispatched(id, reviewSession, 1, 5001, Now, Now, AgentModel.Sonnet));
        run.Apply(new ReviewCompleted(id, 1, ReviewVerdict.Unknown, Now));

        run.Apply(new ReviewVerdictReprompted(
            id, DomainId.New(), reviewSession, 1, 5003, Now, Now, run.LastReviewSessionModel));

        run.ActiveReviewSessionModel.Should().Be(
            AgentModel.Sonnet, "the resumed session keeps the model it started with");
        run.LastReviewSessionId.Should().Be(reviewSession);
    }

    [Fact]
    public void Runs_dispatched_before_the_chain_existed_replay_as_unknown_rather_than_as_a_guess()
    {
        Guid id = DomainId.New();
        RunAggregate run = new();

        run.Apply(new RunDispatched(
            id, DomainId.New(), DomainId.New(), DomainId.New(), 1, DomainId.New(),
            "/tmp/wt", "task/old", ExecutorMode.Subscription, Now));

        run.Model.Should().Be(AgentModel.Unknown, "the unobserved is admitted, never plausibly filled in");
    }

    [Fact]
    public void Both_run_projections_carry_the_model_forward()
    {
        Guid id = DomainId.New();
        RunDispatched dispatched = Dispatched(id, AgentModel.FromInput("claude-opus-5[1m]"));

        RunDetails details = new RunDetailsProjection().Create(new FakeEvent<RunDispatched>(dispatched));
        details.Model.Value.Should().Be("claude-opus-5[1m]");

        RunListItem listItem = new RunListItemProjection().Create(new FakeEvent<RunDispatched>(dispatched));
        listItem.Model.Value.Should().Be(
            "claude-opus-5[1m]", "h9k task show reads the lean row, so the model has to live there too");
    }

    [Fact]
    public void Run_details_tracks_the_latest_review_leg_model_separately_from_the_build_model()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = projection.Create(new FakeEvent<RunDispatched>(
            Dispatched(id, AgentModel.FromInput("claude-opus-5"))));

        projection.Apply(new FakeEvent<ReviewDispatched>(
            new ReviewDispatched(id, DomainId.New(), 1, 5001, Now, Now, AgentModel.Sonnet)), view);
        view.ReviewModel.Should().Be(AgentModel.Sonnet);

        projection.Apply(new FakeEvent<ReviewFixDispatched>(
            new ReviewFixDispatched(id, DomainId.New(), 1, 5002, Now, Now, AgentModel.Haiku)), view);
        view.ReviewModel.Should().Be(AgentModel.Haiku);
        view.Model.Value.Should().Be("claude-opus-5", "the build session's model stays what it was");
    }

    private static RunDispatched Dispatched(Guid id, AgentModel model) => new(
        id, DomainId.New(), DomainId.New(), DomainId.New(), 1, DomainId.New(),
        "/tmp/wt", "task/model", ExecutorMode.Subscription, Now, IsFollowUp: false, Model: model);
}

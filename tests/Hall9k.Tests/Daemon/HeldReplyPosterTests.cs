using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The decide-and-post step for a lap's held fix replies (task: a review-feedback lap's fix reply
/// posts only after the platform's push has moved the pull request's head), against a fake
/// <c>gh</c> and the run's own projection. No test here runs git or reaches GitHub: the head the
/// lap started from and the pushed tip are inputs, and every call the step would have made is
/// recorded rather than sent.
/// </summary>
public sealed class HeldReplyPosterTests
{
    private const string PullRequestUrl = "https://github.com/x/y/pull/2220";

    private const string ReviewUrl = $"{PullRequestUrl}#pullrequestreview-345";

    private const string Started = "1111111aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private const string Pushed = "2222222bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private const string RepositoryPath = "/repo/y";

    private static readonly DateTimeOffset Now = new(2026, 10, 8, 14, 13, 41, TimeSpan.Zero);

    private static readonly PushedHeads Moved = new(Started, Pushed);

    [Fact]
    public async Task A_moved_head_posts_the_reply_with_the_push_line_resolves_the_thread_and_records_the_post()
    {
        Harness harness = new();
        Guid replyId = harness.Hold(threadId: "PRRT_a", body: "Reused the sentinel now.");

        await harness.PostAsync(Moved);

        harness.Gh.Calls.Should().HaveCount(2, "the reply, then the resolve");
        IReadOnlyList<string> reply = harness.Gh.Calls[0].Arguments;
        reply.Should().Contain("threadId=PRRT_a");
        string body = reply.Single(argument => argument.StartsWith("body=", StringComparison.Ordinal));
        body.Should().StartWith("body=Reused the sentinel now.\n\n");
        body.Should().Contain("1111111").And.Contain("2222222");
        body.Should().Contain($"https://github.com/x/y/compare/{Started}..{Pushed}", "GitHub's two-dot compare");
        body.Should().NotContain("—");
        harness.Gh.Calls[0].WorkingDirectory.Should().Be(RepositoryPath);
        harness.Gh.Calls[1].Arguments.Should().Contain("threadId=PRRT_a").And.Contain(
            argument => argument.Contains("resolveReviewThread", StringComparison.Ordinal));

        ReviewThreadReplyPosted posted = harness.Recorded.OfType<ReviewThreadReplyPosted>().Should().ContainSingle().Subject;
        posted.ThreadId.Should().Be("PRRT_a");
        posted.HeldReplyId.Should().Be(replyId);
        harness.Run.ReviewThreadRepliesPosted.Should().ContainSingle();
        harness.Run.HeldReplies.Should().ContainSingle().Which.PostedAt.Should().NotBeNull();
        TaskShowCommand.ComposeHeldReplies([harness.Run], taskAbandoned: false).Should().BeEmpty(
            "a reply that posted is the pull request's business");
    }

    [Fact]
    public async Task A_fix_answer_to_a_review_body_waits_and_then_posts_as_a_comment_with_the_push_line()
    {
        Harness harness = new();
        harness.Hold(reviewUrl: ReviewUrl, body: "Every point is fixed above.", humanAuthored: true);

        await harness.PostAsync(Moved);

        IReadOnlyList<string> call = harness.Gh.Calls.Should().ContainSingle("a review body has no thread to resolve")
            .Subject.Arguments;
        call.Should().StartWith(["pr", "comment", "2220"]);
        string body = call[call.ToList().IndexOf("--body") + 1];
        body.Should().StartWith($"On {ReviewUrl}:\n\nEvery point is fixed above.\n\n");
        body.Should().Contain("compare/").And.Contain("1111111");

        ReviewBodyReplyPosted posted = harness.Recorded.OfType<ReviewBodyReplyPosted>().Should().ContainSingle().Subject;
        posted.ReviewUrl.Should().Be(ReviewUrl);
        posted.ReviewIsHumanAuthored.Should().BeTrue();
        harness.Run.ReviewBodyRepliesPosted.Should().ContainSingle();
    }

    [Fact]
    public async Task A_push_that_left_the_head_where_it_started_posts_nothing_and_shows_as_withheld()
    {
        Harness harness = new();
        harness.Hold(threadId: "PRRT_a", body: "Reused the sentinel now.");

        await harness.PostAsync(new PushedHeads(Started, Started));

        harness.Gh.Calls.Should().BeEmpty();
        ReviewReplyWithheld withheld = harness.Recorded.OfType<ReviewReplyWithheld>().Should().ContainSingle().Subject;
        withheld.Reason.Should().Contain("did not move");
        string shown = string.Join('\n', TaskShowCommand.ComposeHeldReplies([harness.Run], taskAbandoned: false));
        shown.Should().Contain("withheld").And.Contain("thread PRRT_a").And.Contain("did not move")
            .And.Contain("Reused the sentinel now.");
    }

    [Theory]
    [InlineData(null, Pushed)]
    [InlineData(Started, null)]
    [InlineData(null, null)]
    public async Task A_head_that_is_not_known_withholds_the_reply(string? startedFrom, string? pushedTip)
    {
        Harness harness = new();
        harness.Hold(threadId: "PRRT_a", body: "Reused the sentinel now.");

        await harness.PostAsync(new PushedHeads(startedFrom, pushedTip));

        harness.Gh.Calls.Should().BeEmpty();
        harness.Recorded.OfType<ReviewReplyWithheld>().Should().ContainSingle()
            .Which.Reason.Should().Contain("no way to tell");
    }

    [Fact]
    public async Task A_second_pass_over_the_same_run_posts_nothing_new()
    {
        Harness harness = new();
        harness.Hold(threadId: "PRRT_a", body: "Reused the sentinel now.");
        await harness.PostAsync(Moved);
        int callsAfterFirstPass = harness.Gh.Calls.Count;

        await harness.PostAsync(Moved);

        harness.Gh.Calls.Should().HaveCount(callsAfterFirstPass, "a reply the run already recorded as posted stays posted");
        harness.Recorded.OfType<ReviewThreadReplyPosted>().Should().ContainSingle();
    }

    [Fact]
    public async Task One_failed_post_leaves_that_thread_open_and_the_others_still_post()
    {
        Harness harness = new(arguments => arguments.Contains("threadId=PRRT_a") && IsReply(arguments)
            ? new ProcessResult(1, string.Empty, "GraphQL: Could not resolve to a node with the global id of 'PRRT_a'")
            : new ProcessResult(0, "{}", string.Empty));
        harness.Hold(threadId: "PRRT_a", body: "First.");
        harness.Hold(threadId: "PRRT_b", body: "Second.");

        await harness.PostAsync(Moved);

        harness.Gh.Calls.Select(call => call.Arguments).Should().NotContain(
            arguments => arguments.Contains("threadId=PRRT_a") && !IsReply(arguments),
            "a thread whose reply never landed is not resolved");
        harness.Recorded.OfType<ReviewThreadReplyPosted>().Should().ContainSingle().Which.ThreadId.Should().Be("PRRT_b");
        harness.Recorded.OfType<ReviewReplyWithheld>().Should().ContainSingle().Which.Reason
            .Should().Contain("the post failed").And.Contain("Could not resolve to a node");
        harness.Logger.Messages.Should().Contain(message => message.Contains("Could not resolve to a node"));
        string shown = string.Join('\n', TaskShowCommand.ComposeHeldReplies([harness.Run], taskAbandoned: false));
        shown.Should().Contain("PRRT_a").And.NotContain("PRRT_b");
    }

    [Fact]
    public async Task A_failed_resolve_is_logged_and_does_not_undo_the_recorded_post()
    {
        Harness harness = new(arguments => IsReply(arguments)
            ? new ProcessResult(0, "{}", string.Empty)
            : new ProcessResult(1, string.Empty, "Resource not accessible by integration"));
        harness.Hold(threadId: "PRRT_a", body: "First.");

        await harness.PostAsync(Moved);

        harness.Recorded.OfType<ReviewThreadReplyPosted>().Should().ContainSingle();
        harness.Recorded.OfType<ReviewReplyWithheld>().Should().BeEmpty("the reply is on the pull request");
        harness.Logger.Messages.Should().Contain(message => message.Contains("Resource not accessible by integration"));
    }

    [Fact]
    public async Task A_fix_reply_into_a_persons_thread_that_the_closing_triage_calls_a_decline_is_withheld()
    {
        Harness harness = new();
        harness.Hold(threadId: "PRRT_person", body: "Reused the sentinel now.", humanAuthored: true);
        harness.Run.ReviewThreadOutcomes.Add(
            new ReviewThreadOutcome("PRRT_person", ReviewThreadDisposition.Decline, "It already handles that."));

        await harness.PostAsync(Moved);

        harness.Gh.Calls.Should().BeEmpty();
        harness.Recorded.OfType<ReviewReplyWithheld>().Should().ContainSingle().Which.Reason
            .Should().Contain("decline");

        // The run parks, resumes, and pushes: the same triage is still on the run, so it stays withheld.
        await harness.PostAsync(Moved);
        harness.Gh.Calls.Should().BeEmpty();
        harness.Recorded.OfType<ReviewReplyWithheld>().Should().ContainSingle("the same decision says nothing new");
    }

    [Fact]
    public async Task A_decline_triage_on_a_bots_thread_does_not_withhold_the_fix_reply()
    {
        Harness harness = new();
        harness.Hold(threadId: "PRRT_bot", body: "Reused the sentinel now.", humanAuthored: false);
        harness.Run.ReviewThreadOutcomes.Add(
            new ReviewThreadOutcome("PRRT_bot", ReviewThreadDisposition.Decline, "It already handles that."));

        await harness.PostAsync(Moved);

        harness.Recorded.OfType<ReviewThreadReplyPosted>().Should().ContainSingle();
    }

    [Fact]
    public void The_triage_check_finds_only_waiting_replies_into_a_persons_thread_the_triage_contradicts()
    {
        Harness harness = new();
        harness.Hold(threadId: "PRRT_person", body: "One.", humanAuthored: true);
        harness.Hold(threadId: "PRRT_other", body: "Two.", humanAuthored: true);
        harness.Hold(threadId: "PRRT_bot", body: "Three.", humanAuthored: false);

        IReadOnlyList<(HeldReplyRecord Held, ReviewThreadDisposition Triaged)> contradicted =
            HeldReplyPoster.ContradictedByTriage(
                harness.Run,
                [
                    new ReviewThreadOutcome("PRRT_person", ReviewThreadDisposition.Route, "Out of scope."),
                    new ReviewThreadOutcome("PRRT_other", ReviewThreadDisposition.Fix, "Real."),
                    new ReviewThreadOutcome("PRRT_bot", ReviewThreadDisposition.Decline, "False."),
                ]);

        contradicted.Should().ContainSingle().Which.Held.ThreadId.Should().Be("PRRT_person");
        contradicted[0].Triaged.Should().Be(ReviewThreadDisposition.Route);
    }

    [Fact]
    public void A_parked_run_keeps_its_replies_waiting_for_the_push()
    {
        Harness harness = new();
        harness.Hold(threadId: "PRRT_a", body: "Reused the sentinel now.");
        harness.Run.State = RunState.ReviewParked;

        string shown = string.Join('\n', TaskShowCommand.ComposeHeldReplies([harness.Run], taskAbandoned: false));

        shown.Should().Contain("waiting for the push").And.Contain("thread PRRT_a")
            .And.Contain("Reused the sentinel now.");
        harness.Recorded.Should().OnlyContain(@event => @event is ReviewReplyHeld);
    }

    [Theory]
    [InlineData("Failed", false, "failed")]
    [InlineData("Killed", false, "killed")]
    [InlineData("Superseded", false, "superseded")]
    [InlineData("Running", true, "abandoned")]
    public void A_run_that_ended_before_the_push_shows_its_replies_as_withheld_with_the_reason(
        string state, bool taskAbandoned, string expected)
    {
        Harness harness = new();
        harness.Hold(threadId: "PRRT_a", body: "Reused the sentinel now.");
        harness.Run.State = state switch
        {
            "Failed" => RunState.Failed,
            "Killed" => RunState.Killed,
            "Superseded" => RunState.Superseded,
            _ => RunState.Running,
        };

        string shown = string.Join('\n', TaskShowCommand.ComposeHeldReplies([harness.Run], taskAbandoned));

        shown.Should().Contain("withheld").And.Contain(expected).And.NotContain("waiting for the push");
    }

    [Fact]
    public void A_second_hold_for_the_same_thread_replaces_the_one_still_waiting()
    {
        Harness harness = new();
        harness.Hold(threadId: "PRRT_a", body: "First word.");
        harness.Hold(threadId: "PRRT_a", body: "Second word.");

        harness.Run.HeldReplies.Should().ContainSingle().Which.Body.Should().Be("Second word.");
    }

    [Fact]
    public void The_push_line_names_both_short_shas_and_degrades_without_a_link_for_a_non_github_url()
    {
        HeldReplyPoster.PushLine(PullRequestUrl, Moved)
            .Should().Be($"Carried by the push that moved this pull request from 1111111 to 2222222: "
                + $"https://github.com/x/y/compare/{Started}..{Pushed}");
        HeldReplyPoster.PushLine("not a url", Moved)
            .Should().Be("Carried by the push that moved this pull request from 1111111 to 2222222.");
    }

    private static bool IsReply(IReadOnlyList<string> arguments) =>
        arguments.Any(argument => argument.Contains("addPullRequestReviewThreadReply", StringComparison.Ordinal));

    /// <summary>One run's view, a recording fake <c>gh</c>, and a recorder that applies each event to the view as the real one would persist it.</summary>
    private sealed class Harness
    {
        private readonly RunDetailsProjection projection = new();

        public Harness(Func<IReadOnlyList<string>, ProcessResult>? respond = null)
        {
            Gh = new RecordingProcessRunner(respond ?? (_ => new ProcessResult(0, "{}", string.Empty)));
            Run = new RunDetails { Id = RunId, State = RunState.Verifying };
        }

        public Guid RunId { get; } = DomainId.New();

        public RunDetails Run { get; }

        public RecordingProcessRunner Gh { get; }

        public RenderingLogger Logger { get; } = new();

        public List<object> Recorded { get; } = [];

        public Guid Hold(
            string? threadId = null, string? reviewUrl = null, string body = "", bool humanAuthored = false)
        {
            Guid replyId = DomainId.New();
            Apply(new ReviewReplyHeld(
                RunId, replyId, threadId, reviewUrl, ReviewThreadDisposition.Fix, humanAuthored, body, Now));
            return replyId;
        }

        public Task PostAsync(PushedHeads heads) =>
            new HeldReplyPoster(Logger, new GitHubReviewReplies(Gh.Runner), (@event, _) =>
            {
                Apply(@event);
                return Task.CompletedTask;
            }).PostAsync(Run, heads, RepositoryPath, PullRequestUrl, CancellationToken.None);

        private void Apply(object @event)
        {
            Recorded.Add(@event);
            switch (@event)
            {
                case ReviewReplyHeld held:
                    projection.Apply(new FakeEvent<ReviewReplyHeld>(held), Run);
                    break;
                case ReviewReplyWithheld withheld:
                    projection.Apply(new FakeEvent<ReviewReplyWithheld>(withheld), Run);
                    break;
                case ReviewThreadReplyPosted posted:
                    projection.Apply(new FakeEvent<ReviewThreadReplyPosted>(posted), Run);
                    break;
                case ReviewBodyReplyPosted posted:
                    projection.Apply(new FakeEvent<ReviewBodyReplyPosted>(posted), Run);
                    break;
                default:
                    throw new InvalidOperationException($"The harness has no projection for {@event.GetType().Name}.");
            }
        }
    }

    /// <summary>An <see cref="ILogger"/> that renders its messages, exception text included, so a test can read what an operator would.</summary>
    private sealed class RenderingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception.Message));

        private sealed class NullScope : IDisposable
        {
            internal static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}

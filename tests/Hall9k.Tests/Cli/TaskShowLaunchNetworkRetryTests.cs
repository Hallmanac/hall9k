using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Spectre.Console;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// What <c>h9k task show</c> says while a task waits out a launch network backoff, and that it
/// stops saying so once the projection clears the wait.
/// </summary>
public sealed class TaskShowLaunchNetworkRetryTests
{
    [Fact]
    public void A_waiting_task_says_it_will_retry_with_the_attempt_the_bound_the_earliest_claim_and_gits_error()
    {
        DateTimeOffset notBefore = new(2026, 10, 11, 12, 2, 0, TimeSpan.Zero);
        TaskDetails details = new()
        {
            State = TaskState.Queued,
            LaunchNetworkRetryAttempt = 2,
            LaunchNetworkRetryBound = 3,
            LaunchNetworkRetryNotBefore = notBefore,
            LaunchNetworkRetryError = "fatal: unable to access 'https://github.com/x/y.git/': Could not resolve host: github.com",
        };

        string? markup = TaskShowCommand.LaunchNetworkRetryMarkup(details);

        markup.Should().Contain("waiting to retry after a network error");
        markup.Should().Contain("attempt 2 of 3");
        markup.Should().Contain(notBefore.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        markup.Should().Contain("Could not resolve host: github.com");
        Action render = () => _ = new Markup(markup!);
        render.Should().NotThrow("the row is rendered by Spectre, which rejects a malformed tag");
    }

    [Fact]
    public void A_task_that_has_moved_on_from_the_queue_says_nothing_even_with_a_stale_wait()
    {
        TaskDetails details = new()
        {
            State = TaskState.Claimed,
            LaunchNetworkRetryAttempt = 1,
            LaunchNetworkRetryBound = 3,
            LaunchNetworkRetryNotBefore = DateTimeOffset.UtcNow,
        };

        TaskShowCommand.LaunchNetworkRetryMarkup(details).Should().BeNull();
    }

    [Fact]
    public void A_task_that_is_not_waiting_says_nothing()
    {
        TaskShowCommand.LaunchNetworkRetryMarkup(new TaskDetails()).Should().BeNull();
    }

    [Fact]
    public void Gits_error_cannot_inject_markup_into_the_line()
    {
        TaskDetails details = new()
        {
            State = TaskState.Queued,
            LaunchNetworkRetryAttempt = 1,
            LaunchNetworkRetryBound = 3,
            LaunchNetworkRetryNotBefore = DateTimeOffset.UtcNow,
            LaunchNetworkRetryError = "Could not resolve host: [red]x[/]",
        };

        TaskShowCommand.LaunchNetworkRetryMarkup(details).Should().Contain("[[red]]x[[/]]");
    }
}

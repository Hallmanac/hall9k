using FluentAssertions;
using Hall9k.Connectors.WorkItems;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The auto-pr-review feature's own GitHub discovery seam (idea e5e98a33): reading back this
/// install's own authenticated login fresh, listing which open pull requests currently
/// review-request it, and attributing the most recent request or withdrawal to an actor and a
/// moment — the one read nothing in this codebase already does, since every existing GitHub read
/// starts from a pull request the platform already knows the number of.
/// </summary>
public sealed class GitHubReviewAssignmentsTests
{
    [Fact]
    public async Task CurrentLoginAsync_reads_the_login_gh_reports_right_now()
    {
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("brian\n");

        string? login = await new GitHubReviewAssignments(gh.Runner).CurrentLoginAsync("/repos/hall9k", CancellationToken.None);

        login.Should().Be("brian");
        gh.Calls.Single().Arguments.Should().ContainInOrder("api", "user", "-q", ".login");
    }

    [Fact]
    public async Task CurrentLoginAsync_reads_null_rather_than_throwing_when_gh_cannot_answer()
    {
        RecordingProcessRunner gh = RecordingProcessRunner.Failing("gh: not authenticated");

        string? login = await new GitHubReviewAssignments(gh.Runner).CurrentLoginAsync("/repos/hall9k", CancellationToken.None);

        login.Should().BeNull("a poll that cannot read the login counts as a failed inspection, never a crash");
    }

    private const string ReviewRequestedJson = """
        [
          {
            "number": 42,
            "url": "https://github.com/acme/widgets/pull/42",
            "title": "Add rate limiting to auth endpoints",
            "body": "Fixes #17."
          },
          {
            "number": 7,
            "url": "https://github.com/acme/widgets/pull/7",
            "title": "Tighten CORS headers",
            "body": null
          }
        ]
        """;

    [Fact]
    public void ParseReviewRequested_maps_every_open_pull_request_gh_reported()
    {
        IReadOnlyList<ReviewRequestedPullRequest> found = GitHubReviewAssignments.ParseReviewRequested(ReviewRequestedJson);

        found.Should().HaveCount(2);
        found[0].Number.Should().Be(42);
        found[0].Url.Should().Be("https://github.com/acme/widgets/pull/42");
        found[0].Title.Should().Be("Add rate limiting to auth endpoints");
        found[0].Body.Should().Be("Fixes #17.");
        found[1].Body.Should().BeNull("a null body is a fact, never guessed at as a summary of the title");
    }

    [Fact]
    public void ParseReviewRequested_reads_an_empty_array_as_no_matches()
    {
        GitHubReviewAssignments.ParseReviewRequested("[]").Should().BeEmpty();
    }

    private const string TimelineJson = """
        {
          "data": {
            "repository": {
              "pullRequest": {
                "timelineItems": {
                  "nodes": [
                    {
                      "createdAt": "2026-09-01T10:00:00Z",
                      "actor": { "login": "ryan" },
                      "requestedReviewer": { "__typename": "User", "login": "brian" }
                    },
                    {
                      "createdAt": "2026-09-02T14:30:00Z",
                      "actor": { "login": "someone-else" },
                      "requestedReviewer": { "__typename": "User", "login": "not-brian" }
                    },
                    {
                      "createdAt": "2026-09-03T09:15:00Z",
                      "actor": { "login": "ryan" },
                      "requestedReviewer": { "__typename": "User", "login": "brian" }
                    }
                  ]
                }
              }
            }
          }
        }
        """;

    [Fact]
    public void ParseMostRecentRequestActor_walks_backward_to_the_newest_matching_entry()
    {
        // The middle node names a different reviewer entirely and must not win just because it
        // sits closer to some naive midpoint — only the login this call actually asked about
        // ever counts, and among those the most recent (last, since timelineItems' own `last:`
        // ordering is oldest-first) is the one returned.
        ReviewRequestActor actor = GitHubReviewAssignments.ParseMostRecentRequestActor(TimelineJson, "brian");

        actor.Login.Should().Be("ryan");
        actor.RequestedAt.Should().Be(new DateTimeOffset(2026, 9, 3, 9, 15, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ParseMostRecentRequestActor_is_honestly_null_when_the_login_never_appears()
    {
        ReviewRequestActor actor = GitHubReviewAssignments.ParseMostRecentRequestActor(TimelineJson, "nobody-ever-requested");

        actor.Login.Should().BeNull();
        actor.RequestedAt.Should().BeNull();
    }
}

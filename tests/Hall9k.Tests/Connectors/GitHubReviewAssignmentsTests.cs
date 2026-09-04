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
                      "__typename": "ReviewRequestedEvent",
                      "createdAt": "2026-09-01T10:00:00Z",
                      "actor": { "login": "ryan" },
                      "requestedReviewer": { "__typename": "User", "login": "brian" }
                    },
                    {
                      "__typename": "ReviewRequestedEvent",
                      "createdAt": "2026-09-02T14:30:00Z",
                      "actor": { "login": "someone-else" },
                      "requestedReviewer": { "__typename": "User", "login": "not-brian" }
                    },
                    {
                      "__typename": "ReviewRequestedEvent",
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
        ReviewRequestActor actor = GitHubReviewAssignments.ParseMostRecentRequestActor(
            TimelineJson, "brian", ReviewTimelineEventKind.Requested);

        actor.Login.Should().Be("ryan");
        actor.RequestedAt.Should().Be(new DateTimeOffset(2026, 9, 3, 9, 15, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ParseMostRecentRequestActor_is_honestly_null_when_the_login_never_appears()
    {
        ReviewRequestActor actor = GitHubReviewAssignments.ParseMostRecentRequestActor(
            TimelineJson, "nobody-ever-requested", ReviewTimelineEventKind.Requested);

        actor.Login.Should().BeNull();
        actor.RequestedAt.Should().BeNull();
    }

    private const string RequestWithNoRemovalJson = """
        {
          "data": {
            "repository": {
              "pullRequest": {
                "timelineItems": {
                  "nodes": [
                    {
                      "__typename": "ReviewRequestedEvent",
                      "createdAt": "2026-09-01T10:00:00Z",
                      "actor": { "login": "alice" },
                      "requestedReviewer": { "__typename": "User", "login": "brian" }
                    }
                  ]
                }
              }
            }
          }
        }
        """;

    /// <summary>
    /// The misattribution the independent pre-PR review found (adversarial lens, cycle 1): asking
    /// for the most recent event of either kind used to hand the requester's own name back as the
    /// "recaller" whenever a pull request carried no removal event at all. Restricting the walk to
    /// <see cref="ReviewTimelineEventKind.Removed"/> must come back honestly empty instead.
    /// </summary>
    [Fact]
    public void ParseMostRecentRequestActor_never_attributes_a_request_as_its_own_recall()
    {
        ReviewRequestActor actor = GitHubReviewAssignments.ParseMostRecentRequestActor(
            RequestWithNoRemovalJson, "brian", ReviewTimelineEventKind.Removed);

        actor.Login.Should().BeNull("alice requested; nobody has recalled anything");
        actor.RequestedAt.Should().BeNull();
    }

    private const string RequestThenRemovalJson = """
        {
          "data": {
            "repository": {
              "pullRequest": {
                "timelineItems": {
                  "nodes": [
                    {
                      "__typename": "ReviewRequestedEvent",
                      "createdAt": "2026-09-01T10:00:00Z",
                      "actor": { "login": "alice" },
                      "requestedReviewer": { "__typename": "User", "login": "brian" }
                    },
                    {
                      "__typename": "ReviewRequestRemovedEvent",
                      "createdAt": "2026-09-02T11:00:00Z",
                      "actor": { "login": "carol" },
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
    public void ParseMostRecentRequestActor_finds_the_removal_when_one_exists()
    {
        ReviewRequestActor actor = GitHubReviewAssignments.ParseMostRecentRequestActor(
            RequestThenRemovalJson, "brian", ReviewTimelineEventKind.Removed);

        actor.Login.Should().Be("carol");
    }

    [Fact]
    public void ParseMostRecentRequestActor_asked_for_the_request_ignores_a_later_removal()
    {
        ReviewRequestActor actor = GitHubReviewAssignments.ParseMostRecentRequestActor(
            RequestThenRemovalJson, "brian", ReviewTimelineEventKind.Requested);

        actor.Login.Should().Be("alice");
    }

    [Fact]
    public void ParseMostRecentRequestActor_reads_a_pull_request_GraphQL_cannot_resolve_as_unattributed()
    {
        const string nullPullRequestJson = """{"data":{"repository":{"pullRequest":null}}}""";

        ReviewRequestActor actor = GitHubReviewAssignments.ParseMostRecentRequestActor(
            nullPullRequestJson, "brian", ReviewTimelineEventKind.Requested);

        actor.Login.Should().BeNull();
        actor.RequestedAt.Should().BeNull();
    }
}

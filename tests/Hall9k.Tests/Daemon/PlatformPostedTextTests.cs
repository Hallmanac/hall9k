using System.Text;
using FluentAssertions;
using Hall9k.Connectors.Prompts;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon.Closeout;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using PrSummary = Hall9k.Connectors.Prompts.PrSummaryParser.PrSummary;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// Text the platform itself posts to a card, an issue, or a pull request goes out under the
/// owner's login, so it must not advertise the platform. Every input here is free of the words
/// being banned, which means a hit in an output can only have come from the platform's own text.
/// </summary>
public sealed class PlatformPostedTextTests
{
    private const string PullRequestUrl = "https://github.com/acme/widgets/pull/12";
    private const string Started = "1111111111111111111111111111111111111111";
    private const string Pushed = "2222222222222222222222222222222222222222";

    public static TheoryData<string, string> Posts()
    {
        string cutBody = string.Join('\n', Enumerable.Repeat("A line of ordinary prose.", 1500));
        return new TheoryData<string, string>
        {
            { "merge note", CloseoutEngine.MergeComment(PullRequestUrl) },
            { "merge note, issue closed", CloseoutEngine.MergeComment(PullRequestUrl, closed: true) },
            { "body fallback", PullRequestBody.Build(Run(), Task(), "Did the work.", sourceUrl: null) },
            { "authored body", PullRequestBody.Build(Run(), Task(), null, null, new PrSummary("Title", "Prose.")) },
            { "truncated body", PullRequestBody.Build(Run(), Task(), null, null, new PrSummary("Title", cutBody)) },
            { "held reply push line", HeldReplyPoster.PushLine(PullRequestUrl, new PushedHeads(Started, Pushed)) },
            { "held reply push line, no link", HeldReplyPoster.PushLine("not a url", new PushedHeads(Started, Pushed)) },
            { "review body prefix", GitHubReviewReplies.ReviewBodyComment($"{PullRequestUrl}#pullrequestreview-9", "Thanks.") },
        };
    }

    [Theory]
    [MemberData(nameof(Posts))]
    public void Platform_posted_text_never_names_the_platform(string post, string text)
    {
        text.Should().NotContainEquivalentOf("hall9k", "the {0} is posted under the owner's login", post)
            .And.NotContainEquivalentOf("h9k", "the {0} is posted under the owner's login", post);
    }

    [Fact]
    public void The_merge_note_is_the_fixed_sentence_and_adds_the_closing_sentence_only_when_closed()
    {
        CloseoutEngine.MergeComment(PullRequestUrl)
            .Should().Be($"The pull request for this work has merged: {PullRequestUrl}");
        CloseoutEngine.MergeComment(PullRequestUrl, closed: true)
            .Should().Be($"The pull request for this work has merged: {PullRequestUrl}\n\nThe issue was closed alongside it.");
    }

    [Fact]
    public void A_cut_body_ends_in_the_fixed_truncation_note()
    {
        string cutBody = string.Join('\n', Enumerable.Repeat("A line of ordinary prose.", 1500));

        string body = PullRequestBody.Build(Run(), Task(), null, null, new PrSummary("Title", cutBody));

        body.TrimEnd().Should().EndWith("\n\n[Truncated at 30000 characters.]");
    }

    [Fact]
    public void The_body_fallback_heads_the_session_prose_with_a_plain_summary_heading()
    {
        string body = PullRequestBody.Build(Run(), Task(), "Did the work.", sourceUrl: null);

        body.Should().Contain("## Summary").And.NotContain("Agent summary");
    }

    [Fact]
    public void A_project_that_sets_its_own_writing_conventions_still_gets_the_platform_rule()
    {
        StringBuilder prompt = new();

        WorkPromptBuilder.AppendWritingConventions(
            prompt, string.Empty, WritingConventions.Parse("Write like a pirate."), "Lead:");

        prompt.ToString().Should().Contain("Write like a pirate.").And.Contain(WritingConventions.PlatformRule);
    }

    private static RunDetails Run() => new() { Id = DomainId.New() };

    private static TaskDetails Task() => new()
    {
        Id = DomainId.New(),
        Objective = "Make the importer refuse a closed issue",
        AcceptanceCriteria = ["The importer refuses a closed issue"],
        ExternalReference = null,
    };
}

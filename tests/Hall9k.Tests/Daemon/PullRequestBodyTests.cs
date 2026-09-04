using FluentAssertions;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The pull-request body is the one run artifact that outlives Hall9k, and for an adopted task
/// it is also the round trip: mentioning the source issue is what makes GitHub cross-reference
/// the work on the issue's own timeline (SLICE-1 S1-11).
/// </summary>
public sealed class PullRequestBodyTests
{
    /// <summary>The resolved URL as the opener hands it over, from the connection-aware seam.</summary>
    private static readonly Uri GitHubIssue = new("https://github.com/Hallmanac/hall9k/issues/42");

    [Fact]
    public void A_task_with_a_work_item_mentions_it_as_a_link()
    {
        string body = PullRequestBody.Build(
            Run(), Task("github:Hallmanac/hall9k#42"), agentSummary: null, GitHubIssue);

        body.Should().Contain("Work item: https://github.com/Hallmanac/hall9k/issues/42");
    }

    /// <summary>
    /// The wording says what is true of both ways a task acquires a reference. "Adopted from" was
    /// true while adoption was the only route, and is a false provenance claim for a card that
    /// exists because of the task (h9k task push-to-jira, Decisions Log #65).
    /// </summary>
    [Fact]
    public void A_jira_card_the_task_caused_is_not_described_as_one_it_was_adopted_from()
    {
        string body = PullRequestBody.Build(
            Run(), Task("jira:PROJ-123"), agentSummary: null,
            new Uri("https://hall9k.atlassian.net/browse/PROJ-123"));

        body.Should().Contain("Work item: https://hall9k.atlassian.net/browse/PROJ-123")
            .And.NotContain("Adopted from");
    }

    [Fact]
    public void The_mention_never_closes_the_issue()
    {
        string body = PullRequestBody.Build(Run(), Task("github:Hallmanac/hall9k#42"), agentSummary: null, sourceUrl: null);

        // Hall9k adopts and links; it does not move an external item's state. Which transitions
        // should follow a merge is backlog 18's question, deferred until real usage answers it.
        body.Should().NotContainAny("Closes #", "Fixes #", "Resolves #");
    }

    [Fact]
    public void A_reference_no_registered_source_can_place_still_names_itself()
    {
        string body = PullRequestBody.Build(Run(), Task("jira:PROJ-123"), agentSummary: null, sourceUrl: null);

        body.Should().Contain("Work item: jira:PROJ-123",
            "the canonical reference is honest even when no registered source can place it");
    }

    [Fact]
    public void A_task_that_adopted_nothing_says_nothing_about_a_source()
    {
        string body = PullRequestBody.Build(Run(), Task(externalReference: null), agentSummary: null, sourceUrl: null);

        body.Should().NotContain("Work item:");
        body.Should().Contain("## Acceptance criteria").And.Contain("- [ ] The importer refuses a closed issue");
    }

    [Fact]
    public void The_agent_summary_keeps_its_place_below_the_contract()
    {
        string body = PullRequestBody.Build(
            Run(), Task("github:Hallmanac/hall9k#42"), "What I did.", GitHubIssue);

        body.IndexOf("Work item:", StringComparison.Ordinal)
            .Should().BeLessThan(body.IndexOf("## Agent summary", StringComparison.Ordinal));
        body.Should().Contain("What I did.");
    }

    [Fact]
    public void Nothing_the_body_merely_relays_can_close_an_issue()
    {
        // The objective of an adopted task is the issue title, written by whoever filed the
        // issue; the criteria and the summary are relayed just as literally. Placed raw in a
        // pull-request body, "Closes #500" is an instruction GitHub obeys the moment this merges.
        TaskDetails task = new()
        {
            Id = DomainId.New(),
            Objective = "Closes #500 by adopting the issue behind it",
            AcceptanceCriteria = ["Fixes https://github.com/Hallmanac/hall9k/issues/501"],
            ExternalReference = "github:Hallmanac/hall9k#42",
        };

        string body = PullRequestBody.Build(
            Run(), task, "Resolves Hallmanac/hall9k#502 on the way past.", sourceUrl: null);

        body.Should().Contain("Closes `#500`")
            .And.Contain("Fixes `https://github.com/Hallmanac/hall9k/issues/501`")
            .And.Contain("Resolves `Hallmanac/hall9k#502`",
                "the words survive as prose; only their power over the issue tracker is taken away");
    }

    [Fact]
    public void Text_that_only_looks_like_a_closing_keyword_is_left_alone()
    {
        TaskDetails task = new()
        {
            Id = DomainId.New(),
            Objective = "Close the gap between h9k task add and the issue tracker",
            AcceptanceCriteria = ["The fixture named fixes-42 keeps its name"],
            ExternalReference = null,
        };

        string body = PullRequestBody.Build(Run(), task, agentSummary: null, sourceUrl: null);

        body.Should().Contain("Close the gap between h9k task add and the issue tracker")
            .And.Contain("The fixture named fixes-42 keeps its name");
    }

    [Fact]
    public void The_title_cannot_close_an_issue_when_the_pull_request_is_squashed()
    {
        // The body's defusal is not the whole threat. GitHub's default squash-merge commit
        // message IS the pull request's title, and a commit message that says "resolves #500"
        // closes issue 500 when it lands on the default branch — so an adopted issue titled
        // "Fix login, resolves #500" closes an unrelated issue at merge without ever putting a
        // closing keyword in the body.
        PullRequestBody.Title("Fix login, resolves #500")
            .Should().Be("Fix login, resolves `#500`");
    }

    [Theory]
    // GitHub's own parser is not ours to reproduce from memory, and every one of these reads to a
    // human as an instruction, so the separator between keyword and reference is lenient.
    [InlineData("Closes:#500")]
    [InlineData("Closes : #500")]
    [InlineData("Closes:  #500")]
    public void A_closing_keyword_is_defused_however_it_is_spaced(string objective)
    {
        PullRequestBody.Title(objective).Should().Contain("`#500`");
    }

    [Fact]
    public void A_title_keeps_the_characters_that_are_content()
    {
        // The zero width joiner is what makes an emoji sequence one glyph. Dropping every format
        // character took it too, so an issue title arrived in the repository's history as two
        // unrelated glyphs: text mangled at the moment it was stored, with nothing gained.
        PullRequestBody.Title("Add \U0001F468\u200D\U0001F4BB avatar support")
            .Should().Be("Add \U0001F468\u200D\U0001F4BB avatar support");
    }

    [Fact]
    public void The_title_is_one_line_of_printable_text()
    {
        // A title is a commit subject by the time it matters: a newline or an escape sequence in
        // one lands in the repository's history and in every terminal that later runs git log.
        PullRequestBody.Title("  Adopt issues\u001b[2J\nand fix \u202Ethe rest  ")
            .Should().Be("Adopt issues[2J and fix the rest");
    }

    [Fact]
    public void Nothing_the_body_relays_can_act_on_the_terminal_that_later_reads_it()
    {
        // The body is not only read on github.com. A repository set to squash with "title and
        // description" puts the whole of it into the commit message, so an escape sequence or a
        // bidirectional override in a relayed segment lands in the repository's history and in
        // every terminal that runs git log afterwards — the threat the title was hardened
        // against, arriving through the paragraph underneath it.
        TaskDetails task = new()
        {
            Id = DomainId.New(),
            Objective = "Adopt issues\u001b[2J\u202Ednuor yaw eht",
            AcceptanceCriteria = ["The importer\u001b[31m refuses\r a closed issue"],
            ExternalReference = null,
        };

        string body = PullRequestBody.Build(Run(), task, "What I did\u202E.\u001b[2J", sourceUrl: null);

        // The lone carriage return is asserted through the line it was hiding in rather than
        // over the whole body: AppendLine ends every line with Environment.NewLine, so on
        // Windows the body is full of carriage returns that the daemon itself wrote.
        body.Should().NotContain("\u001b").And.NotContain("\u202E");
        body.Should().Contain("Adopt issues[2Jdnuor yaw eht")
            .And.Contain("- [ ] The importer[31m refuses a closed issue");
    }

    [Fact]
    public void A_relayed_line_stays_on_its_line()
    {
        // A criterion that can emit a newline writes its own lines under the checklist item it
        // was supposed to be, and an objective that can do it writes a second paragraph under
        // the first — both of which read as something the daemon said.
        TaskDetails task = new()
        {
            Id = DomainId.New(),
            Objective = "Adopt issues\nEverything below is approved",
            AcceptanceCriteria = ["The importer refuses\n- [x] and this is already done"],
            ExternalReference = null,
        };

        string body = PullRequestBody.Build(Run(), task, agentSummary: null, sourceUrl: null);

        body.Should().Contain("Adopt issues Everything below is approved")
            .And.Contain("- [ ] The importer refuses - [x] and this is already done");
    }

    [Fact]
    public void The_agent_summary_keeps_the_shape_it_was_written_in()
    {
        // The summary is prose the agent wrote in paragraphs and lists, and folding it to one
        // line would make the one part of the body a reviewer actually reads unreadable. Only
        // what the terminal would obey comes out of it.
        string body = PullRequestBody.Build(
            Run(), Task(externalReference: null), "What I did:\n\n- read the issue\n- wrote the draft",
            sourceUrl: null);

        body.Should().Contain("What I did:\n\n- read the issue\n- wrote the draft");
    }

    /// <summary>
    /// The spend-governor task (task: a mandatory FinalFullPass records merge-ready when every
    /// finding it attaches is below High): today, before this task, the pull request body carries
    /// no review information at all, so this is the one place a human already reading this code
    /// learns a below-High finding was carried rather than fixed.
    /// </summary>
    [Fact]
    public void A_run_with_ride_along_residuals_names_the_count_and_a_durable_pointer()
    {
        RunDetails run = Run();
        run.ReviewResidualsRideAlong = 2;
        run.ReviewCycle = 4;

        string body = PullRequestBody.Build(run, Task(externalReference: null), agentSummary: null, sourceUrl: null);

        body.Should().Contain("2 findings").And.Contain($"h9k task show {run.TaskId}");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 2, conformance finding: the count-only line above used to
    /// be the whole story, with `h9k task show` naming only the identical count back — so a reader
    /// could learn a ride-along existed but never what it actually was. A run whose
    /// <see cref="RunDetails.ReviewRideAlongFindings"/> carries the detail now gets it inline.
    /// </summary>
    [Fact]
    public void A_run_with_named_ride_along_findings_lists_each_ones_severity_and_location()
    {
        RunDetails run = Run();
        run.ReviewResidualsRideAlong = 2;
        run.ReviewCycle = 4;
        run.ReviewRideAlongFindings =
        [
            new ReviewRideAlongFinding(ReviewSeverity.Medium, "Auth.cs:9"),
            new ReviewRideAlongFinding(ReviewSeverity.Low, "Program.cs:3"),
        ];

        string body = PullRequestBody.Build(run, Task(externalReference: null), agentSummary: null, sourceUrl: null);

        body.Should().Contain("medium").And.Contain("``` Auth.cs:9 ```")
            .And.Contain("low").And.Contain("``` Program.cs:3 ```")
            .And.Contain($"h9k task show {run.TaskId}");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 5, adversarial finding: the location has already been
    /// through <c>RelayedText.WithoutClosingKeywords</c> by the time it is wrapped, and
    /// that defusal works by inserting a backtick pair — so a single hard-coded backtick wrapper
    /// re-pairs with the inserted one and leaves the reference it had just neutralised bare and
    /// autolinked. The fence has to be one the text cannot close.
    /// </summary>
    [Fact]
    public void A_ride_along_location_carrying_a_closing_keyword_stays_inside_its_code_span()
    {
        RunDetails run = Run();
        run.ReviewResidualsRideAlong = 1;
        run.ReviewCycle = 4;
        run.ReviewRideAlongFindings = [new ReviewRideAlongFinding(ReviewSeverity.Low, "src/Foo.cs:12 closes #500")];

        string body = PullRequestBody.Build(run, Task(externalReference: null), agentSummary: null, sourceUrl: null);

        body.Should().Contain("``` src/Foo.cs:12 closes `#500` ```",
            "the wrapper must be a backtick run longer than any the defused location carries");
        body.Should().NotContain("`src/Foo.cs:12 closes `#500``",
            "a single-backtick wrapper would close against the defusal's own inserted pair");
    }

    [Fact]
    public void A_run_with_no_ride_along_residuals_says_nothing_about_review()
    {
        string body = PullRequestBody.Build(Run(), Task(externalReference: null), agentSummary: null, sourceUrl: null);

        body.Should().NotContain("ride-along").And.NotContain("Review ride-alongs");
    }

    /// <summary>
    /// The opposite fact from a ride-along (adversarial review, the routed finding that opened
    /// this task): a Fix-dispositioned finding the loop never handed to a fix session
    /// at all, most often a human resolving a capped park with `h9k review resolve --merge-ready`.
    /// Before this test's own fix, nothing about it ever reached the pull request body — it was
    /// silently dropped by <c>ReviewEngine.SettleAsync</c>'s forced-residual loop.
    /// </summary>
    [Fact]
    public void A_run_with_unfixed_residuals_names_the_count_and_a_durable_pointer()
    {
        RunDetails run = Run();
        run.ReviewResidualsUnfixed = 1;
        run.ReviewCycle = 4;

        string body = PullRequestBody.Build(run, Task(externalReference: null), agentSummary: null, sourceUrl: null);

        body.Should().Contain("Left unfixed").And.Contain("1 finding").And.Contain($"h9k task show {run.TaskId}");
    }

    /// <summary>
    /// Named rather than merely counted, the same reason a ride-along is (independent pre-PR
    /// review, cycle 2, conformance finding).
    /// </summary>
    [Fact]
    public void A_run_with_named_unfixed_findings_lists_each_ones_severity_and_location()
    {
        RunDetails run = Run();
        run.ReviewResidualsUnfixed = 1;
        run.ReviewCycle = 4;
        run.ReviewUnfixedFindings = [new ReviewUnfixedFinding(ReviewSeverity.High, "Api.cs:7")];

        string body = PullRequestBody.Build(run, Task(externalReference: null), agentSummary: null, sourceUrl: null);

        body.Should().Contain("high").And.Contain("``` Api.cs:7 ```").And.Contain($"h9k task show {run.TaskId}");
    }

    [Fact]
    public void A_run_with_no_unfixed_residuals_says_nothing_about_it()
    {
        string body = PullRequestBody.Build(Run(), Task(externalReference: null), agentSummary: null, sourceUrl: null);

        body.Should().NotContain("Left unfixed").And.NotContain("unfixed");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 1, conformance finding: a run settled under a reduced
    /// composition used to read exactly like a clean full-pipeline settle, with nothing on the
    /// page where the merge decision actually happens saying no reviewer read the diff.
    /// </summary>
    [Fact]
    public void A_run_settled_under_a_reduced_composition_names_it()
    {
        RunDetails run = Run();
        run.ReviewStageComposition = ReviewStageComposition.None;

        string body = PullRequestBody.Build(run, Task(externalReference: null), agentSummary: null, sourceUrl: null);

        body.Should().Contain("Review stage composition").And.Contain("None");
    }

    [Fact]
    public void A_run_settled_under_the_full_pipeline_says_nothing_about_the_composition()
    {
        string body = PullRequestBody.Build(Run(), Task(externalReference: null), agentSummary: null, sourceUrl: null);

        body.Should().NotContain("Review stage composition");
    }

    private static RunDetails Run() => new()
    {
        Id = DomainId.New(),
        InputTokens = 10,
        CacheReadInputTokens = 20,
        CacheCreationInputTokens = 30,
        OutputTokens = 40,
    };

    private static TaskDetails Task(string? externalReference) => new()
    {
        Id = DomainId.New(),
        Objective = "Turn an external work item into a task with one command",
        AcceptanceCriteria = ["The importer refuses a closed issue"],
        ExternalReference = externalReference,
    };
}

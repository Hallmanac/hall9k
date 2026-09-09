using System.Text;
using FluentAssertions;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using PrSummary = Hall9k.Connectors.Prompts.PrSummaryParser.PrSummary;
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
        PullRequestBody.Title(Objective("Fix login, resolves #500"), prSummary: null)
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
        PullRequestBody.Title(Objective(objective), prSummary: null).Should().Contain("`#500`");
    }

    [Fact]
    public void A_title_keeps_the_characters_that_are_content()
    {
        // The zero width joiner is what makes an emoji sequence one glyph. Dropping every format
        // character took it too, so an issue title arrived in the repository's history as two
        // unrelated glyphs: text mangled at the moment it was stored, with nothing gained.
        PullRequestBody.Title(Objective("Add \U0001F468\u200D\U0001F4BB avatar support"), prSummary: null)
            .Should().Be("Add \U0001F468\u200D\U0001F4BB avatar support");
    }

    [Fact]
    public void The_title_is_one_line_of_printable_text()
    {
        // A title is a commit subject by the time it matters: a newline or an escape sequence in
        // one lands in the repository's history and in every terminal that later runs git log.
        PullRequestBody.Title(Objective("  Adopt issues\u001b[2J\nand fix \u202Ethe rest  "), prSummary: null)
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

    /// <summary>
    /// Origin incident, arx-platform PR #2042 (2026-09-09): a readiness-contract objective is an
    /// outcome sentence by rule (PLAN.md §4), so as a title it was 334 characters of contract with
    /// no key on the front. Both halves of that are fixed here.
    /// </summary>
    [Fact]
    public void A_jira_task_with_no_artifact_gets_a_key_prefixed_title_cut_at_a_word_boundary()
    {
        TaskDetails task = new()
        {
            Id = DomainId.New(),
            Objective = "Every arx-platform host resolves Azure Key Vault references from App Configuration "
                + "at startup through its managed identity, with the vault access delivered as scripts",
            AcceptanceCriteria = [],
            ExternalReference = "jira:ARX-4861",
        };

        string title = PullRequestBody.Title(task, prSummary: null);

        title.Should().StartWith("ARX-4861: Every arx-platform host resolves").And.EndWith("…");
        title.Length.Should().BeLessThanOrEqualTo(72);

        string kept = title[..^1];
        string full = $"ARX-4861: {task.Objective}";
        full.Should().StartWith(kept);
        full[kept.Length].Should().Be(' ', "the cut lands on a word boundary, not mid-word");
    }

    [Fact]
    public void A_github_task_with_no_artifact_is_never_key_prefixed()
    {
        string title = PullRequestBody.Title(Task("github:Hallmanac/hall9k#42"), prSummary: null);

        // The work-item line carries the issue, and a bare "42: " on a squash subject says nothing.
        title.Should().Be("Turn an external work item into a task with one command");
    }

    [Fact]
    public void The_artifact_title_wins_over_the_objective()
    {
        string title = PullRequestBody.Title(
            Task("jira:ARX-4861"), Summary("Resolve Key Vault references in every host", "Body."));

        title.Should().Be("ARX-4861: Resolve Key Vault references in every host");
    }

    [Fact]
    public void A_key_the_session_already_wrote_is_not_written_twice()
    {
        string title = PullRequestBody.Title(
            Task("jira:ARX-4861"), Summary("ARX-4861: Resolve Key Vault references", "Body."));

        title.Should().Be("ARX-4861: Resolve Key Vault references");
    }

    /// <summary>
    /// Keys share prefixes with each other, so "already written" is a whole-token question. A
    /// session that opened its title on a DIFFERENT card whose key merely extends this task's is
    /// exactly the shape the forced prefix exists to correct, and a bare <c>StartsWith</c> waved
    /// that one shape through (independent pre-PR review, cycle 1, both lenses).
    /// </summary>
    [Fact]
    public void A_longer_key_that_merely_starts_with_this_tasks_is_not_this_tasks_key()
    {
        string title = PullRequestBody.Title(
            Task("jira:ARX-486"), Summary("ARX-4861 compatibility for the shared provider", "Body."));

        title.Should().Be("ARX-486: ARX-4861 compatibility for the shared provider");
    }

    /// <summary>
    /// A title in a script that writes no spaces carries no word boundary to prefer, so the
    /// fallback cut is the ordinary path for one rather than the exotic one — and a raw char
    /// slice can keep half a surrogate pair, which reaches <c>gh pr create --title</c>, and on a
    /// squash merge the default branch's own commit subject, as U+FFFD (independent pre-PR
    /// review, cycle 1, adversarial lens).
    /// </summary>
    [Fact]
    public void A_title_with_no_word_boundary_is_still_never_cut_through_a_character()
    {
        // The astral character straddles the raw cut: 70 chars before it, and it is two chars.
        string spaceless = new string('あ', 70) + "\U0001D4B3" + new string('あ', 30);

        string title = PullRequestBody.Title(Objective(spaceless), prSummary: null);

        title.Length.Should().BeLessThanOrEqualTo(72);
        title.Should().EndWith("…");
        Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(title)).Should().Be(title,
            "half a surrogate pair survives in memory and only becomes U+FFFD once gh is handed the bytes");
    }

    /// <summary>
    /// The same cut, on the body's own bound: a runaway paste with no line feed in it is cut at
    /// <c>MaxRelayedBodyLength</c>, and <see cref="Hall9k.Connectors.Text.RelayedText.Printable"/>
    /// cannot save a half character the cut itself makes — a lone surrogate is neither a control
    /// character nor a layout override, so it passes the defusal untouched.
    /// </summary>
    [Fact]
    public void An_authored_body_cut_at_the_bound_is_never_cut_through_a_character()
    {
        string runaway = new string('あ', 29_999) + "\U0001D4B3" + new string('あ', 100);

        string body = PullRequestBody.Build(
            Run(), Task(externalReference: null), agentSummary: null, sourceUrl: null, Summary("A title", runaway));

        body.Should().Contain("[Truncated at 30000 characters.");
        Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(body)).Should().Be(body);
    }

    /// <summary>
    /// The bound is Hall9k's own, not GitHub's: 256 is the documented <em>issue</em> title limit
    /// and demonstrably does not bind a pull request's, since PR #2042 opened with 334 characters.
    /// What matters here is that some bound applies and that it cuts rather than fails.
    /// </summary>
    [Fact]
    public void An_authored_title_longer_than_the_platforms_bound_is_cut_rather_than_rejected()
    {
        string runaway = string.Join(' ', Enumerable.Repeat("word", 400));

        string title = PullRequestBody.Title(Task(externalReference: null), Summary(runaway, "Body."));

        title.Length.Should().BeLessThanOrEqualTo(1024).And.BeGreaterThan(334,
            "a 334-character title is one GitHub has actually accepted, so the bound sits above it");
        title.Should().EndWith("…");
    }

    [Fact]
    public void An_authored_title_is_defused_exactly_as_an_objective_is()
    {
        string title = PullRequestBody.Title(
            Task(externalReference: null), Summary("Fix login,\nresolves #500\u001b[2J", "Body."));

        title.Should().Be("Fix login, resolves `#500`[2J");
    }

    [Fact]
    public void The_authored_body_sits_between_the_work_item_line_and_the_platforms_own_parts()
    {
        RunDetails run = Run();
        run.ReviewResidualsRideAlong = 1;

        string body = PullRequestBody.Build(
            run, Task("github:Hallmanac/hall9k#42"), "run narration", GitHubIssue,
            Summary("A title", "Every host now resolves references through the shared provider."));

        body.IndexOf("Work item:", StringComparison.Ordinal).Should().Be(0);
        body.IndexOf("Every host now resolves", StringComparison.Ordinal)
            .Should().BeLessThan(body.IndexOf("<details><summary>Acceptance criteria</summary>", StringComparison.Ordinal));
        body.IndexOf("<details><summary>Acceptance criteria</summary>", StringComparison.Ordinal)
            .Should().BeLessThan(body.IndexOf("Review ride-alongs", StringComparison.Ordinal));
        body.IndexOf("Review ride-alongs", StringComparison.Ordinal)
            .Should().BeLessThan(body.IndexOf("Hall9k run", StringComparison.Ordinal));
        body.Should().Contain("- [ ] The importer refuses a closed issue").And.Contain("</details>");
    }

    [Fact]
    public void The_agent_summary_narration_is_gone_once_a_session_composed_a_body()
    {
        string body = PullRequestBody.Build(
            Run(), Task("github:Hallmanac/hall9k#42"), "Full suite green (4739 passed).", GitHubIssue,
            Summary("A title", "What a reviewer needs to know."));

        body.Should().NotContain("## Agent summary").And.NotContain("Full suite green");
    }

    [Fact]
    public void A_work_item_line_the_session_wrote_itself_is_dropped_rather_than_doubled()
    {
        string body = PullRequestBody.Build(
            Run(), Task("github:Hallmanac/hall9k#42"), agentSummary: null, GitHubIssue,
            Summary("A title", "Work item: https://example.invalid/wrong-one\n\nThe real prose."));

        body.Should().NotContain("wrong-one").And.Contain("The real prose.");
        body.Split("Work item:").Length.Should().Be(2, "exactly one work-item line survives");
    }

    /// <summary>
    /// A body that was nothing but the work-item line composed nothing, and the blankness check
    /// has to run after that line is stripped rather than before it, or the pull request opens on
    /// an empty prose section with the run narration suppressed underneath it.
    /// </summary>
    [Fact]
    public void A_body_that_was_only_a_work_item_line_counts_as_no_body_at_all()
    {
        string body = PullRequestBody.Build(
            Run(), Task("github:Hallmanac/hall9k#42"), "What I did.", GitHubIssue,
            Summary("A title", "Work item: https://example.invalid/wrong-one\n"));

        body.Should().NotContain("wrong-one")
            .And.Contain("## Acceptance criteria")
            .And.Contain("## Agent summary");
    }

    /// <summary>
    /// A session composed a title and no body: the title is still the best one anybody has, and
    /// the body falls back to the skeleton rather than opening on nothing.
    /// </summary>
    [Fact]
    public void A_blank_authored_body_falls_back_while_the_authored_title_still_stands()
    {
        PrSummary summary = Summary("Resolve Key Vault references", string.Empty);

        PullRequestBody.Title(Task("jira:ARX-4861"), summary)
            .Should().Be("ARX-4861: Resolve Key Vault references");
        PullRequestBody.Build(Run(), Task("jira:ARX-4861"), "What I did.", sourceUrl: null, summary)
            .Should().Contain("## Acceptance criteria").And.Contain("## Agent summary");
    }

    /// <summary>
    /// The authored body is relayed text like any other, so it goes through the same two defusals
    /// (<c>RelayedText.WithoutClosingKeywords</c> over <c>RelayedText.Printable</c>) rather than a
    /// second, weaker rule of its own.
    /// <para>
    /// "Fixes ARX-1" survives as prose, and that is the correct answer rather than a gap: GitHub's
    /// closing keywords act only on GitHub issue references, and Hall9k never transitions a Jira
    /// card at all (Decisions Log #65 gives Jira a comment at merge and never a transition), so
    /// this sentence moves nothing. Wrapping it would garble the ordinary sentence a pull request
    /// body is supposed to be able to write about the card it belongs to.
    /// </para>
    /// </summary>
    [Fact]
    public void An_authored_body_is_defused_the_same_way_every_other_relayed_segment_is()
    {
        string body = PullRequestBody.Build(
            Run(), Task("jira:ARX-1"), agentSummary: null, sourceUrl: null,
            Summary("A title", "Fixes ARX-1 on the way past.\u001b[2J\u202E\n\nCloses #500 too."));

        body.Should().NotContain("\u001b").And.NotContain("\u202E");
        body.Should().Contain("Closes `#500`");
        body.Should().Contain("Fixes ARX-1 on the way past.",
            "a Jira key is not a closing instruction on either side, so it stays as written");
    }

    [Fact]
    public void An_authored_body_over_the_bound_says_it_was_cut_and_where_the_whole_one_is()
    {
        RunDetails run = Run();
        run.RunDirectory = "/home/someone/.hall9k/projects/p/tasks/abc/runs/1";
        string runaway = string.Join('\n', Enumerable.Repeat("A line of the agent's own prose.", 1200));

        string body = PullRequestBody.Build(
            run, Task(externalReference: null), agentSummary: null, sourceUrl: null, Summary("A title", runaway));

        body.Should().Contain("[Truncated at 30000 characters.").And.Contain("pr-summary.md")
            .And.Contain($"h9k task show {run.TaskId}");
        body.Should().NotContain(run.RunDirectory,
            "the note names the file, never a daemon-machine-local absolute path carrying the operator's username");
    }

    /// <summary>
    /// The house writing convention every authored PR description follows, applied to the parts
    /// the platform itself writes around the prose: no em dashes (U+2014), commas or semicolons or
    /// parentheses instead.
    /// </summary>
    [Fact]
    public void Nothing_the_platform_writes_around_the_prose_uses_an_em_dash()
    {
        string body = PullRequestBody.Build(
            Run(), Task("jira:PROJ-123"), agentSummary: null,
            new Uri("https://hall9k.atlassian.net/browse/PROJ-123"),
            Summary("A title", "Prose with no em dash in it, deliberately."));

        body.Should().NotContain("—");
    }

    /// <summary>
    /// The fallback is not merely "close to" today's body; a run with no artifact must open a pull
    /// request byte for byte the way it always did, so nothing about this change can regress a
    /// session that never composed one.
    /// </summary>
    [Fact]
    public void Without_an_artifact_the_body_is_byte_for_byte_the_one_the_daemon_has_always_written()
    {
        RunDetails run = Run();

        string body = PullRequestBody.Build(
            run, Task("github:Hallmanac/hall9k#42"), "What I did.", GitHubIssue, prSummary: null);

        body.Should().Be(string.Join(Environment.NewLine,
            "Turn an external work item into a task with one command",
            string.Empty,
            "## Acceptance criteria",
            "- [ ] The importer refuses a closed issue",
            string.Empty,
            "Work item: https://github.com/Hallmanac/hall9k/issues/42",
            string.Empty,
            "## Agent summary",
            "What I did.",
            string.Empty,
            "---",
            $"Hall9k run `{run.Id}` · 100 tokens",
            string.Empty));
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

    /// <summary>A task carrying nothing but the objective under test, for the title rules.</summary>
    private static TaskDetails Objective(string objective) => new()
    {
        Id = DomainId.New(),
        Objective = objective,
        AcceptanceCriteria = [],
        ExternalReference = null,
    };

    private static PrSummary Summary(string? title, string body) => new(title, body);
}

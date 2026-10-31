using FluentAssertions;
using Hall9k.Connectors.Text;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;
using PrSummary = Hall9k.Connectors.Prompts.PrSummaryParser.PrSummary;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// What a pull request says on a Linear-tracked project: the identifier at the front of the title,
/// the work-item line that mentions the issue without instructing Linear, and the guard that keeps
/// every other piece of composed text from reaching an issue the task does not own. Nothing here calls
/// Linear, opens a pull request, or touches GitHub: the team keys are a set the test hands over.
/// </summary>
public sealed class PullRequestBodyLinearTests
{
    private static readonly IReadOnlySet<string> Teams = new HashSet<string> { "ENG", "DES", "OPS" };
    private static readonly Uri OwnIssue = new("https://linear.app/hall9k/issue/ENG-123");

    private static readonly LinearTextScope Scope = new("ENG-123", Teams);
    private static readonly LinearTextScope ShapeOnly = new("ENG-123", TeamKeys: null);

    [Fact]
    public void The_title_puts_the_Linear_identifier_at_the_front_once()
    {
        PullRequestBody.Title(LinearTask(), Summary("Add rate limiting", "body"))
            .Should().Be("ENG-123: Add rate limiting");
    }

    [Theory]
    [InlineData("ENG-123: Add rate limiting")]
    [InlineData("ENG-123 Add rate limiting")]
    [InlineData("eng-123: Add rate limiting")]
    [InlineData("ENG-123")]
    public void An_authored_title_that_already_starts_with_the_identifier_is_kept_as_written(string authored)
    {
        PullRequestBody.Title(LinearTask(), Summary(authored, "body")).Should().Be(authored);
        PullRequestBody.Title(LinearTask(), Summary(authored, "body"), Scope).Should().Be(authored);
    }

    [Fact]
    public void The_identifier_check_is_a_whole_token_one_so_a_longer_identifier_does_not_pass_for_the_tasks_own()
    {
        PullRequestBody.Title(LinearTask(), Summary("ENG-1234 compatibility", "body"))
            .Should().Be("ENG-123: ENG-1234 compatibility");
    }

    [Fact]
    public void A_GitHub_issue_number_still_gets_no_prefix()
    {
        PullRequestBody.Title(Task("github:Hallmanac/hall9k#42"), Summary("Add rate limiting", "body"))
            .Should().Be("Add rate limiting");
    }

    [Fact]
    public void A_Jira_key_is_placed_as_it_always_was()
    {
        PullRequestBody.Title(Task("jira:PROJ-7"), Summary("Add rate limiting", "body"))
            .Should().Be("PROJ-7: Add rate limiting");
    }

    [Theory]
    [InlineData("DES-5: Fix login", "ENG-123: Fix login")]
    [InlineData("[DES-5] Fix login", "ENG-123: Fix login")]
    [InlineData("ENG-1234 compatibility", "ENG-123: compatibility")]
    [InlineData("Fix login (OPS-2)", "ENG-123: Fix login")]
    [InlineData("Fix SHA-256 hashing", "ENG-123: Fix SHA-256 hashing")]
    [InlineData("ENG-123: Fix DES-5", "ENG-123: Fix")]
    public void The_title_holds_no_Linear_identifier_but_the_tasks_own(string authored, string expected)
    {
        PullRequestBody.Title(LinearTask(), Summary(authored, "body"), Scope).Should().Be(expected);
    }

    [Fact]
    public void An_authored_title_left_with_no_words_falls_back_to_the_objective()
    {
        TaskDetails task = LinearTask(objective: "Turn an external work item into a task");

        PullRequestBody.Title(task, Summary("[DES-5]", "body"), Scope)
            .Should().Be("ENG-123: Turn an external work item into a task");
    }

    [Fact]
    public void An_objective_left_with_no_words_leaves_the_identifier_alone_or_says_Untitled_change()
    {
        PullRequestBody.Title(LinearTask(objective: "DES-5"), prSummary: null, Scope).Should().Be("ENG-123");

        TaskDetails unlinked = Task(externalReference: null, objective: "[OPS-1]");
        PullRequestBody.Title(unlinked, prSummary: null, new LinearTextScope(null, Teams)).Should().Be("Untitled change");
    }

    [Fact]
    public void GitHubs_closing_keyword_rule_sees_the_title_after_the_drop_so_no_live_Fixes_number_reaches_it()
    {
        string title = PullRequestBody.Title(LinearTask(), Summary("Fixes DES-5 #12", "body"), Scope);

        title.Should().Be("ENG-123: Fixes `#12`");
    }

    [Fact]
    public void A_title_naming_another_issue_after_a_linking_word_gets_the_neutral_word_even_with_no_team_keys()
    {
        PullRequestBody.Title(LinearTask(), Summary("Closes DES-5 properly", "body"), ShapeOnly)
            .Should().Be("ENG-123: Closes ticket DES-5 properly");
    }

    [Fact]
    public void With_no_team_keys_the_title_rule_is_skipped_and_the_title_keeps_its_other_identifier()
    {
        PullRequestBody.Title(LinearTask(), Summary("DES-5: Fix login", "body"), ShapeOnly)
            .Should().Be("ENG-123: DES-5: Fix login");
    }

    [Fact]
    public void A_title_is_cut_after_the_drop_and_the_key_so_the_bound_still_holds()
    {
        string words = string.Join(' ', Enumerable.Repeat("compatibility", 100));

        string title = PullRequestBody.Title(LinearTask(), Summary($"DES-5 {words}", "body"), Scope);

        title.Should().StartWith("ENG-123: compatibility").And.NotContain("DES-5").And.EndWith("…");
        title.Length.Should().BeLessThanOrEqualTo(1024);
    }

    [Fact]
    public void The_work_item_line_links_the_issue_by_its_identifier_with_no_magic_word_before_it()
    {
        string body = PullRequestBody.Build(Run(), LinearTask(), agentSummary: null, OwnIssue, Summary("Title", "Rate limiting."));

        body.Should().StartWith("Work item: [ENG-123](https://linear.app/hall9k/issue/ENG-123)");
        RelayedText.WithoutLinearMagicWords(body, "ENG-123", Teams).Should().Be(body);
    }

    [Fact]
    public void A_Linear_link_with_no_resolvable_url_falls_back_to_the_unlinked_form()
    {
        string body = PullRequestBody.Build(Run(), LinearTask(), agentSummary: null, sourceUrl: null, Summary("Title", "Rate limiting."));

        body.Should().StartWith("Work item: linear:ENG-123");
    }

    /// <summary>
    /// Every piece of text the composer relays, each carrying a phrase that would link another issue,
    /// pinned against the title and the body it composes: the objective, each criterion, the authored
    /// title, the authored body (with the phrase inside a code span and inside a fenced block), and
    /// the agent's closing summary.
    /// </summary>
    [Fact]
    public void Every_composed_piece_is_guarded_on_the_skeleton_body()
    {
        TaskDetails task = LinearTask(
            objective: "Port the login page. Fixes DES-5 along the way.",
            criteria: ["Closes OPS-1 once done", "The old note `Resolves DES-6` is gone", "Part of DES-7"]);

        string body = PullRequestBody.Build(
            Run(), task, agentSummary: "Closes OPS-2 and Fixes: DES-8.\n```\nResolves DES-9\n```", OwnIssue,
            prSummary: null, Scope);

        body.Should().Contain("Fixes ticket DES-5 along the way")
            .And.Contain("- [ ] Closes ticket OPS-1 once done")
            .And.Contain("- [ ] The old note `Resolves ticket DES-6` is gone")
            .And.Contain("- [ ] Part of ticket DES-7")
            .And.Contain("Closes ticket OPS-2 and Fixes: ticket DES-8.")
            .And.Contain("```\nResolves ticket DES-9\n```");
        RelayedText.WithoutLinearMagicWords(body, "ENG-123", Teams).Should().Be(body, "the guard is idempotent");
    }

    [Fact]
    public void Every_composed_piece_is_guarded_on_the_authored_body_including_code_spans_and_fences()
    {
        PrSummary summary = Summary(
            "Fixes DES-5: port the login page",
            "Ports the page. Closes DES-1, and refs OPS-2.\n\nA quoted past note: `Fixes DES-6`.\n\n"
            + "```text\nResolves [DES-7](https://linear.app/hall9k/issue/DES-7/slug)\n```\n\n"
            + "Fixes ENG-123 and skip ENG-123.");

        string body = PullRequestBody.Build(Run(), LinearTask(), agentSummary: "never used", OwnIssue, summary, Scope);
        string title = PullRequestBody.Title(LinearTask(), summary, Scope);

        body.Should().Contain("Closes ticket DES-1, and refs ticket OPS-2.")
            .And.Contain("`Fixes ticket DES-6`")
            .And.Contain("Resolves ticket [DES-7](https://linear.app/hall9k/issue/DES-7/slug)")
            .And.Contain("Fixes ENG-123 and skip ticket ENG-123.");
        title.Should().Be("ENG-123: Fixes port the login page", "DES-5 and the colon that framed it are dropped");
        RelayedText.WithoutLinearMagicWords(body, "ENG-123", Teams).Should().Be(body);
    }

    [Fact]
    public void The_bound_on_an_authored_body_is_measured_before_the_guard_inserts_anything()
    {
        string line = "Fixes DES-5.\n";
        string authored = string.Concat(Enumerable.Repeat(line, 30_000 / line.Length + 100));

        string body = PullRequestBody.Build(Run(), LinearTask(), agentSummary: null, OwnIssue, Summary("T", authored), Scope);

        body.Should().Contain("[Truncated at 30000 characters.]");
        body.Should().NotContain("Fixes DES-5.", "every kept line was guarded after the cut");
    }

    [Fact]
    public void A_pull_request_with_no_scope_is_composed_exactly_as_it_was_for_every_other_tracker()
    {
        PrSummary summary = Summary("Fixes PROJ-1: harden hashing", "Fix SHA-256 hashing. Fixes PROJ-1 and Closes ENG-9.");

        foreach (string reference in new[] { "jira:PROJ-1", "github:Hallmanac/hall9k#42" })
        {
            TaskDetails task = Task(reference, objective: "Fixes PROJ-1 and Fix SHA-256 hashing");
            string body = PullRequestBody.Build(Run(), task, "Fixes ENG-9", sourceUrl: null, summary);

            body.Should().Contain("Fix SHA-256 hashing. Fixes PROJ-1 and Closes ENG-9.").And.NotContain("ticket");
            PullRequestBody.Title(task, summary).Should().NotContain("ticket");
            PullRequestBody.Build(Run(), task, "Fixes ENG-9", sourceUrl: null, prSummary: null)
                .Should().Contain("Fixes PROJ-1 and Fix SHA-256 hashing").And.Contain("Fixes ENG-9").And.NotContain("ticket");
        }
    }

    private static TaskDetails LinearTask(string objective = "Add rate limiting", string[]? criteria = null) =>
        Task("linear:ENG-123", objective, criteria);

    private static TaskDetails Task(string? externalReference, string objective = "Add rate limiting", string[]? criteria = null) => new()
    {
        Id = DomainId.New(),
        Objective = objective,
        AcceptanceCriteria = [.. criteria ?? ["Requests over the limit are refused"]],
        ExternalReference = externalReference,
    };

    private static RunDetails Run() => new() { Id = DomainId.New() };

    private static PrSummary Summary(string? title, string body) => new(title, body);
}

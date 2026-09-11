using System.Text.Json;
using FluentAssertions;
using Hall9k.Connectors.Prompts;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The review lap's opening briefing, its push guard, and the <c>--finding</c> form a reviewer
/// types (Decisions Log #149) — all three are pure, so all three are checked here rather than
/// through a store.
/// <para>
/// The briefing's own design ruling is that it states what is there and volunteers nothing else.
/// That is a property of composed text, which makes it exactly the kind of thing that decays
/// silently: a well-meant "here are three scenarios to try" added later would read as helpful
/// and would quietly replace the reviewer's judgment with the platform's, which is what #149
/// rules against.
/// </para>
/// </summary>
// ReviewLapPromptBuilder.Build reads its prose through PromptTemplates, which falls back to
// TemplateLibraryPaths.CanonicalDirectory (a HALL9K_HOME-derived path) whenever this checkout's own
// .claude/templates does not carry a file it asks for — so this class shares the serialized
// Hall9kHome collection with every other HALL9K_HOME-touching class, and points HALL9K_HOME at an
// empty temp home itself so a fixture never accidentally reads whatever a real install already
// published to this machine (independent pre-PR review, cycle 1).
[Collection("Hall9kHome")]
public sealed class ReviewLapPromptBuilderTests : IDisposable
{
    private readonly string _platformHome = Path.Combine(Path.GetTempPath(), $"h9k-review-lap-prompt-builder-{Guid.NewGuid():N}");
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");

    public ReviewLapPromptBuilderTests()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", _platformHome);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", _previousHome);
        if (Directory.Exists(_platformHome))
        {
            Directory.Delete(_platformHome, recursive: true);
        }
    }

    [Fact]
    public void The_briefing_names_the_authors_objective_and_criteria_when_this_node_can_read_them()
    {
        string prompt = ReviewLapPromptBuilder.Build(Briefing() with
        {
            StatedObjective = "Teach the closeout monitor to read a rebase conflict",
            AcceptanceCriteria = ["the conflict is observed, never inferred", "the dispute parks for the human"],
            AuthorTaskShortId = "28b19893",
        });

        prompt.Should().Contain("From the author's own task on this node (28b19893)");
        prompt.Should().Contain("- the conflict is observed, never inferred");
        prompt.Should().Contain("- the dispute parks for the human");
    }

    /// <summary>
    /// The absence has to be the loud kind. A briefing that silently fell back to the pull
    /// request's own description would have the reviewer weighing the diff against the author's
    /// account of the diff, which always agrees with it — the one comparison that can never fail.
    /// </summary>
    [Fact]
    public void The_briefing_says_it_is_showing_the_description_rather_than_the_intent_when_no_task_is_readable()
    {
        string prompt = ReviewLapPromptBuilder.Build(Briefing());

        prompt.Should().Contain("cannot read an authoring task");
        prompt.Should().Contain("no stated objective or acceptance-criteria contract");
        prompt.Should().Contain("Adds the conflict read", "the pull request's own body is all there is, and it is shown");
    }

    [Fact]
    public void The_blast_radius_groups_the_files_two_segments_deep()
    {
        string prompt = ReviewLapPromptBuilder.Build(Briefing());

        prompt.Should().Contain("2 surfaces");
        prompt.Should().Contain("`src/Hall9k.Daemon` — 1 file(s), +80/-10");
        prompt.Should().Contain("`tests/Hall9k.Tests` — 1 file(s), +40/-8");
        prompt.Should().Contain("`src/Hall9k.Daemon/Closeout/CloseoutEngine.cs` +80/-10");
    }

    /// <summary>
    /// GitHub paginates the files list, so a large pull request comes back with an honest count
    /// and a short list. A grouping that silently covered part of the change would read as the
    /// whole blast radius, and nothing else on the briefing would contradict it.
    /// </summary>
    [Fact]
    public void A_truncated_file_list_says_so_and_keeps_the_pull_requests_own_totals()
    {
        string prompt = ReviewLapPromptBuilder.Build(Briefing() with
        {
            PullRequest = PullRequest() with { ChangedFiles = 137 },
        });

        prompt.Should().Contain("137 files, +120/-18 overall");
        prompt.Should().Contain("GitHub served only 2 of those 137 files");
        prompt.Should().Contain("read the diff directly for the rest");
    }

    [Fact]
    public void A_complete_file_list_says_nothing_about_truncation()
    {
        string prompt = ReviewLapPromptBuilder.Build(Briefing());

        prompt.Should().NotContain("GitHub served only");
    }

    /// <summary>
    /// The branch whose whole point is admitting a gap must not print a plausible number into it.
    /// The changed-file count falls back to the list's own length, which is zero exactly here, and
    /// "0 file(s)" reads as "this pull request changes nothing" (self-review, round two).
    /// </summary>
    [Fact]
    public void No_file_list_and_no_count_admits_both_rather_than_reporting_zero_files()
    {
        string prompt = ReviewLapPromptBuilder.Build(Briefing() with
        {
            PullRequest = PullRequest() with { Files = [], ChangedFiles = 0 },
        });

        prompt.Should().Contain("no file list for this pull request");
        prompt.Should().Contain("no file count reported either");
        prompt.Should().NotContain("0 file(s)", "zero would read as a pull request that changes nothing");
        prompt.Should().Contain("+120/-18", "the totals GitHub did report are still stated");
    }

    [Fact]
    public void No_file_list_but_a_count_states_the_count_it_did_report()
    {
        string prompt = ReviewLapPromptBuilder.Build(Briefing() with
        {
            PullRequest = PullRequest() with { Files = [], ChangedFiles = 9 },
        });

        prompt.Should().Contain("9 file(s), +120/-18");
        prompt.Should().NotContain("no file count reported either");
    }

    /// <summary>
    /// One segment would collapse this codebase's whole <c>src/</c> tree into a single bucket,
    /// and a blast radius that cannot tell the CLI from the daemon is not a blast radius.
    /// </summary>
    [Theory]
    [InlineData("src/Hall9k.Cli/Program.cs", "src/Hall9k.Cli")]
    [InlineData("src\\Hall9k.Cli\\Program.cs", "src/Hall9k.Cli")]
    [InlineData("AGENTS.md", "AGENTS.md")]
    [InlineData("docs/cli.md", "docs/cli.md")]
    public void A_files_surface_is_its_first_two_path_segments(string path, string expected) =>
        ReviewLapPromptBuilder.SurfaceOf(path).Should().Be(expected);

    [Fact]
    public void An_unfinished_check_is_reported_as_having_concluded_nothing()
    {
        string prompt = ReviewLapPromptBuilder.Build(Briefing());

        prompt.Should().Contain("ci / build: SUCCESS");
        prompt.Should().Contain("ci / test: IN_PROGRESS — no conclusion yet");
    }

    /// <summary>
    /// The three-way distinction the rollup actually supports. An unobserved rollup and an
    /// observed-but-empty one are different sentences, and neither may read as "CI passed" —
    /// which is what an empty section would say to anybody skimming.
    /// </summary>
    [Fact]
    public void An_unobserved_status_rollup_is_told_apart_from_an_empty_one()
    {
        string unobserved = ReviewLapPromptBuilder.Build(Briefing() with
        {
            PullRequest = PullRequest() with { Checks = [], ChecksObserved = false },
        });
        string empty = ReviewLapPromptBuilder.Build(Briefing() with
        {
            PullRequest = PullRequest() with { Checks = [], ChecksObserved = true },
        });

        unobserved.Should().Contain("no status rollup for this head at all");
        unobserved.Should().Contain("not the same as");
        empty.Should().Contain("nothing ran against this head");
        empty.Should().NotContain("no status rollup for this head at all");
    }

    [Fact]
    public void The_authors_unclaimed_residuals_are_named_with_where_they_came_from()
    {
        string prompt = ReviewLapPromptBuilder.Build(Briefing() with
        {
            AuthorRun = new ReviewLapAuthorRun(
                "Settled", ResidualsFixed: 3, ResidualsRouted: 1,
                UnclaimedResiduals: ["high at src/Hall9k.Daemon/Closeout/CloseoutEngine.cs:88 — met the fix bar"],
                Rulings: ["MergeReady: confirmed by git log, not a real finding"]),
        });

        prompt.Should().Contain("Unclaimed residuals (1)");
        prompt.Should().Contain("no knowledge of the author's intent");
        prompt.Should().Contain("high at src/Hall9k.Daemon/Closeout/CloseoutEngine.cs:88");
        prompt.Should().Contain("Disputes and rulings (1)");
        prompt.Should().Contain("not a reason a reviewer cannot reach a different one");
        prompt.Should().Contain("3 residual(s) fixed, 1 routed elsewhere");
    }

    [Fact]
    public void The_authors_run_section_is_absent_when_this_node_cannot_read_it()
    {
        string prompt = ReviewLapPromptBuilder.Build(Briefing());

        prompt.Should().NotContain(
            "What the author's own run already settled",
            "an absent read is an absent section, not an empty one implying the platform found nothing");
    }

    [Fact]
    public void The_briefing_volunteers_no_review_direction_and_ends_only_on_the_verdict_commands()
    {
        Guid taskId = DomainId.New();
        string prompt = ReviewLapPromptBuilder.Build(Briefing() with { TaskId = taskId });

        prompt.Should().Contain("Do not open with test scenarios");
        prompt.Should().Contain("Once they ask");
        prompt.Should().Contain("Never commit to or push this pull request's branch");
        prompt.Should().Contain("Tests the reviewer writes go on a branch of their own");
        prompt.Should().Contain("--stacked-on");
        prompt.Should().Contain("You never post to GitHub");
        prompt.Should().Contain($"h9k pr approve {taskId}");
        prompt.Should().Contain($"h9k pr request-changes {taskId}");
        prompt.Should().Contain("never on its own");
        prompt.Should().Contain(
            "Both are denied for this session",
            "the briefing prints the two verdict commands with the task id filled in, so it has to say they "
            + "are the reviewer's to run — and the guard denies them, so the session cannot run one anyway "
            + "(independent pre-PR review, cycle 1, conformance lens)");
    }

    [Fact]
    public void The_briefing_names_the_pull_requests_own_base_as_the_diff_range()
    {
        string prompt = ReviewLapPromptBuilder.Build(Briefing());

        prompt.Should().Contain(
            "git diff origin/main...HEAD",
            "the range is the reviewed pull request's own base, never the project's stand-in for it");
    }

    [Fact]
    public void The_no_worktree_lap_is_told_there_is_no_checkout()
    {
        string prompt = ReviewLapPromptBuilder.Build(Briefing() with { WorktreePath = string.Empty });

        prompt.Should().Contain("No checkout was made for this lap (`--no-worktree`)");
        prompt.Should().NotContain("git diff origin/main...HEAD", "there is nothing local to diff");
    }

    /// <summary>
    /// A pull request title is written by somebody else and this briefing is printed to a
    /// terminal and pasted into a session. A bidirectional override in it would be obeyed rather
    /// than shown — the same defusal <c>PullRequestBody</c> applies to everything it relays.
    /// </summary>
    [Fact]
    public void Relayed_text_is_defused_before_it_reaches_the_briefing()
    {
        string prompt = ReviewLapPromptBuilder.Build(Briefing() with
        {
            PullRequest = PullRequest() with { Title = "Fix the thing‮gnihton‬" },
        });

        prompt.Should().NotContain("‮");
        prompt.Should().NotContain("‬");
    }

    [Fact]
    public void The_review_lap_settings_deny_every_way_work_leaves_this_machine()
    {
        string settings = ClaudeSettingsFile.BuildForReviewLap(TimeSpan.FromMinutes(30));

        using JsonDocument document = JsonDocument.Parse(settings);
        document.RootElement.GetProperty("includeCoAuthoredBy").GetBoolean().Should().BeFalse(
            "the platform's standing conventions still apply to a lap");
        JsonElement deny = document.RootElement.GetProperty("permissions").GetProperty("deny");
        deny.EnumerateArray().Select(rule => rule.GetString()).Should().BeEquivalentTo(
            ClaudeSettingsFile.ReviewLapDeniedTools);
        deny.EnumerateArray().Select(rule => rule.GetString()).Should().Contain("Bash(git push:*)");
        deny.EnumerateArray().Select(rule => rule.GetString()).Should().Contain(
            "Bash(gh api:*)",
            "gh api is the surface GitHubPullRequestSurface.PostReviewAsync itself posts through, so a list of "
            + "the four gh pr verbs alone left a session able to post a review — or start a thread — under the "
            + "reviewer's own login (independent pre-PR review, cycle 1, both lenses)");
        deny.EnumerateArray().Select(rule => rule.GetString()).Should().Contain(
            ["Bash(gh pr update-branch:*)", "Bash(gh pr edit:*)", "Bash(gh pr ready:*)", "Bash(gh pr reopen:*)"],
            "the write half of gh pr is denied whole rather than by the few verbs a session reaching for a "
            + "REVIEW would use: gh pr update-branch merges the base into somebody else's branch server-side "
            + "and gh pr edit --body rewrites their description, both ordinary first-class verbs and both "
            + "reachable from a reviewer's casual 'bring it current with main' (independent pre-PR review, "
            + "cycle 1, adversarial lens)");
        deny.EnumerateArray().Select(rule => rule.GetString()).Should().NotContain(
            ["Bash(gh pr view:*)", "Bash(gh pr diff:*)", "Bash(gh pr checks:*)"],
            "reading the pull request is most of what a lap does");
        deny.EnumerateArray().Select(rule => rule.GetString()).Should().Contain(
            ["Bash(gh issue comment:*)", "Bash(gh issue edit:*)", "Bash(gh issue close:*)", "Bash(gh issue lock:*)"],
            "issues and pull requests share one number space and one REST resource — this codebase's own "
            + "GitHubWorkItemProvider observes gh issue view resolving a pull request — so gh issue comment "
            + "<pr-number> starts a thread on the pull request under the reviewer's login through a "
            + "first-class verb, the exact write the gh api .../issues/<n>/comments denial exists for "
            + "(independent pre-PR review, cycle 2, adversarial lens)");
        deny.EnumerateArray().Select(rule => rule.GetString()).Should().NotContain(
            ["Bash(gh issue view:*)", "Bash(gh issue list:*)", "Bash(gh issue status:*)"],
            "a lap reads the issues a pull request cites");
        deny.EnumerateArray().Select(rule => rule.GetString()).Should().Contain(
            ["Bash(h9k pr approve:*)", "Bash(h9k pr request-changes:*)"],
            "the platform's own verdict commands post through that same gh api endpoint under the reviewer's "
            + "own login AND finalize the task, and the lap's briefing prints both with the task id filled in "
            + "— so denying only the gh side left the session handed the exact spelling of a review it could "
            + "post for the reviewer (independent pre-PR review, cycle 1, conformance lens)");
        ClaudeSettingsFile.ReviewLapDeniedTools.Should().NotContain(
            rule => rule!.Contains("git commit", StringComparison.Ordinal),
            "a reviewer's own end-to-end tests have to be committable — the detached checkout is what makes a commit here harmless");
        ClaudeSettingsFile.ReviewLapDeniedTools.Should().NotContain(
            rule => rule!.Contains("h9k review resolve", StringComparison.Ordinal),
            "ending a lap without posting anything is not the authorship invariant this list defends, and a "
            + "lap ended early is recoverable with one h9k pr review");
    }

    [Fact]
    public void The_in_worktree_guard_carries_the_same_rules_and_nothing_else()
    {
        using JsonDocument document = JsonDocument.Parse(ReviewLapGuardFile.Content());

        document.RootElement.EnumerateObject().Select(property => property.Name).Should().Equal(
            ["permissions"],
            "the guard adds a restriction to whatever a session already resolves rather than replacing its configuration");
        document.RootElement.GetProperty("permissions").GetProperty("deny")
            .EnumerateArray().Select(rule => rule.GetString())
            .Should().BeEquivalentTo(ClaudeSettingsFile.ReviewLapDeniedTools);
    }

    /// <summary>
    /// Re-entering a lap — closing the terminal and re-running <c>h9k pr review</c> — finds the
    /// guard the first entry wrote, and that has to read as "still guarded" rather than as
    /// somebody else's settings file: the existence check alone warned every re-entering reviewer
    /// that the push guard was NOT written there while it sat in the checkout, active
    /// (independent pre-PR review, cycle 1, adversarial lens).
    /// </summary>
    [Fact]
    public async Task The_guard_tells_its_own_earlier_file_apart_from_the_pull_requests_own()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        string worktree = Path.Combine(Path.GetTempPath(), $"hall9k-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktree);
        try
        {
            ReviewLapGuardOutcome first = await ReviewLapGuardFile.InstallAsync(worktree, cts.Token);
            ReviewLapGuardOutcome again = await ReviewLapGuardFile.InstallAsync(worktree, cts.Token);

            first.Should().Be(ReviewLapGuardOutcome.Written);
            again.Should().Be(
                ReviewLapGuardOutcome.AlreadyGuarded,
                "the file it found is its own from the entry before, so the checkout is covered");

            await File.WriteAllTextAsync(
                ReviewLapGuardFile.PathIn(worktree), """{"permissions": {"allow": ["Bash(git push:*)"]}}""",
                cts.Token);
            ReviewLapGuardOutcome foreign = await ReviewLapGuardFile.InstallAsync(worktree, cts.Token);

            foreign.Should().Be(
                ReviewLapGuardOutcome.AlreadyPresent,
                "a settings file the pull request's own tree carries is never overwritten, and the reviewer is "
                + "told the guard travels with --settings instead");
        }
        finally
        {
            Directory.Delete(worktree, recursive: true);
        }
    }

    [Fact]
    public async Task The_guard_reports_no_worktree_rather_than_a_failure_when_there_is_no_checkout()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        ReviewLapGuardOutcome outcome = await ReviewLapGuardFile.InstallAsync(
            string.Empty, cts.Token);

        outcome.Should().Be(ReviewLapGuardOutcome.NoWorktree, "an absence is not a write that failed");
    }

    [Theory]
    [InlineData("src/Hall9k.Cli/Program.cs:42: this swallows the cancellation", "src/Hall9k.Cli/Program.cs", 42, "this swallows the cancellation")]
    [InlineData("  AGENTS.md:7:   the rule has no origin incident  ", "AGENTS.md", 7, "the rule has no origin incident")]
    // The leftmost ":<digits>:" is the separator, so a colon-number-colon inside the reviewer's
    // own prose stays in their prose. Greedy first, which split at the last one and named a path
    // that does not exist (independent pre-PR review, cycle 1, adversarial lens): free text
    // carries such a sequence routinely, a repo-relative path almost never, and on Windows never.
    [InlineData("src/App.cs:42: see RFC 3986 section 3:2: wrong scheme", "src/App.cs", 42, "see RFC 3986 section 3:2: wrong scheme")]
    public void A_finding_is_parsed_as_path_line_text(string finding, string path, int line, string body)
    {
        PullRequestReviewLineComment parsed = PullRequestReviewLineComment.Parse(finding);

        parsed.Path.Should().Be(path);
        parsed.Line.Should().Be(line);
        parsed.Body.Should().Be(body);
        parsed.ToString().Should().Be($"{path}:{line}: {body}");
    }

    /// <summary>
    /// Refused rather than repaired. Every plausible repair reaches GitHub and is wrong there: a
    /// finding with no line has no diff position to attach to, and quietly moving it into the
    /// review body relocates a comment the reviewer aimed at one line.
    /// </summary>
    [Theory]
    [InlineData("src/Program.cs: no line number at all")]
    [InlineData("just some prose about the diff")]
    [InlineData("src/Program.cs:42:")]
    [InlineData("")]
    // A line number that cannot be an int is not a line either, and it reaches this same refusal
    // rather than escaping as an OverflowException/FormatException that Program.cs's exception
    // mapping never sees — a raw stack trace where a self-correctable message was designed
    // (independent pre-PR review, cycle 1, both lenses). Second case: Arabic-Indic digits, which
    // .NET's \d matched happily and int.Parse then refused.
    [InlineData("src/Program.cs:99999999999: a line number past int.MaxValue")]
    [InlineData("src/Program.cs:٤٢: digits pasted out of a document")]
    public void A_finding_that_names_no_line_is_refused(string finding)
    {
        Action act = () => PullRequestReviewLineComment.Parse(finding);

        act.Should().Throw<DomainValidationException>().WithMessage("*path:line: text*");
    }

    [Fact]
    public void The_posted_review_payload_pins_the_head_and_states_the_side()
    {
        string payload = GitHubPullRequestSurface.BuildPayload(
            "0f1e2d3c4b5a", ReviewerVerdict.ChangesRequested, "Two real defects.",
            [new PullRequestReviewLineComment("src/Program.cs", 42, "this swallows the cancellation")]);

        using JsonDocument document = JsonDocument.Parse(payload);
        document.RootElement.GetProperty("commit_id").GetString().Should().Be("0f1e2d3c4b5a");
        document.RootElement.GetProperty("event").GetString().Should().Be("REQUEST_CHANGES");
        JsonElement comment = document.RootElement.GetProperty("comments")[0];
        comment.GetProperty("side").GetString().Should().Be("RIGHT");
        comment.GetProperty("line").GetInt32().Should().Be(42);
    }

    /// <summary>
    /// A note is free text a human just typed. Built with string interpolation it would break the
    /// request the moment it contained a quote or a newline; built through a JSON writer it
    /// reaches GitHub as written.
    /// </summary>
    [Fact]
    public void A_note_carrying_quotes_and_newlines_survives_the_payload_intact()
    {
        string note = "The \"fence\" is read after the load.\nSee CloseoutEngine.cs:88.\\end";

        string payload = GitHubPullRequestSurface.BuildPayload(
            "abc123", ReviewerVerdict.Approved, note, []);

        using JsonDocument document = JsonDocument.Parse(payload);
        document.RootElement.GetProperty("body").GetString().Should().Be(note);
    }

    [Fact]
    public void An_unknown_verdict_maps_to_no_github_review_event()
    {
        ReviewerVerdict.Approved.GitHubEvent.Should().Be("APPROVE");
        ReviewerVerdict.ChangesRequested.GitHubEvent.Should().Be("REQUEST_CHANGES");
        ReviewerVerdict.Unknown.GitHubEvent.Should().BeEmpty(
            "there is no review GitHub could be asked to submit for a verdict nobody gave");
    }

    /// <summary>
    /// The note and each finding are posted verbatim under the reviewer's own login, so the lap
    /// that drafts them is told the house style (task 412afe6c). A lap composed with no project
    /// conventions still gets the platform's own text rather than nothing.
    /// </summary>
    [Fact]
    public void The_closing_section_carries_the_writing_conventions_the_note_is_posted_under()
    {
        string prompt = ReviewLapPromptBuilder.Build(Briefing());

        prompt.Should().Contain("No em dashes (U+2014)")
            .And.Contain("How a draft you hand them reads");
        prompt.IndexOf("No em dashes (U+2014)", StringComparison.Ordinal)
            .Should().BeGreaterThan(prompt.IndexOf("h9k pr approve", StringComparison.Ordinal),
                "it governs the note those two commands post");
    }

    [Fact]
    public void A_project_that_states_its_own_conventions_is_what_the_lap_reads()
    {
        string prompt = ReviewLapPromptBuilder.Build(
            Briefing() with { WritingConventions = WritingConventions.Parse("Terse. British spelling.") });

        prompt.Should().Contain("Terse. British spelling.").And.NotContain("No em dashes (U+2014)");
    }

    private static ReviewLapBriefing Briefing() => new(
        DomainId.New(),
        PullRequest(),
        "hall9k",
        "/home/reviewer/.hall9k/projects/hall9k/repo",
        "/home/reviewer/.hall9k/projects/hall9k/repo/wt-pr-42",
        StatedObjective: null,
        AcceptanceCriteria: [],
        AuthorTaskShortId: null,
        FindingsReport: null,
        AuthorRun: null);

    private static PullRequestSurface PullRequest() => new(
        "acme/web",
        42,
        "Teach the closeout monitor to read a rebase conflict",
        "Adds the conflict read and the dispute park behind it.",
        "OPEN",
        "main",
        "task/9f2-conflict-read",
        "0f1e2d3c4b5a69788796a5b4c3d2e1f001234567",
        new Uri("https://github.com/acme/web/pull/42"),
        "someone-else",
        Additions: 120,
        Deletions: 18,
        ChangedFiles: 2,
        Files:
        [
            new PullRequestFileChange("src/Hall9k.Daemon/Closeout/CloseoutEngine.cs", 80, 10),
            new PullRequestFileChange("tests/Hall9k.Tests/Integration/CloseoutEngineTests.cs", 40, 8),
        ],
        Checks:
        [
            new PullRequestCheck("build", "ci", "COMPLETED", "SUCCESS"),
            new PullRequestCheck("test", "ci", "IN_PROGRESS", null),
        ],
        ChecksObserved: true);
}

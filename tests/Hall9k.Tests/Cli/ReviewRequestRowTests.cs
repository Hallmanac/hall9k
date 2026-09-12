using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.AutoPrReview;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Spectre.Console;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The two rows Decisions Log #161 requires of <c>h9k status</c>, in the words an operator
/// actually reads: a needs-you row wherever nothing started and the operator has to act, and an
/// informational row wherever the daemon is already doing the work — never needs-you there,
/// because a busy login must not be nagged for a review a task is already running. Also the
/// always-printed per-project setting line, and <c>h9k project show</c>'s own row, both of which
/// exist because the origin incident (2026-09-08) was an invisible state rather than a wrong one.
/// </summary>
public sealed class ReviewRequestRowTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_off_project_asks_the_operator_and_names_both_commands()
    {
        ReviewRequestRow row = ReviewRequestPane.Compose(
            Observed(ReviewRequestOutcome.HeldSettingOff, Now.AddMinutes(-5)),
            "arx-platform",
            new AutoPrReviewSetting(AutoPrReviewSpeed.Off, Recorded: true),
            covering: null,
            Now);

        row.NeedsYou.Should().BeTrue("the operator is the only one who can act on it");
        row.Markup.Should().Contain("a review of acme/widgets#2033 was requested of brian");
        row.Markup.Should().Contain("auto pr-review is off here");
        row.Markup.Should().Contain("h9k task add --project arx-platform --from-pr 2033");
        row.Markup.Should().Contain("h9k project set arx-platform --auto-pr-review normal");
    }

    [Fact]
    public void An_on_project_with_a_live_task_is_informational_and_names_the_task()
    {
        Guid taskId = DomainId.New();

        ReviewRequestRow row = ReviewRequestPane.Compose(
            Observed(ReviewRequestOutcome.TaskCreated, Now.AddMinutes(-5), taskId),
            "arx-platform",
            AutoPrReviewSetting.Unrecorded,
            new CoveringReview(taskId, Live: true, "Working", AutoCreated: true),
            Now);

        row.NeedsYou.Should().BeFalse("a busy login is not nagged for work the daemon is already doing");
        row.Markup.Should().Contain("a review of acme/widgets#2033 was requested of brian");
        row.Markup.Should().Contain($"task {DomainId.Short(taskId)} is created and reviewing (Working)");
    }

    [Theory]
    [InlineData("Delivered")]
    [InlineData("Done")]
    public void The_row_follows_the_task_it_created(string stateWord)
    {
        Guid taskId = DomainId.New();

        ReviewRequestRow row = ReviewRequestPane.Compose(
            Observed(ReviewRequestOutcome.TaskCreated, Now.AddMinutes(-5), taskId),
            "arx-platform",
            AutoPrReviewSetting.Unrecorded,
            new CoveringReview(taskId, Live: stateWord != "Done", stateWord, AutoCreated: true),
            Now);

        row.NeedsYou.Should().BeFalse();
        row.Markup.Should().Contain(stateWord);
    }

    [Fact]
    public void A_task_that_adopted_the_request_by_hand_clears_the_off_projects_needs_you_row()
    {
        Guid taskId = DomainId.New();

        ReviewRequestRow row = ReviewRequestPane.Compose(
            Observed(ReviewRequestOutcome.HeldSettingOff, Now.AddMinutes(-5)),
            "arx-platform",
            new AutoPrReviewSetting(AutoPrReviewSpeed.Off, Recorded: true),
            new CoveringReview(taskId, Live: true, "Working", AutoCreated: false),
            Now);

        row.NeedsYou.Should().BeFalse("a task adopting the request is one of the four ways the row clears");
        row.Markup.Should().Contain(DomainId.Short(taskId));
    }

    /// <summary>
    /// A closed covering task holds back only what the engine's own re-mint guard actually holds
    /// back (independent pre-PR review, cycle 1, conformance lens): that guard matches on
    /// <c>WasAutoPrReviewCreated</c>, so a hand-adopted <c>--from-pr</c> task closed while the
    /// request still stands holds nothing at all on an on-by-default project — and a flat
    /// "nothing new starts on its own" there is the row overstating its own hold.
    /// </summary>
    [Theory]
    [InlineData(true, true, "nothing new starts on its own unless GitHub requests the review again")]
    [InlineData(true, false, "may start one of its own on the next sweep")]
    [InlineData(false, false, "nothing new starts on its own: auto pr-review is off here")]
    [InlineData(false, true, "nothing new starts on its own: auto pr-review is off here")]
    public void A_closed_covering_task_promises_only_the_hold_the_engines_own_guard_gives(
        bool settingOn, bool autoCreated, string expected)
    {
        Guid taskId = DomainId.New();

        ReviewRequestRow row = ReviewRequestPane.Compose(
            Observed(ReviewRequestOutcome.AlreadyCovered, Now.AddMinutes(-5), taskId),
            "arx-platform",
            settingOn
                ? AutoPrReviewSetting.Unrecorded
                : new AutoPrReviewSetting(AutoPrReviewSpeed.Off, Recorded: true),
            new CoveringReview(taskId, Live: false, "Done", autoCreated),
            Now);

        row.NeedsYou.Should().BeFalse("nothing is being asked of the operator either way");
        row.Markup.Should().Contain($"task {DomainId.Short(taskId)} already covered it (Done)");
        row.Markup.Should().Contain(expected);
    }

    /// <summary>
    /// The row names the login GitHub made the request of rather than asserting it is the
    /// reader's (independent pre-PR review, cycle 1, adversarial lens): rows are keyed per
    /// reviewer login because two installs with two <c>gh</c> authentications can share one
    /// database, and this pane has no observation of which login is reading it.
    /// </summary>
    [Fact]
    public void The_row_names_the_login_the_review_was_requested_of_rather_than_claiming_it_is_yours()
    {
        ObservedReviewRequest request = Observed(ReviewRequestOutcome.HeldSettingOff, Now.AddMinutes(-5));
        request.ReviewerLogin = "otherbot";

        ReviewRequestRow row = ReviewRequestPane.Compose(
            request, "arx-platform", new AutoPrReviewSetting(AutoPrReviewSpeed.Off, Recorded: true), null, Now);

        row.Markup.Should().Contain("was requested of otherbot");
        row.Markup.Should().NotContain("requested of you",
            "on a shared database that would read as the reader's own request and invite a duplicate adoption");
    }

    [Fact]
    public void A_row_that_records_no_reviewer_login_says_so_rather_than_naming_one()
    {
        ObservedReviewRequest request = Observed(ReviewRequestOutcome.HeldSettingOff, Now.AddMinutes(-5));
        request.ReviewerLogin = string.Empty;

        ReviewRequestRow row = ReviewRequestPane.Compose(
            request, "arx-platform", new AutoPrReviewSetting(AutoPrReviewSpeed.Off, Recorded: true), null, Now);

        row.Markup.Should().Contain("a login this row does not record");
    }

    /// <summary>
    /// A recorded login is a value off GitHub, so it goes through the same escaping every other
    /// recorded value in this pane does: Spectre reads '[' as markup, and an unescaped one is a
    /// rendering crash on the surface that has to still print when something else is wrong.
    /// </summary>
    [Fact]
    public void A_reviewer_login_carrying_markup_still_renders()
    {
        ObservedReviewRequest request = Observed(ReviewRequestOutcome.HeldSettingOff, Now.AddMinutes(-5));
        request.ReviewerLogin = "brian[bot]";

        ReviewRequestRow row = ReviewRequestPane.Compose(
            request, "arx-platform", new AutoPrReviewSetting(AutoPrReviewSpeed.Off, Recorded: true), null, Now);

        Rendered(row.Markup).Should().Contain("was requested of brian[bot]");
    }

    [Fact]
    public void An_off_project_turned_on_stops_asking_before_the_next_sweep_even_mints()
    {
        ReviewRequestRow row = ReviewRequestPane.Compose(
            Observed(ReviewRequestOutcome.HeldSettingOff, Now.AddMinutes(-5)),
            "arx-platform",
            AutoPrReviewSetting.Unrecorded,
            covering: null,
            Now);

        row.NeedsYou.Should().BeFalse("the setting is resolved at render time, so turning it on clears the row");
        row.Markup.Should().Contain("auto pr-review is on here (normal)");
        row.Markup.Should().Contain("starts on the next sweep");
    }

    [Fact]
    public void A_request_older_than_the_cutoff_asks_the_operator_and_names_its_age_even_with_the_setting_on()
    {
        ReviewRequestRow row = ReviewRequestPane.Compose(
            Observed(ReviewRequestOutcome.HeldBeforeCutoff, Now.AddDays(-14)),
            "arx-platform",
            AutoPrReviewSetting.Unrecorded,
            covering: null,
            Now);

        row.NeedsYou.Should().BeTrue();
        row.Markup.Should().Contain("14d ago", "the age is what tells an operator this is one of the stale ones");
        row.Markup.Should().Contain("predates auto pr-review's start on this install");
        row.Markup.Should().Contain("h9k task add --project arx-platform --from-pr 2033");
        row.Markup.Should().NotContain("--auto-pr-review normal",
            "turning the setting on would not start a stale request, so the row must not offer it as a lever");
    }

    [Fact]
    public void A_request_whose_own_time_could_not_be_read_asks_the_operator_and_says_why()
    {
        ObservedReviewRequest request = Observed(ReviewRequestOutcome.HeldRequestTimeUnknown, requestedAt: null);

        ReviewRequestRow row = ReviewRequestPane.Compose(
            request, "arx-platform", AutoPrReviewSetting.Unrecorded, covering: null, Now);

        row.NeedsYou.Should().BeTrue();
        row.Markup.Should().Contain("requested-at time could not be read");
    }

    [Fact]
    public void A_refused_mint_asks_the_operator_and_quotes_what_was_recorded()
    {
        ObservedReviewRequest request = Observed(ReviewRequestOutcome.MintFailed, Now.AddMinutes(-5));
        request.OutcomeDetail = "gh pr view exited 1";

        ReviewRequestRow row = ReviewRequestPane.Compose(
            request, "arx-platform", AutoPrReviewSetting.Unrecorded, covering: null, Now);

        row.NeedsYou.Should().BeTrue();
        row.Markup.Should().Contain("could not adopt it (gh pr view exited 1)");
    }

    [Fact]
    public void An_outcome_a_newer_build_recorded_asks_the_operator_rather_than_reading_as_handled()
    {
        ObservedReviewRequest request = Observed(ReviewRequestOutcome.TaskCreated, Now.AddMinutes(-5));
        request.Outcome = "SomethingElseEntirely";

        ReviewRequestRow row = ReviewRequestPane.Compose(
            request, "arx-platform", AutoPrReviewSetting.Unrecorded, covering: null, Now);

        row.NeedsYou.Should().BeTrue();
        row.Markup.Should().Contain("recorded by a newer build");
    }

    /// <summary>
    /// A held mention had nowhere to surface at all before this row existed (independent pre-PR
    /// review, cycle 1, adversarial lens, medium): <see cref="ObservedReviewMention"/> is permanent
    /// dedupe with no CLI reader, so the comment was silently lost to the operator forever, whether
    /// the hold was the project's own setting or the no-backfill cutoff.
    /// </summary>
    [Theory]
    [InlineData("HeldSettingOff", "while auto pr-review was off here")]
    [InlineData("HeldBeforeCutoff", "before auto pr-review's start on this install")]
    public void A_held_mention_asks_the_operator_and_names_who_tagged_it(string outcome, string expectedCause)
    {
        ReviewRequestRow row = ReviewRequestPane.ComposeMentionRow(
            ObservedMention(outcome), "arx-platform", covering: null);

        row.NeedsYou.Should().BeTrue("a held mention is never retried, so it is only ever the operator's to take");
        row.Markup.Should().Contain("a comment from ryan mentioned brian on acme/widgets#2033");
        row.Markup.Should().Contain(expectedCause);
        row.Markup.Should().Contain("a mention already seen is never retried");
        row.Markup.Should().Contain("h9k task add --project arx-platform --from-pr 2033");
    }

    [Fact]
    public void A_refused_mention_mint_asks_the_operator_and_quotes_what_was_recorded()
    {
        ObservedReviewMention mention = ObservedMention("MintFailed");
        mention.OutcomeDetail = "gh pr view exited 1";

        ReviewRequestRow row = ReviewRequestPane.ComposeMentionRow(mention, "arx-platform", covering: null);

        row.NeedsYou.Should().BeTrue();
        row.Markup.Should().Contain("could not adopt it (gh pr view exited 1)");
    }

    [Fact]
    public void A_held_mention_covered_by_a_task_is_informational_and_names_the_task()
    {
        Guid taskId = DomainId.New();

        ReviewRequestRow row = ReviewRequestPane.ComposeMentionRow(
            ObservedMention("HeldSettingOff"), "arx-platform",
            new CoveringReview(taskId, Live: true, "Working", AutoCreated: true));

        row.NeedsYou.Should().BeFalse("a task already covering the pull request is nothing to ask the operator");
        row.Markup.Should().Contain($"task {DomainId.Short(taskId)} already covers it (Working)");
    }

    [Fact]
    public void A_held_mention_with_no_recorded_author_says_so_rather_than_naming_one()
    {
        ObservedReviewMention mention = ObservedMention("HeldSettingOff");
        mention.CommentAuthorLogin = string.Empty;

        ReviewRequestRow row = ReviewRequestPane.ComposeMentionRow(mention, "arx-platform", covering: null);

        row.Markup.Should().Contain("a comment mentioned brian");
    }

    /// <summary>
    /// The login actually mentioned, not the reader's own (independent pre-PR review, cycle 1,
    /// conformance lens): two installs with two <c>gh</c> authentications can share one database,
    /// and this row has no observation of which login is reading it.
    /// </summary>
    [Fact]
    public void A_mention_row_with_no_recorded_login_says_so_rather_than_naming_one()
    {
        ObservedReviewMention mention = ObservedMention("HeldSettingOff");
        mention.MentionedLogin = string.Empty;

        ReviewRequestRow row = ReviewRequestPane.ComposeMentionRow(mention, "arx-platform", covering: null);

        row.Markup.Should().Contain("a login this row does not record");
    }

    /// <summary>
    /// The core loss the origin incident named (independent pre-PR review, cycle 1, both lenses):
    /// a mention that attached to a covering task with no follow-up dispatched used to render as
    /// though the task already answered it — the covering-task branch fires for every other
    /// outcome, but must not swallow this one.
    /// </summary>
    [Fact]
    public void An_attached_mention_with_no_follow_up_asks_the_operator_despite_the_covering_task()
    {
        Guid taskId = DomainId.New();
        ObservedReviewMention mention = ObservedMention("AttachedNoFollowUp");
        mention.TaskId = taskId;
        mention.OutcomeDetail = "recorded; auto-pr-review is off here, so no follow-up was dispatched";

        ReviewRequestRow row = ReviewRequestPane.ComposeMentionRow(
            mention, "arx-platform", new CoveringReview(taskId, Live: true, "Working", AutoCreated: true));

        row.NeedsYou.Should().BeTrue(
            "the task covers the pull request in general, but nothing ever answered this exact comment");
        row.Markup.Should().Contain($"attached to task {DomainId.Short(taskId)}");
        row.Markup.Should().Contain("no follow-up was dispatched");
        row.Markup.Should().Contain("h9k pr review acme/widgets#2033 --since-my-review");
    }

    /// <summary>
    /// Never dropped the way this outcome used to be (independent pre-PR review, cycle 1,
    /// adversarial lens): the identical treatment <see cref="ReviewRequestOutcome.Unknown"/> gets
    /// on the request side, rather than the row silently vanishing.
    /// </summary>
    [Fact]
    public void A_mention_outcome_a_newer_build_recorded_asks_the_operator_rather_than_vanishing()
    {
        ObservedReviewMention mention = ObservedMention("AttachedNoFollowUp");
        mention.Outcome = "SomethingElseEntirely";

        ReviewRequestRow row = ReviewRequestPane.ComposeMentionRow(mention, "arx-platform", covering: null);

        row.NeedsYou.Should().BeTrue();
        row.Markup.Should().Contain("recorded by a newer build");
    }

    private static ObservedReviewMention ObservedMention(string outcome)
    {
        Guid nodeId = DomainId.New();
        Guid projectId = DomainId.New();
        return new ObservedReviewMention
        {
            Id = ObservedReviewMention.ComputeId(nodeId, projectId, "acme/widgets", 2033, "brian", "IC_1"),
            ObservingNodeId = nodeId,
            ProjectId = projectId,
            Repository = "acme/widgets",
            Number = 2033,
            PullRequestUrl = "https://github.com/acme/widgets/pull/2033",
            MentionedLogin = "brian",
            CommentId = "IC_1",
            CommentAuthorLogin = "ryan",
            CommentBody = "@brian what do you think?",
            CommentUrl = "https://github.com/acme/widgets/pull/2033#issuecomment-1",
            CommentCreatedAt = Now.AddMinutes(-5),
            ObservedAt = Now,
            Outcome = outcome,
        };
    }

    [Fact]
    public void The_status_setting_line_is_printed_for_every_project_including_the_default()
    {
        IReadOnlyList<string> lines = ReviewRequestPane.SettingLines(
        [
            ("hall9k", AutoPrReviewSetting.Unrecorded),
            ("arx-platform", new AutoPrReviewSetting(AutoPrReviewSpeed.Off, Recorded: true)),
        ]);

        lines.Should().HaveCount(2);
        lines[0].Should().Contain("arx-platform").And.Contain("off").And.Contain("explicit")
            .And.Contain("h9k project set arx-platform --auto-pr-review normal");
        lines[1].Should().Contain("hall9k").And.Contain("on").And.Contain("normal").And.Contain("default");
    }

    [Fact]
    public void The_project_show_row_names_the_effective_value_and_its_origin_at_the_default()
    {
        string row = ProjectShowCommand.AutoPrReviewRow(Project(), AutoPrReviewSetting.Unrecorded);

        row.Should().Contain("normal");
        row.Should().Contain("default");
        row.Should().Contain("nothing recorded here");
        row.Should().Contain("h9k project set arx-platform --auto-pr-review off");
    }

    [Fact]
    public void The_project_show_row_names_an_explicit_opt_out_as_explicit()
    {
        string row = ProjectShowCommand.AutoPrReviewRow(
            Project(), new AutoPrReviewSetting(AutoPrReviewSpeed.Off, Recorded: true));

        row.Should().Contain("off");
        row.Should().Contain("explicit");
        row.Should().NotContain("nothing recorded here");
        row.Should().Contain("h9k project set arx-platform --auto-pr-review normal");
    }

    /// <summary>
    /// Every row and line this pane composes, put through a real Spectre console rather than
    /// only inspected as a string: the assertions above read the markup, and markup that reads
    /// correctly can still throw the moment it is actually rendered — an unbalanced tag, or a
    /// recorded value carrying a bracket that was not escaped. <c>h9k status</c> would take the
    /// whole pane down with it, which is the one surface that must still print when something
    /// else is wrong.
    /// </summary>
    [Fact]
    public void Every_row_and_setting_line_renders_through_a_real_console()
    {
        ObservedReviewRequest failed = Observed(ReviewRequestOutcome.MintFailed, Now.AddMinutes(-5));
        // A recorded reason that came off gh's own stderr, brackets and all: Spectre reads '['
        // as markup, so an unescaped detail is a rendering crash rather than an ugly line.
        failed.OutcomeDetail = "gh pr view exited 1 [see the daemon log]";

        (ObservedReviewRequest Request, AutoPrReviewSetting Setting, CoveringReview? Covering)[] shapes =
        [
            (Observed(ReviewRequestOutcome.HeldSettingOff, Now.AddMinutes(-5)),
                new AutoPrReviewSetting(AutoPrReviewSpeed.Off, Recorded: true), null),
            (Observed(ReviewRequestOutcome.HeldBeforeCutoff, Now.AddDays(-14)),
                AutoPrReviewSetting.Unrecorded, null),
            (Observed(ReviewRequestOutcome.HeldRequestTimeUnknown, null),
                AutoPrReviewSetting.Unrecorded, null),
            (failed, AutoPrReviewSetting.Unrecorded, null),
            (Observed(ReviewRequestOutcome.TaskCreated, Now.AddMinutes(-5)),
                AutoPrReviewSetting.Unrecorded,
                new CoveringReview(DomainId.New(), Live: true, "Working", AutoCreated: true)),
            (Observed(ReviewRequestOutcome.AlreadyCovered, Now.AddMinutes(-5)),
                AutoPrReviewSetting.Unrecorded,
                new CoveringReview(DomainId.New(), Live: false, "Done", AutoCreated: true)),
            (Observed(ReviewRequestOutcome.AlreadyCovered, Now.AddMinutes(-5)),
                AutoPrReviewSetting.Unrecorded,
                new CoveringReview(DomainId.New(), Live: false, "Done", AutoCreated: false)),
            (Observed(ReviewRequestOutcome.AlreadyCovered, Now.AddMinutes(-5)),
                new AutoPrReviewSetting(AutoPrReviewSpeed.Off, Recorded: true),
                new CoveringReview(DomainId.New(), Live: false, "Abandoned", AutoCreated: false)),
            (Observed(ReviewRequestOutcome.TaskCreated, Now.AddMinutes(-5)),
                AutoPrReviewSetting.Unrecorded, null),
        ];

        foreach ((ObservedReviewRequest request, AutoPrReviewSetting setting, CoveringReview? covering) in shapes)
        {
            ReviewRequestRow row = ReviewRequestPane.Compose(
                request, "arx-platform [beta]", setting, covering, Now);

            Rendered(row.Markup).Should().Contain("a review of acme/widgets#2033 was requested of brian")
                .And.NotContain("[dim]", "the markup was rendered, not printed as text");
        }

        foreach (string line in ReviewRequestPane.SettingLines(
        [
            ("arx-platform [beta]", AutoPrReviewSetting.Unrecorded),
            ("hall9k", new AutoPrReviewSetting(AutoPrReviewSpeed.Off, Recorded: true)),
        ]))
        {
            Rendered(line).Should().Contain("auto pr-review:");
        }

        Rendered(ProjectShowCommand.AutoPrReviewRow(Project(), AutoPrReviewSetting.Unrecorded))
            .Should().Contain("normal");
        Rendered(ProjectShowCommand.ClaimGateRow(Project(), recorded: false)).Should().Contain("off");
        Rendered(ProjectShowCommand.SkipPermissionsRow(Project(), recorded: false)).Should().Contain("no (default");
        Rendered(ProjectShowCommand.BacklogPolicyRow(Project(), recorded: false)).Should().Contain("none (default");
        Rendered(ProjectShowCommand.CloseLinkedIssueRow(Project(), recorded: false))
            .Should().Contain("when-all-tasks-close (default");
    }

    /// <summary>The plain text a real console makes of one markup string — it throws on markup a terminal could not render.</summary>
    private static string Rendered(string markup)
    {
        StringWriter writer = new();
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            // Spectre's default profile enrichers turn ANSI back on whenever they recognise the
            // host CI (GitHub Actions among them), whatever AnsiSupport.No asked for. Left on, the
            // styling Spectre itself emits would put escape sequences in the rendered string and
            // these assertions would be reading the harness rather than the value.
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = 400;
        console.Write(new Markup(markup));
        return writer.ToString();
    }

    [Theory]
    [InlineData(true, "explicit")]
    [InlineData(false, "default — nothing recorded here")]
    public void The_claim_gate_row_names_its_origin_too_rather_than_a_bare_off(bool recorded, string expectedOrigin)
    {
        string row = ProjectShowCommand.ClaimGateRow(Project(), recorded);

        row.Should().Contain($"off ({expectedOrigin})");
        row.Should().Contain("h9k project set arx-platform --claim-gate tracker-assignee");
    }

    /// <summary>
    /// The same always-printed origin rule, on the pane's other rows whose effective value reads
    /// identically whichever way it got there (independent pre-PR review, cycle 1, conformance
    /// lens): "no", "none" and "when-all-tasks-close" are each both the platform's untouched
    /// default and a value an operator can type, and a row rendering the two the same way is the
    /// indistinguishability Decisions Log #161 ended for auto pr-review beside them.
    /// </summary>
    [Theory]
    [InlineData(true, "explicit")]
    [InlineData(false, "default — nothing recorded here")]
    public void The_panes_other_default_driven_rows_name_their_origin_too(bool recorded, string expectedOrigin)
    {
        ProjectShowCommand.SkipPermissionsRow(Project(), recorded)
            .Should().Contain($"no ({expectedOrigin})");
        ProjectShowCommand.BacklogPolicyRow(Project(), recorded)
            .Should().Contain($"none ({expectedOrigin})");
        ProjectShowCommand.CloseLinkedIssueRow(Project(), recorded)
            .Should().Contain($"when-all-tasks-close ({expectedOrigin})");
    }

    /// <summary>
    /// A value that can only have been recorded says nothing about origin: there is no default it
    /// could be confused with, and a parenthetical on every row is how the ones that need it stop
    /// being read. SkipPermissions no longer belongs to this group — registration is a second way
    /// to reach "yes" now (task: a newly registered project skips permission prompts by default,
    /// recorded at registration), so its "yes" row does name an origin; that behaviour has its own
    /// test below.
    /// </summary>
    [Fact]
    public void A_row_whose_value_could_only_have_been_chosen_says_nothing_about_origin()
    {
        ProjectDetails project = Project();
        project.BacklogPolicy = BacklogPolicy.Jira;
        project.CloseLinkedIssue = CloseLinkedIssueRule.Never;

        ProjectShowCommand.BacklogPolicyRow(project, recorded: true).Should().NotContain("explicit");
        ProjectShowCommand.CloseLinkedIssueRow(project, recorded: true).Should().NotContain("explicit");
    }

    /// <summary>
    /// "yes" now has two possible origins (task: a newly registered project skips permission
    /// prompts by default, recorded at registration): a project <c>h9k project add</c> registered,
    /// which never went through <c>h9k project set</c> at all, and a project whose operator typed
    /// <c>--skip-permissions true</c> explicitly. The row names which one it is instead of
    /// collapsing them into a bare "yes" the way it did before registration could produce one.
    /// </summary>
    [Theory]
    [InlineData(true, "explicit")]
    [InlineData(false, "recorded at registration")]
    public void The_skip_permissions_row_names_which_of_two_origins_produced_yes(bool recorded, string expectedOrigin)
    {
        ProjectDetails project = Project();
        project.SkipPermissions = true;

        Rendered(ProjectShowCommand.SkipPermissionsRow(project, recorded)).Should().Contain($"yes ({expectedOrigin})");
    }

    [Fact]
    public void The_claim_gate_row_says_what_the_gate_does_once_it_is_on()
    {
        ProjectDetails project = Project();
        project.ClaimGate = ClaimGate.TrackerAssignee;

        ProjectShowCommand.ClaimGateRow(project, recorded: true)
            .Should().StartWith("tracker-assignee").And.Contain("h9k task assign <id> --take");
    }

    private static ProjectDetails Project() => new() { Id = DomainId.New(), Name = "arx-platform" };

    private static ObservedReviewRequest Observed(
        ReviewRequestOutcome outcome, DateTimeOffset? requestedAt, Guid? taskId = null)
    {
        Guid nodeId = DomainId.New();
        Guid projectId = DomainId.New();
        return new ObservedReviewRequest
        {
            Id = ObservedReviewRequest.ComputeId(nodeId, projectId, "acme/widgets", 2033, "brian"),
            ObservingNodeId = nodeId,
            ProjectId = projectId,
            Repository = "acme/widgets",
            Number = 2033,
            PullRequestUrl = "https://github.com/acme/widgets/pull/2033",
            ReviewerLogin = "brian",
            RequesterLogin = "alice",
            RequestedAt = requestedAt,
            FirstObservedAt = Now,
            LastObservedAt = Now,
            Outcome = outcome,
            TaskId = taskId,
        };
    }
}

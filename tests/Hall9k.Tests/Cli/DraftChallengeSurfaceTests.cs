using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Trust;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.TestSupport;
using Spectre.Console.Cli;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// What a person sees of a draft's challenge: the rows <c>h9k task show</c> prints, the <c>Next:</c> line every
/// command that prints one shares, the project row, the config rows, and that a dispatched session is refused the verb.
/// </summary>
public sealed class DraftChallengeSurfaceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ThisNode = DomainId.New();
    private static readonly Guid OtherNode = DomainId.New();

    private static TaskDetails Draft() => new()
    {
        Id = DomainId.New(),
        Objective = "Challenge me",
        AcceptanceCriteria = ["a criterion"],
        State = TaskState.Draft,
        Type = TaskType.Feature,
    };

    private static DraftChallengeRecorded Verdict(
        ChallengeVerdict verdict, Guid node, int must = 0, int should = 0, int park = 0) =>
        new(DomainId.New(), DomainId.New(), node, verdict, ChallengeFailureReason.Unknown, must, should, park,
            AgentModel.ChallengeDefault, AgentEffort.High, Now);

    private static DraftChallengeRecorded Failed(ChallengeFailureReason reason, Guid node, Guid? session) =>
        new(DomainId.New(), session, node, ChallengeVerdict.Unknown, reason, 0, 0, 0,
            AgentModel.ChallengeDefault, AgentEffort.High, Now);

    private static string Rows(TaskDetails details, Guid? node = null, bool requirementOn = true) =>
        string.Join('\n', TaskShowCommand.ChallengeRows(
            details, new ProjectDetails { Name = "demo", RequireChallengeRecorded = requirementOn ? null : false },
            node, MemberLabelLookup.Empty, skippedBy: "Brian").Select(row => $"{row.Label}: {row.Markup}"));

    [Fact]
    public void A_request_waiting_for_the_daemon_says_so()
    {
        TaskDetails details = Draft();
        details.ChallengeRequested = true;

        Rows(details, ThisNode).Should().Contain("Challenge: ").And.Contain("requested").And.Contain("waiting for the daemon");
    }

    [Fact]
    public void A_running_challenge_names_the_node_the_model_and_the_effort()
    {
        TaskDetails details = Draft();
        details.RunningChallengeSessionId = DomainId.New();
        details.RunningChallengeNodeId = ThisNode;
        details.RunningChallengeDispatchedAt = Now;
        details.RunningChallengeModel = AgentModel.ChallengeDefault;
        details.RunningChallengeEffort = AgentEffort.High;

        Rows(details, ThisNode).Should().Contain("running").And.Contain("on this node")
            .And.Contain("claude-opus-5-5[[1m]]").And.Contain("high effort");
    }

    [Fact]
    public void A_verdict_prints_its_counts_when_it_landed_its_model_its_report_path_and_whether_the_draft_was_revised_since()
    {
        TaskDetails details = Draft();
        DraftChallengeRecorded verdict = Verdict(ChallengeVerdict.PublishAsRevised, ThisNode, must: 2, should: 3, park: 1);
        details.LatestChallengeOutcome = verdict;
        details.LatestChallengeVerdict = verdict;

        string unrevised = Rows(details, ThisNode);
        unrevised.Should().Contain("publish as revised").And.Contain("2 must, 3 should, 1 park").And.Contain("landed")
            .And.Contain("high effort").And.Contain(RunPaths.ChallengeReportFile(verdict.SessionId!.Value))
            .And.Contain("the draft was not revised since");

        details.RevisedSinceChallengeVerdict = true;
        Rows(details, ThisNode).Should().Contain("the draft was revised since");
    }

    [Fact]
    public void On_a_node_that_does_not_hold_the_report_the_node_that_does_is_named_and_no_path_is_printed()
    {
        TaskDetails details = Draft();
        DraftChallengeRecorded verdict = Verdict(ChallengeVerdict.PublishAsWritten, OtherNode);
        details.LatestChallengeOutcome = verdict;
        details.LatestChallengeVerdict = verdict;

        string rows = Rows(details, ThisNode);

        rows.Should().Contain($"node {DomainId.Short(OtherNode)}").And.Contain("which ran the session")
            .And.NotContain(RunPaths.ChallengeReportFile(verdict.SessionId!.Value));
    }

    [Fact]
    public void A_failure_prints_its_reason_and_the_verdict_the_gate_still_reads_beneath_it()
    {
        TaskDetails details = Draft();
        DraftChallengeRecorded earlier = Verdict(ChallengeVerdict.DoNotPublish, ThisNode, must: 1);
        DraftChallengeRecorded failure = Failed(ChallengeFailureReason.TimedOut, ThisNode, DomainId.New());
        details.LatestChallengeVerdict = earlier;
        details.LatestChallengeOutcome = failure;

        string rows = Rows(details, ThisNode);

        rows.Should().Contain("failed").And.Contain(ChallengeFailureReason.TimedOut.Description)
            .And.Contain("Verdict on record").And.Contain("do not publish");
    }

    [Fact]
    public void A_failure_with_no_session_says_no_report_exists()
    {
        TaskDetails details = Draft();
        details.LatestChallengeOutcome = Failed(ChallengeFailureReason.NoLongerDraft, ThisNode, session: null);

        Rows(details, ThisNode).Should().Contain("no session ran");
    }

    [Fact]
    public void The_gate_row_appears_only_while_publish_would_be_refused_and_names_both_ways_out()
    {
        TaskDetails details = Draft();

        Rows(details, ThisNode).Should().Contain("Publish gate").And.Contain("h9k task challenge").And.Contain("--skip-challenge");
        Rows(details, ThisNode, requirementOn: false).Should().NotContain("Publish gate");

        DraftChallengeRecorded passing = Verdict(ChallengeVerdict.PublishAsWritten, ThisNode);
        details.LatestChallengeVerdict = passing;
        details.LatestChallengeOutcome = passing;
        Rows(details, ThisNode).Should().NotContain("Publish gate");
    }

    [Fact]
    public void A_skipped_challenge_shows_the_reason_and_who_gave_it()
    {
        TaskDetails details = Draft();
        details.State = TaskState.Published;
        details.ChallengeSkipReason = "a typo fix";
        details.ChallengeSkippedAt = Now;

        Rows(details, ThisNode).Should().Contain("Challenge skipped").And.Contain("Brian").And.Contain("a typo fix");
    }

    [Fact]
    public void A_task_never_challenged_prints_nothing_when_the_requirement_is_off()
    {
        Rows(Draft(), ThisNode, requirementOn: false).Should().BeEmpty();
    }

    // ---- the Next: line ----

    [Fact]
    public void The_draft_next_step_points_at_the_challenge_while_the_gate_would_refuse_and_at_publish_otherwise()
    {
        TaskDetails details = Draft();

        string withGate = string.Join('\n', TaskNextStep.Lines(details, hasOpenDependency: false, challengeRequired: true));
        withGate.Should().Contain($"h9k task challenge {TaskListCommand.ShortId(details.Id)}")
            .And.Contain($"h9k task publish {TaskListCommand.ShortId(details.Id)}");

        string withoutGate = string.Join('\n', TaskNextStep.Lines(details, hasOpenDependency: false, challengeRequired: false));
        withoutGate.Should().NotContain("h9k task challenge").And.Contain("h9k task publish");

        DraftChallengeRecorded passing = Verdict(ChallengeVerdict.PublishAsWritten, ThisNode);
        details.LatestChallengeVerdict = passing;
        string passed = string.Join('\n', TaskNextStep.Lines(details, hasOpenDependency: false, challengeRequired: true));
        passed.Should().NotContain("h9k task challenge");
    }

    [Fact]
    public void A_challenge_already_asked_for_is_waited_for_not_asked_for_twice()
    {
        TaskDetails details = Draft();
        details.ChallengeRequested = true;

        string next = string.Join('\n', TaskNextStep.Lines(details, hasOpenDependency: false, challengeRequired: true));

        next.Should().Contain("the challenge you asked for").And.NotContain("h9k task challenge ");
    }

    [Theory]
    [InlineData(true, true, "h9k task challenge abcd1234")]
    [InlineData(true, false, "h9k task publish abcd1234")]
    [InlineData(false, false, "h9k task publish abcd1234")]
    public void The_shared_publish_step_is_one_sentence_everywhere(bool gateWouldRefuse, bool _, string expected)
    {
        TaskNextStep.PublishStep("abcd1234", gateWouldRefuse, challengePending: false).Should().Contain(expected);
    }

    // ---- settings ----

    [Fact]
    public void The_project_row_prints_the_value_whether_it_is_the_default_and_the_way_to_flip_it()
    {
        ProjectDetails project = new() { Name = "demo" };
        ProjectShowCommand.RequireChallengeRow(project).Should().Contain("on").And.Contain("default")
            .And.Contain("--require-challenge off").And.Contain("--skip-challenge");

        project.RequireChallengeRecorded = true;
        ProjectShowCommand.RequireChallengeRow(project).Should().Contain("explicit");

        project.RequireChallengeRecorded = false;
        ProjectShowCommand.RequireChallengeRow(project).Should().Contain("off").And.Contain("--require-challenge on");
    }

    [Theory]
    [InlineData("on", true)]
    [InlineData("OFF", false)]
    [InlineData(" on ", true)]
    public void The_setting_takes_on_or_off(string input, bool expected) =>
        ChallengeRequirement.ParseOnOff(input, "--require-challenge").Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData("yes")]
    [InlineData(null)]
    public void Anything_else_is_refused_by_name(string? input)
    {
        Action act = () => ChallengeRequirement.ParseOnOff(input, "--require-challenge");

        act.Should().Throw<Hall9k.Domain.Shared.Exceptions.DomainValidationException>().WithMessage("*--require-challenge*'on' or 'off'*");
    }

    [Fact]
    public void A_project_with_no_recorded_value_reads_on_including_one_registered_before_the_setting_existed()
    {
        new ProjectDetails().ChallengeRequirementSetting.Should().Be(new ChallengeRequirement(true, Recorded: false));
        ChallengeRequirement.Unrecorded.IsOn.Should().BeTrue();
    }

    [Fact]
    public void The_config_show_rows_say_the_floor_and_that_the_node_wide_values_do_not_move_it()
    {
        OperatingSettingsReport report = Report();

        IReadOnlyList<(string Label, string Value)> rows = OperatingSettingsRendering.Rows(report);

        rows.Single(row => row.Label == "model (challenge)").Value
            .Should().Contain(AgentModel.ChallengeDefault).And.Contain("challenge's own floor").And.Contain("default-model");
        rows.Single(row => row.Label == "effort (challenge)").Value
            .Should().Contain("high").And.Contain("challenge's own floor").And.Contain("node-wide effort");
    }

    [Fact]
    public void The_configured_challenge_values_print_with_their_origin()
    {
        OperatingSettingsReport report = Report(
            model: "claude-opus-5-5", effort: "xhigh", origin: SettingOrigin.PlatformConfigFile);

        IReadOnlyList<(string Label, string Value)> rows = OperatingSettingsRendering.Rows(report);

        rows.Single(row => row.Label == "model (challenge)").Value.Should().Contain("claude-opus-5-5").And.Contain("config");
        rows.Single(row => row.Label == "effort (challenge)").Value.Should().Contain("xhigh").And.Contain("config");
    }

    [Fact]
    public void The_model_option_help_carries_the_floor_with_its_brackets_escaped_for_markup()
    {
        string help = typeof(ConfigSetCommand.Settings).GetProperty(nameof(ConfigSetCommand.Settings.ModelChallenge))!
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
            .Cast<System.ComponentModel.DescriptionAttribute>().Single().Description;

        help.Should().Contain(AgentModel.ChallengeDefault.Replace("[", "[[").Replace("]", "]]"),
            "the literal in the option's help is pinned to the constant; markup would read a single bracket as a style");
    }

    [Fact]
    public void Config_set_sees_the_challenge_options_as_changes()
    {
        ConfigSetCommand.Settings model = new() { ModelChallenge = "claude-opus-5-5" };
        ConfigSetCommand.Validate(model);

        ConfigSetCommand.Settings effort = new() { EffortChallenge = "xhigh" };
        ConfigSetCommand.Validate(effort);
        effort.RoleEfforts.Single(option => option.Flag == "--effort-challenge").Input.Should().Be("xhigh");
    }

    [Fact]
    public async Task Config_set_writes_the_challenge_role_and_config_show_prints_it_with_its_origin()
    {
        using ScopedTestHome home = new();
        CommandApp app = new();
        app.Configure(config =>
        {
            CliCommandTree.Configure(config, _ => null);
            config.PropagateExceptions();
        });

        int exit = await app.RunAsync(
            ["config", "set", "--model-challenge", "claude-opus-5-5", "--effort-challenge", "xhigh"], CancellationToken.None);

        exit.Should().Be(0);
        OperatingSettings written = await PlatformConfigFile.ReadOperatingSettingsAsync(CancellationToken.None);
        written.ModelByRole.Challenge.Should().Be("claude-opus-5-5");
        written.EffortByRole.Challenge.Should().Be("xhigh");

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);
        IReadOnlyList<(string Label, string Value)> rows = OperatingSettingsRendering.Rows(report);
        rows.Single(row => row.Label == "model (challenge)").Value.Should().Contain("claude-opus-5-5").And.Contain("config");
        rows.Single(row => row.Label == "effort (challenge)").Value.Should().Contain("xhigh").And.Contain("config");

        (await app.RunAsync(["config", "set", "--model-challenge", "default"], CancellationToken.None)).Should().Be(0);
        (await PlatformConfigFile.ReadOperatingSettingsAsync(CancellationToken.None)).ModelByRole.Challenge
            .Should().BeNullOrEmpty("default clears the override and leaves the role on its floor");
    }

    [Fact]
    public void A_dispatched_session_is_refused_the_verb()
    {
        DispatchedSessionCommandClassification.BySettingsType[typeof(TaskChallengeCommand.Settings)]
            .Should().Be((DispatchedSessionAccess.Refused, "task challenge"));
    }

    private static OperatingSettingsReport Report(
        string? model = null, string? effort = null, SettingOrigin origin = SettingOrigin.Default) => new(
        new ResolvedSetting<int>(OperatingSettings.DefaultMaxConcurrentAgentSessions, SettingOrigin.Default, null),
        false,
        new ResolvedSetting<int>(OperatingSettings.DefaultMaxConcurrentTaskRuns, SettingOrigin.Default, null),
        false,
        false,
        new ResolvedSetting<int>(OperatingSettings.DefaultSessionCapPerRun, SettingOrigin.Default, null),
        new ResolvedSetting<string>(AgentModel.PlatformFallback, SettingOrigin.Default, null),
        [new RoleModelSetting(nameof(RoleModelSettings.Challenge), new ResolvedSetting<string?>(model, origin, origin == SettingOrigin.Default ? null : "cfg"))],
        null,
        [],
        new ResolvedSetting<int>(OperatingSettings.DefaultMaxComplianceReviewCycles, SettingOrigin.Default, null),
        new ResolvedSetting<int>(OperatingSettings.DefaultMaxAdversarialReviewCycles, SettingOrigin.Default, null),
        new ResolvedSetting<int>(OperatingSettings.DefaultMaxFinalFullPassRounds, SettingOrigin.Default, null),
        new ResolvedSetting<int>(OperatingSettings.DefaultLifetimeReviewCycleBudget, SettingOrigin.Default, null),
        new ResolvedSetting<long?>(null, SettingOrigin.Default, null),
        new ResolvedSetting<string>(OperatingSettings.DefaultSpendPeriod, SettingOrigin.Default, null),
        new ResolvedSetting<string>(Hall9k.Domain.Features.Run.ReviewStageComposition.FullPipeline.Value, SettingOrigin.Default, null),
        new ResolvedSetting<string?>(null, SettingOrigin.Default, null),
        new ResolvedSetting<int>(OperatingSettings.DefaultAutoPrReviewMintHoldSeconds, SettingOrigin.Default, null),
        [new RoleEffortSetting(nameof(RoleEffortSettings.Challenge), new ResolvedSetting<string?>(effort, origin, origin == SettingOrigin.Default ? null : "cfg"))],
        new ResolvedSetting<bool>(OperatingSettings.DefaultSessionContainment, SettingOrigin.Default, null));
}

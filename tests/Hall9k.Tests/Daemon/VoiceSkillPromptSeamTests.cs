using System.Text;
using FluentAssertions;
using Hall9k.Connectors.Prompts;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// Every prompt seam where a session composes text a human will read as the owner's names the
/// owner's own voice skill when they have named one, and renders exactly as it did before the
/// preference existed when they have not.
/// <para>
/// The golden fixtures already hold the second half of that for the no-skill case; what this class
/// adds is the other direction, seam by seam, plus the rule that makes the whole design safe: the
/// skill is referenced BY NAME and its text is never inlined, not into a template and not into an
/// assembled prompt.
/// </para>
/// </summary>
[Collection("Hall9kHome")]
public sealed class VoiceSkillPromptSeamTests : IDisposable
{
    private static readonly VoiceSkillName MyVoice = VoiceSkillName.Parse("my-voice");

    private const string VoiceLeadIn = "The owner's own voice.";

    private readonly string _worktreePath =
        Path.Combine(Path.GetTempPath(), $"hall9k-voice-seam-{Guid.NewGuid():N}");

    public VoiceSkillPromptSeamTests() => Directory.CreateDirectory(_worktreePath);

    public void Dispose() => Directory.Delete(_worktreePath, recursive: true);

    /// <summary>
    /// work-prompt-builder's pull-request-summary-step, inside the Whose voice bullet: the pull
    /// request body the platform posts verbatim under the owner's login.
    /// </summary>
    [Fact]
    public void The_pull_request_summary_step_names_the_voice_skill_and_its_code_review_context()
    {
        string withSkill = CheckpointCommitRules(MyVoice);
        string without = CheckpointCommitRules(voiceSkill: null);

        VoiceLines(withSkill).Should().HaveCount(1);
        withSkill.Should().Contain("`my-voice`")
            .And.Contain($"`{WorkPromptBuilder.CodeReviewVoiceContext}`");
        without.Should().NotContain(VoiceLeadIn);
    }

    /// <summary>
    /// agent-prompt-builder's commit-style, on both arms: a commit message is authored history read
    /// under the owner's login exactly as a pull request body is.
    /// </summary>
    [Theory]
    [InlineData("Narrative")]
    [InlineData("Append")]
    public void The_commit_style_seam_names_the_voice_skill_on_either_style(string style)
    {
        CommitStyle commitStyle = CommitStyle.FromInput(style);
        string withSkill = AgentPromptBuilder.BuildFixChecks(
            SomeTask(), SomeProject(), "task/1-slug", "https://github.com/x/y/pull/7", commitStyle,
            voiceSkill: MyVoice);
        string without = AgentPromptBuilder.BuildFixChecks(
            SomeTask(), SomeProject(), "task/1-slug", "https://github.com/x/y/pull/7", commitStyle);

        VoiceLines(withSkill).Should().HaveCount(1, "one line, at the one seam this prompt has");
        withSkill.Should().Contain("`my-voice`")
            .And.Contain($"`{WorkPromptBuilder.CodeReviewVoiceContext}`");
        without.Should().NotContain(VoiceLeadIn);
    }

    /// <summary>
    /// agent-prompt-builder's thread-handling (replies this session posts, code-review context) and
    /// thread-dispute (a draft a human reads and decides on, explainer context) both render inside
    /// the review-feedback follow-up, alongside its commit-style seam.
    /// </summary>
    [Fact]
    public void The_follow_up_names_the_voice_skill_at_each_of_its_three_seams()
    {
        string withSkill = AgentPromptBuilder.BuildFollowUp(
            SomeTask(), SomeProject(), "task/1-slug", "https://github.com/x/y/pull/7", CommitStyle.Narrative,
            voiceSkill: MyVoice);
        string without = AgentPromptBuilder.BuildFollowUp(
            SomeTask(), SomeProject(), "task/1-slug", "https://github.com/x/y/pull/7", CommitStyle.Narrative);

        IReadOnlyList<string> lines = VoiceLines(withSkill);
        lines.Should().HaveCount(3, "thread-handling, thread-dispute, and commit-style");
        lines.Where(line => line.Contains(WorkPromptBuilder.CodeReviewVoiceContext, StringComparison.Ordinal))
            .Should().HaveCount(2, "the in-thread replies and the commit messages both post");
        lines.Where(line => line.Contains(WorkPromptBuilder.ExplainerVoiceContext, StringComparison.Ordinal))
            .Should().HaveCount(1, "a parked disagreement is drafted for a human, not posted");
        lines.Should().OnlyContain(line => line.Contains("`my-voice`", StringComparison.Ordinal));
        without.Should().NotContain(VoiceLeadIn);
    }

    /// <summary>agent-prompt-builder's review-requested-changes, plus its own commit-style seam.</summary>
    [Fact]
    public void The_changes_requested_lap_names_the_voice_skill_for_the_replies_it_posts()
    {
        string withSkill = AgentPromptBuilder.BuildReviewRequestedChanges(
            ChangesRequestedTask(), SomeProject(), "task/1-slug", "https://github.com/x/y/pull/7",
            CommitStyle.Narrative, voiceSkill: MyVoice);
        string without = AgentPromptBuilder.BuildReviewRequestedChanges(
            ChangesRequestedTask(), SomeProject(), "task/1-slug", "https://github.com/x/y/pull/7",
            CommitStyle.Narrative);

        IReadOnlyList<string> lines = VoiceLines(withSkill);
        lines.Should().HaveCount(2, "the handling rules and the commit style");
        lines.Should().OnlyContain(
            line => line.Contains(WorkPromptBuilder.CodeReviewVoiceContext, StringComparison.Ordinal));
        without.Should().NotContain(VoiceLeadIn);
    }

    /// <summary>
    /// mention-followup-prompt-builder's drafted reply: read-only, and everything it produces is
    /// handed to the owner to read and decide on, which is the explainer's job.
    /// </summary>
    [Fact]
    public void The_mention_follow_up_names_the_voice_skill_for_the_reply_it_drafts()
    {
        string withSkill = MentionFollowUpPromptBuilder.Build(
            "acme/web", 7, _worktreePath, "main", SomeMention(), priorReport: null, voiceSkill: MyVoice);
        string without = MentionFollowUpPromptBuilder.Build(
            "acme/web", 7, _worktreePath, "main", SomeMention(), priorReport: null);

        IReadOnlyList<string> lines = VoiceLines(withSkill);
        lines.Should().HaveCount(1);
        lines[0].Should().Contain("`my-voice`")
            .And.Contain($"`{WorkPromptBuilder.ExplainerVoiceContext}`");
        without.Should().NotContain(VoiceLeadIn);
    }

    /// <summary>
    /// The same drafted reply reaches a session two ways — as this addendum when the pr-review task
    /// was MINTED from a mention, and as its own prompt when a mention lands on a pull request
    /// already reviewed — so both name the skill. One covered and the other not would answer a
    /// mint-time mention in the platform's voice and a follow-up mention in the owner's.
    /// </summary>
    [Fact]
    public void The_mint_time_mention_addendum_names_the_voice_skill_on_the_same_terms()
    {
        string withSkill = MintAddendum(MyVoice);
        string without = MintAddendum(voiceSkill: null);

        IReadOnlyList<string> lines = VoiceLines(withSkill);
        lines.Should().HaveCount(1);
        lines[0].Should().Contain("`my-voice`")
            .And.Contain($"`{WorkPromptBuilder.ExplainerVoiceContext}`");
        without.Should().NotContain(VoiceLeadIn);
    }

    /// <summary>
    /// The settling-gate repair lap commits fixes like any other, so its commit-style seam names
    /// the skill too: the blast-radius half of "the commit-style seam", which four prompts share.
    /// </summary>
    [Fact]
    public void The_settling_gate_repair_lap_names_the_voice_skill_at_its_commit_style_seam()
    {
        string withSkill = SettlingGateRepair(MyVoice);
        string without = SettlingGateRepair(voiceSkill: null);

        VoiceLines(withSkill).Should().HaveCount(1);
        without.Should().NotContain(VoiceLeadIn);
    }

    /// <summary>
    /// The pre-PR fix lap may refresh the pull request's own body with a fresh PR SUMMARY: block —
    /// the same artifact, read by the same parser, that the build session's pull-request summary
    /// step produces — so it names the skill on the same terms. Beyond the seams the decision
    /// enumerates, and covered because a refreshed body in a different voice than the one it
    /// replaces is the inconsistency this feature exists to remove.
    /// </summary>
    [Fact]
    public void The_review_fix_laps_summary_refresh_names_the_voice_skill_too()
    {
        string withSkill = AgentPromptBuilder.BuildReviewFix(
            SomeTask(), SomeProject(), "task/1-slug", "FINDING: at=src/Limiter.cs:42", 1,
            voiceSkill: MyVoice);
        string without = AgentPromptBuilder.BuildReviewFix(
            SomeTask(), SomeProject(), "task/1-slug", "FINDING: at=src/Limiter.cs:42", 1);

        IReadOnlyList<string> lines = VoiceLines(withSkill);
        lines.Should().HaveCount(1);
        lines[0].Should().Contain($"`{WorkPromptBuilder.CodeReviewVoiceContext}`");
        without.Should().NotContain(VoiceLeadIn);
    }

    /// <summary>
    /// The rule the whole design rests on. The voice text is personal and per account, so a seam
    /// gets a POINTER at a skill, never a copy of it: one line, short enough that no voice document
    /// could be hiding in it, with structure authority explicitly left where it was.
    /// </summary>
    [Fact]
    public void The_rendered_line_is_a_pointer_at_the_skill_and_never_a_copy_of_it()
    {
        string prompt = AgentPromptBuilder.BuildFollowUp(
            SomeTask(), SomeProject(), "task/1-slug", "https://github.com/x/y/pull/7", CommitStyle.Narrative,
            voiceSkill: MyVoice);

        IReadOnlyList<string> lines = VoiceLines(prompt);
        lines.Should().NotBeEmpty();
        foreach (string line in lines)
        {
            line.Length.Should().BeLessThan(500,
                "the seam points at the owner's skill by name; a copy of their voice text could not fit");
            line.Should().Contain("load `my-voice`", "the skill is loaded by name")
                .And.Contain("PR-description rule", "the repository still decides the structure")
                .And.Contain("decides only the prose");
        }
    }

    /// <summary>
    /// The same rule one level up, where an author would be tempted to break it: no checked-in
    /// template carries the voice skill's own layout literally. Both context paths reach a prompt
    /// only as parameters substituted by the builder, so an operator editing template prose can
    /// never be editing somebody's voice.
    /// </summary>
    [Fact]
    public void No_checked_in_template_names_a_voice_context_literally()
    {
        string templates = Path.Combine(RepositoryRoot(), ".claude", "templates");
        Directory.Exists(templates).Should().BeTrue();

        List<string> offenders = [];
        foreach (string file in Directory.EnumerateFiles(templates, "*.md", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            if (text.Contains(WorkPromptBuilder.CodeReviewVoiceContext, StringComparison.Ordinal)
                || text.Contains(WorkPromptBuilder.ExplainerVoiceContext, StringComparison.Ordinal))
            {
                offenders.Add(Path.GetRelativePath(templates, file));
            }
        }

        offenders.Should().BeEmpty(
            "a voice skill's own internals belong to the owner, so the builder substitutes the context "
            + "path as a parameter rather than a template author typing it");
    }

    /// <summary>
    /// The one template that renders the line carries two placeholders and nothing about how
    /// anybody writes: the mechanical form of "named, never inlined".
    /// </summary>
    [Fact]
    public void The_owner_voice_template_is_one_parameterised_line()
    {
        string line = PromptTemplates.Load($"{WorkPromptBuilder.TemplateDirectory}/owner-voice.md", "line");

        line.Should().NotContain("\n", "one line, appended at a seam inside another section");
        line.Should().Contain("{{VoiceSkill}}").And.Contain("{{VoiceContext}}").And.Contain("{{Indent}}");
    }

    private string CheckpointCommitRules(VoiceSkillName? voiceSkill)
    {
        StringBuilder prompt = new();
        WorkPromptBuilder.AppendCheckpointCommitRules(
            prompt, SomeProject(), _worktreePath, voiceSkill: voiceSkill);
        return prompt.ToString();
    }

    private string MintAddendum(VoiceSkillName? voiceSkill) =>
        MentionFollowUpPromptBuilder.BuildMintAddendum(
            "teammate", new DateTimeOffset(2026, 9, 14, 12, 30, 0, TimeSpan.Zero),
            "Is the limiter reset deliberate?", "https://github.com/acme/web/pull/7#issuecomment-1",
            _worktreePath, voiceSkill);

    private static string SettlingGateRepair(VoiceSkillName? voiceSkill) =>
        AgentPromptBuilder.BuildSettlingGateRepair(
            SomeTask(), SomeProject(), "task/1-slug", CommitStyle.Narrative,
            "https://github.com/x/y/pull/7", "main", "aaaaaaaaaa1111111111", "bbbbbbbbbb2222222222",
            rebaseWasRecovered: false, gateOutput: "dotnet test failed.", voiceSkill: voiceSkill);

    private static IReadOnlyList<string> VoiceLines(string prompt) =>
        [.. prompt.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(line => line.Contains(VoiceLeadIn, StringComparison.Ordinal))];

    private static TaskDetails SomeTask() => new()
    {
        Objective = "Add rate limiting to auth endpoints",
        AcceptanceCriteria = ["Requests over the limit get 429"],
    };

    private static TaskDetails ChangesRequestedTask()
    {
        TaskDetails task = SomeTask();
        task.ChangesRequestedReviews =
        [
            new ChangesRequestedReview(
                "teammate", "https://github.com/x/y/pull/7#pullrequestreview-42",
                new DateTimeOffset(2026, 9, 14, 12, 15, 0, TimeSpan.Zero),
                [new ChangesRequestedFinding("This limiter never resets.", "src/Limiter.cs:42", "PRRT_abc")]),
        ];
        return task;
    }

    private static ProjectDetails SomeProject() => new()
    {
        Name = "hall9k",
        BaseBranch = "main",
    };

    private static PullRequestMentionComment SomeMention() => new(
        "IC_abc", "teammate", "Is the limiter reset deliberate?",
        "https://github.com/acme/web/pull/7#issuecomment-1",
        new DateTimeOffset(2026, 9, 14, 12, 30, 0, TimeSpan.Zero));

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Hall9k.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            "Could not find this checkout's own Hall9k.slnx above " + AppContext.BaseDirectory);
    }
}

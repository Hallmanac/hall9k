using FluentAssertions;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// What the pre-PR Adversarial lens is handed when the project's security-review setting is on
/// (idea b2a6f46c): the six security areas the Security persona hunts, read from the very same
/// <c>lenses.md</c>, and the instruction to tag what falls in them <c>kind=security</c>. The
/// byte-for-byte goldens for the setting-off case and for every other review prompt live in
/// <see cref="AgentPromptBuilderGoldenTests"/>; this file is the content of the section itself.
/// </summary>
// Redirects HALL9K_HOME to an empty temp home, the same as the golden suites and for the identical
// reason: PromptTemplates falls back to a HALL9K_HOME-derived canonical directory whenever this
// checkout's own .claude/templates does not carry a file it asks for.
public sealed class AdversarialSecurityAreasPromptTests : IDisposable
{
    private const string SectionHeading = "## Security areas (read for these as well)";

    private static readonly string[] AreaFragments = ["injection", "secrets", "auth", "unsafe-io", "dependencies", "ci"];

    private readonly ScopedTestHome _scopedHome = new();

    public void Dispose()
    {
        _scopedHome.Dispose();
    }

    [Theory]
    [InlineData(nameof(ReviewMode.Discovery))]
    [InlineData(nameof(ReviewMode.FinalFullPass))]
    public void With_the_setting_on_every_adversarial_pass_names_the_six_areas_as_lenses_md_words_them(string modeName)
    {
        ReviewMode mode = modeName == nameof(ReviewMode.Discovery) ? ReviewMode.Discovery : ReviewMode.FinalFullPass;

        string prompt = AdversarialPrompt(mode, readSecurityAreas: true);

        prompt.Should().Contain(SectionHeading);
        foreach (string fragment in AreaFragments)
        {
            string areaText = PromptTemplates.Load(
                $"{SecurityReviewPromptBuilder.TemplateDirectory}/lenses.md", fragment);
            areaText.Should().NotBeNullOrWhiteSpace();
            prompt.Should().Contain(areaText.Trim(), $"the {fragment} area is read from lenses.md at build time");
        }
    }

    [Theory]
    [InlineData(nameof(ReviewMode.Discovery))]
    [InlineData(nameof(ReviewMode.FinalFullPass))]
    public void With_the_setting_on_the_prompt_tells_the_reviewer_to_tag_what_it_finds_there_as_security(string modeName)
    {
        ReviewMode mode = modeName == nameof(ReviewMode.Discovery) ? ReviewMode.Discovery : ReviewMode.FinalFullPass;

        string prompt = AdversarialPrompt(mode, readSecurityAreas: true);

        prompt.Should().Contain($"{ReviewResultParser.KindTagKey}={ReviewFindingKind.Security.Value}");
    }

    /// <summary>
    /// The section's own framing is the Adversarial lens's, not the persona's: the persona's intro
    /// calls a change touching none of the areas a complete review, which would tell an Adversarial
    /// pass it may stop.
    /// </summary>
    [Fact]
    public void The_sections_framing_is_the_adversarial_lenss_own_and_not_the_personas()
    {
        string prompt = AdversarialPrompt(ReviewMode.Discovery, readSecurityAreas: true);

        prompt.Should().NotContain("that is a legitimate, complete review");
        prompt.Should().NotContain("## What you are looking for");
        prompt.Should().Contain("does not end your review");
    }

    [Fact]
    public void With_the_setting_on_the_existing_warm_up_stays_as_it_was()
    {
        string off = AdversarialPrompt(ReviewMode.Discovery, readSecurityAreas: false);
        string on = AdversarialPrompt(ReviewMode.Discovery, readSecurityAreas: true);

        on.Should().Contain("## Where defects hide (a warm-up, NOT a checklist)")
            .And.Contain("**Injection and trust boundaries.**");
        off.Should().NotContain(SectionHeading)
            .And.NotContain($"{ReviewResultParser.KindTagKey}={ReviewFindingKind.Security.Value}");
    }

    /// <summary>
    /// A pr-review task's own lens reaches the same method with the foreign-pull-request flag, and
    /// it has the Security persona for this reading, so the section never rides in whatever the
    /// setting says.
    /// </summary>
    [Fact]
    public void A_pr_review_lens_carries_no_security_section_with_the_setting_on()
    {
        AgentPromptBuilder.ReviewMechanicsOverride foreign = new("develop", DiffIsForeignPullRequest: true);

        string on = AgentPromptBuilder.BuildReview(
            SomePrReviewTask(), SomeProject(), "detached", cycle: 1, ReviewLens.Adversarial,
            mechanicsOverride: foreign, readSecurityAreas: true);
        string off = AgentPromptBuilder.BuildReview(
            SomePrReviewTask(), SomeProject(), "detached", cycle: 1, ReviewLens.Adversarial,
            mechanicsOverride: foreign, readSecurityAreas: false);

        on.Should().Be(off);
        on.Should().NotContain(SectionHeading)
            .And.NotContain($"{ReviewResultParser.KindTagKey}={ReviewFindingKind.Security.Value}");
    }

    [Fact]
    public void The_public_pr_review_lens_entry_point_carries_no_security_section()
    {
        string prompt = AgentPromptBuilder.BuildPrReviewLens(
            SomePrReviewTask(), SomeProject(), "detached", ReviewLens.Adversarial, "develop");

        prompt.Should().NotContain(SectionHeading)
            .And.NotContain($"{ReviewResultParser.KindTagKey}={ReviewFindingKind.Security.Value}");
    }

    [Fact]
    public void The_conformance_lens_never_carries_the_section()
    {
        string on = AgentPromptBuilder.BuildReview(
            SomeTask(), SomeProject(), "task/1-slug", cycle: 1, ReviewLens.Conformance, readSecurityAreas: true);
        string off = AgentPromptBuilder.BuildReview(
            SomeTask(), SomeProject(), "task/1-slug", cycle: 1, ReviewLens.Conformance, readSecurityAreas: false);

        on.Should().Be(off);
    }

    private static string AdversarialPrompt(ReviewMode mode, bool readSecurityAreas) =>
        AgentPromptBuilder.BuildReview(
            SomeTask(), SomeProject(), "task/1-slug", cycle: 1, ReviewLens.Adversarial, mode,
            readSecurityAreas: readSecurityAreas);

    private static TaskDetails SomeTask() => new()
    {
        Objective = "Add rate limiting to auth endpoints",
        AcceptanceCriteria = ["Requests over the limit get 429"],
    };

    private static TaskDetails SomePrReviewTask() => new()
    {
        Type = TaskType.PrReview,
        Objective = "Review pull request #42",
        AcceptanceCriteria = ["Every finding is verified before it is reported"],
    };

    private static ProjectDetails SomeProject() => new() { Name = "hall9k", BaseBranch = "main" };
}

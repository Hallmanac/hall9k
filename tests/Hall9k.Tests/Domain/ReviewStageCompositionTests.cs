using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Task: the review pipeline's stage composition becomes configuration recorded per run — the
/// value object's own parsing, lens-set, and guarantee-loss classification, plus the shared
/// resolver's strict task &gt; project &gt; node &gt; compiled default hierarchy (the same shape
/// <c>ReviewCapResolverTests</c> already proves for the review-cycle caps) and the
/// acknowledgment-required-to-degrade validation.
/// </summary>
public sealed class ReviewStageCompositionTests
{
    [Theory]
    [InlineData("full-pipeline")]
    [InlineData("FULL-PIPELINE")]
    [InlineData("full")]
    [InlineData("fullpipeline")]
    public void Parse_accepts_every_alias_of_full_pipeline(string input) =>
        ReviewStageComposition.Parse(input).Should().Be(ReviewStageComposition.FullPipeline);

    [Fact]
    public void Parse_rejects_an_unrecognized_word_and_quotes_the_recognized_values()
    {
        Action act = () => ReviewStageComposition.Parse("bogus");

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*'bogus'*")
            .WithMessage("*full-pipeline*")
            .WithMessage("*adversarial-only*")
            .WithMessage("*conformance-only*")
            .WithMessage("*skip-final-pass*")
            .WithMessage("*none*");
    }

    /// <summary>
    /// 'default' is not a canonical alias here (unlike the model chain): it is the clearing word
    /// a project or task level's own CLI command intercepts before ever reaching Parse, so
    /// passing it straight to Parse is refused exactly like any other unrecognized word.
    /// </summary>
    [Fact]
    public void Parse_refuses_the_word_default_since_clearing_is_handled_a_level_up()
    {
        Action act = () => ReviewStageComposition.Parse("default");

        act.Should().Throw<DomainValidationException>().WithMessage("*'default'*");
    }

    [Fact]
    public void FromInput_reads_an_unrecognized_value_as_unknown_rather_than_throwing() =>
        ReviewStageComposition.FromInput("bogus").Should().Be(ReviewStageComposition.Unknown);

    [Theory]
    [InlineData("FullPipeline")]
    [InlineData("SkipFinalPass")]
    public void OpeningLenses_opens_both_tracks_for_full_pipeline_and_skip_final_pass(string composition) =>
        ReviewStageComposition.FromInput(composition).OpeningLenses().Should().Equal(ReviewLens.CycleLenses);

    [Fact]
    public void OpeningLenses_opens_only_the_adversarial_track_for_adversarial_only() =>
        ReviewStageComposition.AdversarialOnly.OpeningLenses().Should().Equal([ReviewLens.Adversarial]);

    [Fact]
    public void OpeningLenses_opens_only_the_conformance_track_for_conformance_only() =>
        ReviewStageComposition.ConformanceOnly.OpeningLenses().Should().Equal([ReviewLens.Conformance]);

    [Fact]
    public void OpeningLenses_opens_no_track_at_all_for_none() =>
        ReviewStageComposition.None.OpeningLenses().Should().BeEmpty();

    /// <summary>A stream written before this field existed carries no recorded value; it ran the full pipeline.</summary>
    [Fact]
    public void OpeningLenses_treats_unknown_as_full_pipeline() =>
        ReviewStageComposition.Unknown.OpeningLenses().Should().Equal(ReviewLens.CycleLenses);

    [Theory]
    [InlineData("SkipFinalPass", true)]
    [InlineData("None", true)]
    [InlineData("FullPipeline", false)]
    [InlineData("AdversarialOnly", false)]
    [InlineData("ConformanceOnly", false)]
    public void WaivesFinalFullPassGuarantee_is_true_only_for_skip_final_pass_and_none(string composition, bool expected) =>
        ReviewStageComposition.FromInput(composition).WaivesFinalFullPassGuarantee.Should().Be(expected);

    [Theory]
    [InlineData("AdversarialOnly", true)]
    [InlineData("ConformanceOnly", true)]
    [InlineData("None", true)]
    [InlineData("FullPipeline", false)]
    [InlineData("SkipFinalPass", false)]
    public void DropsALens_is_true_for_every_composition_that_removes_a_track(string composition, bool expected) =>
        ReviewStageComposition.FromInput(composition).DropsALens.Should().Be(expected);

    public sealed class ResolverTests
    {
        [Fact]
        public void Nothing_set_anywhere_resolves_to_full_pipeline_the_unchanged_defaults_case() =>
            ReviewStageCompositionResolver.Resolve(taskValue: null, projectValue: null, nodeValue: null)
                .Should().Be(ReviewStageComposition.FullPipeline);

        [Fact]
        public void A_node_value_resolves_when_nothing_above_it_is_set() =>
            ReviewStageCompositionResolver.Resolve(null, null, "skip-final-pass")
                .Should().Be(ReviewStageComposition.SkipFinalPass);

        [Fact]
        public void A_project_value_outranks_the_node_even_when_the_node_also_set_one() =>
            ReviewStageCompositionResolver.Resolve(null, "conformance-only", "skip-final-pass")
                .Should().Be(ReviewStageComposition.ConformanceOnly);

        [Fact]
        public void A_task_value_outranks_both_the_project_and_the_node() =>
            ReviewStageCompositionResolver.Resolve("adversarial-only", "conformance-only", "skip-final-pass")
                .Should().Be(ReviewStageComposition.AdversarialOnly);

        [Fact]
        public void An_unrecognized_value_at_a_level_is_skipped_rather_than_crashing_the_resolver() =>
            ReviewStageCompositionResolver.Resolve(null, "not-a-real-composition", "none")
                .Should().Be(ReviewStageComposition.None, "an unrecognized project value reads as unset, falling through to the node");
    }

    public sealed class ValidationTests
    {
        [Fact]
        public void RefuseWithoutAcknowledgment_lets_full_pipeline_through_unacknowledged() =>
            FluentActions.Invoking(() => ReviewStageCompositionValidation.RefuseWithoutAcknowledgment(
                ReviewStageComposition.FullPipeline, acknowledged: false, "--review-stage-composition"))
                .Should().NotThrow();

        [Theory]
        [InlineData("SkipFinalPass")]
        [InlineData("None")]
        [InlineData("AdversarialOnly")]
        [InlineData("ConformanceOnly")]
        public void RefuseWithoutAcknowledgment_refuses_every_guarantee_reducing_composition_unacknowledged(string composition)
        {
            Action act = () => ReviewStageCompositionValidation.RefuseWithoutAcknowledgment(
                ReviewStageComposition.FromInput(composition), acknowledged: false, "--review-stage-composition");

            act.Should().Throw<DomainValidationException>().WithMessage("*--accept-reduced-review*");
        }

        [Fact]
        public void RefuseWithoutAcknowledgment_names_decision_92_for_skip_final_pass()
        {
            Action act = () => ReviewStageCompositionValidation.RefuseWithoutAcknowledgment(
                ReviewStageComposition.SkipFinalPass, acknowledged: false, "--review-stage-composition");

            act.Should().Throw<DomainValidationException>().WithMessage("*Decisions Log #92*");
        }

        [Fact]
        public void RefuseWithoutAcknowledgment_names_decision_92_for_none()
        {
            Action act = () => ReviewStageCompositionValidation.RefuseWithoutAcknowledgment(
                ReviewStageComposition.None, acknowledged: false, "--review-stage-composition");

            act.Should().Throw<DomainValidationException>().WithMessage("*Decisions Log #92*");
        }

        [Theory]
        [InlineData("AdversarialOnly")]
        [InlineData("ConformanceOnly")]
        public void RefuseWithoutAcknowledgment_lets_a_guarantee_reducing_composition_through_once_acknowledged(string composition) =>
            FluentActions.Invoking(() => ReviewStageCompositionValidation.RefuseWithoutAcknowledgment(
                ReviewStageComposition.FromInput(composition), acknowledged: true, "--review-stage-composition"))
                .Should().NotThrow();

        [Fact]
        public void VetInput_treats_blank_and_default_as_no_override() =>
            ReviewStageCompositionValidation.VetInput(" Default ", acknowledged: false, "--review-stage-composition")
                .Should().BeNull();

        [Fact]
        public void VetInput_canonicalizes_a_recognized_alias() =>
            ReviewStageCompositionValidation.VetInput("adversarial-only", acknowledged: true, "--review-stage-composition")
                .Should().Be("AdversarialOnly");

        [Fact]
        public void AcknowledgmentActuallyNeeded_is_false_for_full_pipeline_even_when_acknowledged() =>
            ReviewStageCompositionValidation.AcknowledgmentActuallyNeeded("FullPipeline", acknowledged: true)
                .Should().BeFalse("full-pipeline never trades away a guarantee, so there is nothing to have accepted");

        [Fact]
        public void AcknowledgmentActuallyNeeded_is_false_when_nothing_was_recorded() =>
            ReviewStageCompositionValidation.AcknowledgmentActuallyNeeded(null, acknowledged: true).Should().BeFalse();

        [Fact]
        public void AcknowledgmentActuallyNeeded_is_true_for_none_when_acknowledged() =>
            ReviewStageCompositionValidation.AcknowledgmentActuallyNeeded("None", acknowledged: true).Should().BeTrue();

        [Fact]
        public void AcknowledgmentActuallyNeeded_is_false_for_none_when_not_acknowledged() =>
            ReviewStageCompositionValidation.AcknowledgmentActuallyNeeded("None", acknowledged: false).Should().BeFalse();
    }
}

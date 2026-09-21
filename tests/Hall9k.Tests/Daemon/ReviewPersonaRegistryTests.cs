using System.Text.Json;
using FluentAssertions;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// Review selection (idea b9b09779, piece 1): the assignee's declared personas resolved into the
/// sessions one pr-review run dispatches, and into what the findings report says about the
/// personas it could not run.
/// </summary>
public sealed class ReviewPersonaRegistryTests
{
    [Fact]
    public void No_persona_declared_plans_the_engineer_review_unchanged()
    {
        ReviewPersonaPlan plan = ReviewPersonaRegistry.Plan(null);

        plan.Requested.Should().Equal(ReviewPersona.Engineer);
        plan.Ran.Should().Equal(ReviewPersona.Engineer);
        plan.Skipped.Should().BeEmpty();
        plan.FellBackToEngineer.Should().BeFalse(
            "the engineer's review is what they asked for by declaring nothing, not a substitute for it");
        // Today's pull-request review is two lenses over the same diff, and personas leave it alone.
        plan.Sessions.Select(session => session.Slug).Should().Equal(
            ReviewLens.Adversarial.Slug, ReviewLens.Conformance.Slug);
        plan.Sessions[0].RoleName.Should().Be(SessionRoleName.ReviewAdversarial(1),
            "the primary session's own name is what the interaction rules and the phase line key on");
    }

    [Fact]
    public void Declaring_the_engineer_explicitly_plans_exactly_what_declaring_nothing_does()
    {
        ReviewPersonaPlan plan = ReviewPersonaRegistry.Plan([ReviewPersona.Engineer]);

        plan.Ran.Should().Equal(ReviewPersona.Engineer);
        plan.Skipped.Should().BeEmpty();
        plan.FellBackToEngineer.Should().BeFalse();
        plan.Sessions.Should().HaveCount(2);
    }

    /// <summary>
    /// One session per persona the registry can run, and every persona it cannot named as
    /// skipped — never dropped, and never quietly given a review it was not asked for.
    /// </summary>
    [Fact]
    public void An_assignee_holding_several_personas_gets_every_one_named_run_or_skipped()
    {
        ReviewPersonaPlan plan = ReviewPersonaRegistry.Plan(
            [ReviewPersona.Designer, ReviewPersona.Engineer, ReviewPersona.Qa]);

        plan.Requested.Should().Equal(ReviewPersona.Engineer, ReviewPersona.Qa, ReviewPersona.Designer);
        plan.Ran.Should().Equal(ReviewPersona.Engineer, ReviewPersona.Qa, ReviewPersona.Designer);
        plan.Skipped.Should().BeEmpty("every persona in the fixed set has a review of its own now");
        plan.FellBackToEngineer.Should().BeFalse("the engineer's review really did run, and it was declared");
        // Persona order, then session order within each: the engineer's two lenses, then QA's
        // one, then the designer's. The primary session is still the engineer's adversarial
        // lens, which is what keeps the artifact layout of a mixed-persona run identical to an
        // engineer-only one.
        plan.Sessions.Select(session => session.Slug).Should().Equal(
            ReviewLens.Adversarial.Slug, ReviewLens.Conformance.Slug, ReviewPersonaRegistry.QaSlug,
            DesignReviewSection.SessionSlug);
    }

    /// <summary>
    /// The QA persona's own review (idea b9b09779, piece 2): one session, its own role name and
    /// slug, and a declaration of qa alone no longer falls back to anything — it is a review the
    /// platform can now actually run.
    /// </summary>
    [Fact]
    public void Declaring_qa_alone_runs_the_qa_review_and_nothing_else()
    {
        ReviewPersonaPlan plan = ReviewPersonaRegistry.Plan([ReviewPersona.Qa]);

        plan.Requested.Should().Equal(ReviewPersona.Qa);
        plan.Ran.Should().Equal(ReviewPersona.Qa);
        plan.Skipped.Should().BeEmpty();
        plan.FellBackToEngineer.Should().BeFalse(
            "qa is registered now, so nothing stands in for it");
        plan.Sessions.Should().ContainSingle();
        plan.Sessions[0].Slug.Should().Be(ReviewPersonaRegistry.QaSlug);
        plan.Sessions[0].Persona.Should().Be(ReviewPersona.Qa);
        plan.Sessions[0].RoleName.Should().Be(SessionRoleName.ReviewQa(1));
        plan.Sessions[0].SeesTaskContext.Should().BeTrue(
            "the QA review grades the change against the acceptance criteria on whatever it is linked to");
    }

    [Fact]
    public void The_qa_persona_maps_to_the_qa_review_prompt()
    {
        ReviewPersonaSession session = ReviewPersonaRegistry.For(ReviewPersona.Qa).Sessions.Single();

        string prompt = session.BuildPrompt(new ReviewPersonaPromptRequest(
            QaReviewPromptTests.SomeTask(), QaReviewPromptTests.SomeProject(), "task/1-slug", "main",
            TimeSpan.FromMinutes(30)));

        prompt.Should().StartWith("# QA review");
        prompt.Should().Contain("blast radius");
    }

    /// <summary>
    /// A request carrying no drive decision renders the safe shape rather than the permissive
    /// one: a session told it may launch a product on the strength of a fact nobody looked up is
    /// the one outcome the setting exists to prevent.
    /// </summary>
    [Fact]
    public void A_qa_prompt_built_with_no_drive_decision_neither_drives_nor_offers()
    {
        string prompt = ReviewPersonaRegistry.For(ReviewPersona.Qa).Sessions.Single().BuildPrompt(
            new ReviewPersonaPromptRequest(
                QaReviewPromptTests.SomeTask(), QaReviewPromptTests.SomeProject(), "task/1-slug", "main",
                TimeSpan.FromMinutes(30)));

        prompt.Should().Contain("## You do not launch the product");
        prompt.Should().Contain("Do not offer to run the branch locally.");
    }

    [Fact]
    public void Every_persona_in_the_fixed_set_has_an_entry_carrying_its_criteria()
    {
        foreach (ReviewPersona persona in ReviewPersona.All)
        {
            ReviewPersonaEntry entry = ReviewPersonaRegistry.For(persona);
            entry.Persona.Should().Be(persona);
            entry.Criteria.Should().NotBeNullOrWhiteSpace(
                "a declared persona a member can never be told the criteria of is a declaration they cannot reason about");
        }

        ReviewPersonaRegistry.For(ReviewPersona.Engineer).IsRegistered.Should().BeTrue();
        ReviewPersonaRegistry.For(ReviewPersona.Qa).IsRegistered.Should().BeTrue(
            "the QA review's own prompt is piece 2 of this idea, and it has landed");
        ReviewPersonaRegistry.For(ReviewPersona.Designer).IsRegistered.Should().BeTrue(
            "the design review's own prompt is piece 3 of this idea, and it has landed");

        // Which leaves Plan's fall-back-to-the-engineer arm with no input a member could
        // actually declare: all three are registered, so every declaration runs the review it
        // asked for. The arm stays for the next persona added to the set before its prompt
        // exists — the state QA and the designer were both in — and this is what says it is
        // unreachable today rather than quietly broken.
        foreach (ReviewPersona persona in ReviewPersona.All)
        {
            ReviewPersonaRegistry.Plan([persona]).FellBackToEngineer.Should().BeFalse();
        }
    }

    /// <summary>
    /// The designer maps to the design review prompt and to nothing else (idea b9b09779, piece
    /// 3): one session, under its own role name and its own findings slug, laid out in the
    /// report by the platform rather than carried in verbatim.
    /// </summary>
    [Fact]
    public void The_designer_persona_maps_to_the_design_review_prompt()
    {
        ReviewPersonaEntry entry = ReviewPersonaRegistry.For(ReviewPersona.Designer);

        ReviewPersonaSession session = entry.Sessions.Should().ContainSingle().Subject;
        session.Persona.Should().Be(ReviewPersona.Designer);
        session.Slug.Should().Be(DesignReviewSection.SessionSlug);
        session.RoleName.Should().Be(SessionRoleName.ReviewDesign(1));
        session.SeesTaskContext.Should().BeTrue(
            "the conformance lens here judges the change against a design named on the task or its linked "
            + "item, which a session that never saw the task cannot find");
        session.ComposeSection.Should().NotBeNull(
            "the fixed lens order, the drive statement and the closing offer are the platform's to write");
        entry.CanDriveTheProduct.Should().BeTrue();
        ReviewPersonaRegistry.For(ReviewPersona.Engineer).CanDriveTheProduct.Should().BeFalse(
            "the engineer's review reads a diff and has never stood anything up");
    }

    /// <summary>
    /// A drive decision is resolved per persona that can drive, and only for a persona that
    /// actually ran — a caller handing over one for a persona the plan skipped, or for one whose
    /// review never drives, must not be able to put it in front of a reader.
    /// </summary>
    [Fact]
    public void Only_a_running_persona_that_can_drive_keeps_a_drive_decision()
    {
        ReviewPersonaPlan plan = ReviewPersonaRegistry.Plan(
            [ReviewPersona.Engineer, ReviewPersona.Designer],
            [
                new ReviewDriveDecision(ReviewPersona.Designer, SettingOn: true, ProjectHasRunSkill: true),
                new ReviewDriveDecision(ReviewPersona.Engineer, SettingOn: true, ProjectHasRunSkill: true),
                new ReviewDriveDecision(ReviewPersona.Qa, SettingOn: true, ProjectHasRunSkill: true),
            ]);

        plan.DriveDecisions.Should().ContainSingle().Which.Persona.Should().Be(ReviewPersona.Designer);
        plan.DriveFor(ReviewPersona.Designer).Drives.Should().BeTrue();
        plan.DriveFor(ReviewPersona.Engineer).Drives.Should().BeFalse(
            "a persona with no decision falls back to no run skill, which cannot drive");
    }

    /// <summary>
    /// The decision has to survive the round trip through the event store, since the report is
    /// composed from what the run recorded and not from a fresh resolve. Serialized under the
    /// daemon's own Marten options, and checked for what must NOT be there too: the three
    /// derived members are report prose and conclusions, and a stream carries the facts that
    /// were recorded rather than the reading somebody took of them on the day.
    /// </summary>
    [Fact]
    public void A_recorded_drive_decision_round_trips_through_the_stored_json()
    {
        JsonSerializerOptions storedJson =
            new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        PrReviewPersonasSelected selected = new(
            Guid.Empty, [ReviewPersona.Designer], [ReviewPersona.Designer], [], false, DateTimeOffset.UtcNow,
            [new ReviewDriveDecision(ReviewPersona.Designer, SettingOn: true, ProjectHasRunSkill: false)]);

        string json = JsonSerializer.Serialize(selected, storedJson);
        json.Should().NotContain("drives").And.NotContain("whyNotDriven").And.NotContain("canOffer");

        PrReviewPersonasSelected read = JsonSerializer.Deserialize<PrReviewPersonasSelected>(json, storedJson)!;
        ReviewDriveDecision decision = read.DriveDecisions.Should().ContainSingle().Subject;
        decision.Persona.Should().Be(ReviewPersona.Designer);
        decision.SettingOn.Should().BeTrue();
        decision.ProjectHasRunSkill.Should().BeFalse();
        decision.Drives.Should().BeFalse();
    }

    /// <summary>
    /// A designer review whose run recorded no drive decision at all — a stream written before
    /// this feature, or a plan built by a caller with no project to read. It reads as a static
    /// review, never as a driven one: nothing observed says the product was stood up.
    /// </summary>
    [Fact]
    public void A_designer_plan_with_nothing_recorded_reads_as_a_review_that_did_not_drive()
    {
        ReviewDriveDecision drive = ReviewPersonaRegistry
            .Recorded([ReviewPersona.Designer], [ReviewPersona.Designer], null, fellBackToEngineer: false)
            .DriveFor(ReviewPersona.Designer);

        drive.Drives.Should().BeFalse();
        drive.ProjectHasRunSkill.Should().BeFalse();
        drive.CanOfferToRunItLive.Should().BeFalse();
    }

    /// <summary>
    /// How strictly a session's verdict is screened follows the session's own prompt, declared on
    /// the session, never where that session happens to land in the plan. The engineer's two are
    /// first and second today only because the engineer is the only registered persona, and a
    /// session's screening must not change with what else its assignee declared.
    /// </summary>
    [Fact]
    public void Whether_a_session_saw_the_tasks_context_is_declared_on_it_not_inferred_from_its_position()
    {
        IReadOnlyList<ReviewPersonaSession> sessions = ReviewPersonaRegistry.For(ReviewPersona.Engineer).Sessions;

        sessions.Single(session => session.Slug == ReviewLens.Adversarial.Slug).SeesTaskContext.Should().BeFalse(
            "the adversarial lens is never handed the task's objective or acceptance criteria");
        sessions.Single(session => session.Slug == ReviewLens.Conformance.Slug).SeesTaskContext.Should().BeTrue(
            "the conformance lens is the only pr-review session that gets them");
    }

    /// <summary>
    /// Only the engineer's own session failing takes the run down: an additional persona's
    /// failure is recorded and named, so one bad session never costs the others their findings.
    /// </summary>
    [Fact]
    public void Only_the_engineers_sessions_fail_the_whole_run()
    {
        ReviewPersonaRegistry.For(ReviewPersona.Engineer).FailureFailsTheRun.Should().BeTrue();
        ReviewPersonaRegistry.For(ReviewPersona.Qa).FailureFailsTheRun.Should().BeFalse();
        ReviewPersonaRegistry.For(ReviewPersona.Designer).FailureFailsTheRun.Should().BeFalse();
    }

    /// <summary>
    /// A run dispatched before review personas existed recorded no selection at all. It ran the
    /// engineer's review, because that was the only review there was, and it has to replay as
    /// exactly that rather than as a run with no primary session.
    /// </summary>
    [Fact]
    public void A_run_that_recorded_no_selection_replays_as_the_engineer_review()
    {
        ReviewPersonaPlan plan = ReviewPersonaRegistry.Recorded(null, null, null, fellBackToEngineer: false);

        plan.Ran.Should().Equal(ReviewPersona.Engineer);
        plan.Sessions.Select(session => session.Slug).Should().Equal(
            ReviewLens.Adversarial.Slug, ReviewLens.Conformance.Slug);
    }

    [Fact]
    public void A_recorded_selection_is_rebuilt_rather_than_re_derived_from_the_registry()
    {
        ReviewPersonaPlan plan = ReviewPersonaRegistry.Recorded(
            [ReviewPersona.Qa], [ReviewPersona.Engineer], [ReviewPersona.Qa], fellBackToEngineer: true);

        plan.Requested.Should().Equal(ReviewPersona.Qa);
        plan.Ran.Should().Equal(ReviewPersona.Engineer);
        plan.Skipped.Should().Equal(ReviewPersona.Qa);
        plan.FellBackToEngineer.Should().BeTrue();
    }

    /// <summary>
    /// The findings report a human walks: one section per persona in the fixed order, the
    /// engineer's two lenses under its own, and every skipped persona named with why.
    /// </summary>
    [Fact]
    public async Task The_report_carries_one_section_per_persona_in_the_fixed_order()
    {
        string runDirectory = Path.Combine(Path.GetTempPath(), $"h9k-persona-report-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runDirectory);
        try
        {
            await File.WriteAllTextAsync(
                RunPaths.ReviewLensFindingsFile(runDirectory, 1, ReviewLens.Adversarial.Slug),
                "Nothing found.\n\nRUN-SKILL DRIFT: no\n\nVERDICT: merge-ready");
            await File.WriteAllTextAsync(
                RunPaths.ReviewLensFindingsFile(runDirectory, 1, ReviewLens.Conformance.Slug),
                "Matches the description.\n\nVERDICT: merge-ready");

            string body = await PrReviewEngine.ComposePersonaSectionsAsync(
                runDirectory,
                ReviewPersonaRegistry.Recorded(
                    [ReviewPersona.Engineer, ReviewPersona.Qa, ReviewPersona.Designer],
                    [ReviewPersona.Engineer],
                    [ReviewPersona.Qa, ReviewPersona.Designer],
                    fellBackToEngineer: false),
                new Dictionary<string, ReviewPersonaSessionFailure>(),
                CancellationToken.None);

            body.IndexOf("## Engineer review", StringComparison.Ordinal).Should().BeGreaterThanOrEqualTo(0);
            body.IndexOf("## QA review", StringComparison.Ordinal).Should().BeGreaterThan(
                body.IndexOf("## Engineer review", StringComparison.Ordinal));
            body.IndexOf("## Design review", StringComparison.Ordinal).Should().BeGreaterThan(
                body.IndexOf("## QA review", StringComparison.Ordinal));

            body.Should().Contain("### Adversarial (full depth)").And.Contain("Nothing found.");
            body.Should().Contain("Matches the description.");
            body.Should().Contain("no review prompt is registered for the qa persona yet");
            body.Should().Contain("no review prompt is registered for the designer persona yet");

            // The standing question, recorded either way: the adversarial pass answered it and
            // the conformance pass did not, and the report says which is which rather than
            // reading a silence as a no.
            body.Should().Contain("Run-skill drift: checked, no");
            body.Should().Contain("Run-skill drift: not answered by this pass");
        }
        finally
        {
            Directory.Delete(runDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task A_persona_whose_session_failed_is_named_in_the_report_rather_than_dropped()
    {
        string runDirectory = Path.Combine(Path.GetTempPath(), $"h9k-persona-report-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runDirectory);
        try
        {
            await File.WriteAllTextAsync(
                RunPaths.ReviewLensFindingsFile(runDirectory, 1, ReviewLens.Adversarial.Slug),
                "Nothing found.\n\nRUN-SKILL DRIFT: no\n\nVERDICT: merge-ready");

            string body = await PrReviewEngine.ComposePersonaSectionsAsync(
                runDirectory,
                ReviewPersonaRegistry.Plan([ReviewPersona.Engineer]),
                new Dictionary<string, ReviewPersonaSessionFailure>
                {
                    [ReviewLens.Conformance.Slug] = new(
                        ReviewPersona.Engineer, "The pr-review conformance session died without a result.",
                        DateTimeOffset.UtcNow),
                },
                CancellationToken.None);

            body.Should().Contain("### Conformance");
            body.Should().Contain("Not delivered: The pr-review conformance session died without a result.");
        }
        finally
        {
            Directory.Delete(runDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task A_fallback_to_the_engineer_is_said_plainly_in_the_report()
    {
        string runDirectory = Path.Combine(Path.GetTempPath(), $"h9k-persona-report-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runDirectory);
        try
        {
            // Rebuilt from what a run actually recorded rather than planned fresh: every
            // persona a member can declare is registered now, so Plan cannot produce this shape
            // any more, and the runs that carry it are the ones dispatched while QA or the
            // designer was still declared-but-unbuilt. Their reports are still read.
            string body = await PrReviewEngine.ComposePersonaSectionsAsync(
                runDirectory,
                ReviewPersonaRegistry.Recorded(
                    [ReviewPersona.Qa], [ReviewPersona.Engineer], [ReviewPersona.Qa],
                    fellBackToEngineer: true),
                new Dictionary<string, ReviewPersonaSessionFailure>(), CancellationToken.None);

            body.Should().Contain("the engineer's review ran in their place");
            body.Should().Contain("## QA review");
        }
        finally
        {
            Directory.Delete(runDirectory, recursive: true);
        }
    }
}

using FluentAssertions;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The design review's own half of a findings report (idea b9b09779, piece 3): what the platform
/// writes, from what the session wrote and from what the run decided about driving. Pure — a
/// string in, a string out — because every rule under test is a rule about the report's shape
/// rather than about any session that produced it.
/// </summary>
public sealed class DesignReviewSectionTests
{
    private static readonly ReviewDriveDecision Driven =
        new(ReviewPersona.Designer, SettingOn: true, ProjectHasRunSkill: true);

    private static readonly ReviewDriveDecision SettingOff =
        new(ReviewPersona.Designer, SettingOn: false, ProjectHasRunSkill: true);

    private static readonly ReviewDriveDecision NoRunSkill =
        new(ReviewPersona.Designer, SettingOn: true, ProjectHasRunSkill: false);

    /// <summary>
    /// A change with nothing user-facing in it: the session answered no lens at all. Every one of
    /// the seven is still present, in the fixed order, each naming in one line the usual reason a
    /// lens goes unanswered — a section that simply went missing would read as a lens that found
    /// nothing wrong. The line attributes rather than asserts: the session skipped the lens, and
    /// what the change does or does not touch is not something it reported.
    /// </summary>
    [Fact]
    public void A_change_with_nothing_user_facing_leaves_every_lens_present_and_unanswered()
    {
        string section = DesignReviewSection.Compose(
            "DESIGN REFERENCE: https://figma.com/file/abc, named in the pull request body\n"
            + "DESIGN SYSTEM: none\n\n"
            + "Nothing in this change reaches a user-facing surface.\n\nVERDICT: merge-ready",
            NoRunSkill);

        foreach (DesignReviewLens lens in DesignReviewLens.All)
        {
            section.Should().Contain($"#### {lens.Heading}");
            section.Should().Contain(
                $"The usual reason is that {lens.NotApplicableBecause}, but that is not something "
                + "the session reported.");
        }

        section.Should().NotContain("Not applicable:",
            "stating the change touches nothing here is an observation this session never made");

        HeadingOrder(section).Should().Equal([.. DesignReviewLens.All.Select(lens => lens.Heading)]);
    }

    /// <summary>
    /// The order is the vocabulary's own, always, regardless of the order the session happened to
    /// answer in. Two design reports of two different pull requests have to read the same way.
    /// </summary>
    [Fact]
    public void The_lenses_print_in_the_fixed_order_whatever_order_the_session_answered_in()
    {
        string section = DesignReviewSection.Compose(
            "DESIGN REFERENCE: a prototype at https://preview.example/pr-42\n"
            + "DESIGN SYSTEM: tokens in src/styles/tokens.css\n"
            + "DESIGN LENS: design-system\nThe new badge hard-codes its background.\n"
            + "DESIGN LENS: user-experience\nThe empty state has no way out.\n"
            + "DESIGN LENS: motion\nThe drawer slides in over 600ms.\n",
            NoRunSkill);

        HeadingOrder(section).Should().Equal([.. DesignReviewLens.All.Select(lens => lens.Heading)]);
        section.Should().Contain("The empty state has no way out.");
        section.Should().Contain("The new badge hard-codes its background.");
    }

    /// <summary>
    /// No Figma link, image set, or prototype named anywhere. The conformance lens reports that
    /// and judges nothing, and every other lens is untouched by the gap.
    /// </summary>
    [Fact]
    public void No_reference_makes_the_conformance_lens_report_it_and_judge_nothing()
    {
        string section = DesignReviewSection.Compose(
            "DESIGN REFERENCE: none\n"
            + "DESIGN SYSTEM: none\n"
            + "DESIGN LENS: proposed-design\nIt looks roughly like the rest of the settings pages.\n"
            + "DESIGN LENS: look-and-feel\nThe heading is two sizes larger than its siblings.\n",
            NoRunSkill);

        section.Should().Contain("**Proposed design:** no reference supplied");
        section.Should().Contain("No reference supplied. Nothing is judged against an imagined design");
        section.Should().NotContain("It looks roughly like the rest of the settings pages.",
            "a conformance judgment with nothing to judge against is exactly what this lens must not print");
        section.Should().Contain("it is withheld from this report for that reason",
            "withholding it silently would leave a reader unable to tell it from a lens the session skipped");
        section.Should().Contain("The heading is two sizes larger than its siblings.",
            "every other lens still applies in full");
    }

    /// <summary>
    /// No reference AND no conformance judgment written: the lens says the reference is missing
    /// and claims nothing about withholding anything, because there was nothing to withhold.
    /// </summary>
    [Fact]
    public void No_reference_and_no_judgment_claims_nothing_was_withheld()
    {
        string section = DesignReviewSection.Compose("DESIGN REFERENCE: none\n", NoRunSkill);

        section.Should().Contain("No reference supplied. Nothing is judged against an imagined design");
        section.Should().NotContain("it is withheld from this report");
    }

    /// <summary>
    /// A session that wrote a completely empty findings file. Every lens still prints, because
    /// the fixed order is what composing this section is for — but the report says first that
    /// the file was empty, so seven unanswered lenses are not read as seven judgments.
    /// </summary>
    [Fact]
    public void An_empty_findings_file_is_named_before_the_lenses_it_would_otherwise_look_like()
    {
        string section = DesignReviewSection.Compose(string.Empty, NoRunSkill);

        section.Should().Contain("**This session's findings file is empty.**");
        section.Should().Contain("not a review that looked and found nothing");
        section.IndexOf("findings file is empty", StringComparison.Ordinal).Should().BeLessThan(
            section.IndexOf("Not answered — the session wrote nothing", StringComparison.Ordinal));

        DesignReviewSection.Compose("DESIGN LENS: motion\nThe drawer eases too slowly.\n", NoRunSkill)
            .Should().NotContain("findings file is empty",
                "a session that answered one lens and skipped six wrote something");
    }

    /// <summary>
    /// A reference that IS named is printed, and the conformance lens prints what the session
    /// actually concluded rather than the no-reference line.
    /// </summary>
    [Fact]
    public void A_named_reference_is_printed_and_the_conformance_lens_speaks_for_itself()
    {
        string section = DesignReviewSection.Compose(
            "DESIGN REFERENCE: https://figma.com/file/abc frame 'Settings / empty', named on issue #42\n"
            + "DESIGN LENS: proposed-design\nThe comp puts the action above the illustration.\n",
            NoRunSkill);

        section.Should().Contain("**Proposed design:** https://figma.com/file/abc frame 'Settings / empty'");
        section.Should().Contain("The comp puts the action above the illustration.");
        section.Should().NotContain("no reference supplied");
    }

    /// <summary>
    /// Three outcomes, not two: named, looked and found none, and never answered. The third is
    /// the one a report must not collapse into the second, because "the session looked for tokens
    /// and found none" over a session that wrote no marker is an observation nobody made.
    /// </summary>
    [Fact]
    public void The_design_system_is_named_its_absence_is_or_the_silence_is()
    {
        DesignReviewSection.Compose("DESIGN SYSTEM: tokens in src/styles/tokens.css\n", NoRunSkill)
            .Should().Contain("**Design system:** tokens in src/styles/tokens.css");

        DesignReviewSection.Compose("DESIGN SYSTEM: none\n", NoRunSkill)
            .Should().Contain("**Design system:** none found");

        string silent = DesignReviewSection.Compose("The session never answered.\n", NoRunSkill);
        silent.Should().Contain("**Design system:** not reported",
            "a session that said nothing about a design system did not look and find none");
        silent.Should().NotContain("**Design system:** none found");
        silent.Should().Contain("nothing below is judged against one",
            "the consequence is the same either way, and the report still says so");
    }

    /// <summary>
    /// Driving turned off for this project: the report is a static one and says so, with the
    /// reason named, and no Driven section at all.
    /// </summary>
    [Fact]
    public void The_setting_off_yields_a_static_report_that_says_why()
    {
        string section = DesignReviewSection.Compose(
            "DESIGN REFERENCE: none\nDESIGN LENS: accessibility\nThe close button has no accessible name.\n",
            SettingOff);

        section.Should().Contain("**Drive:** static");
        section.Should().Contain("this project has design-review driving turned off");
        section.Should().NotContain("#### Driven");
        section.Should().Contain("Static: read from the markup, with no automated audit on a running screen.");
        section.Should().Contain("The close button has no accessible name.");
    }

    /// <summary>
    /// The other reason a review is static — driving is on, but there is no run skill to drive
    /// with. Named separately, because the two are reversed by different people doing different
    /// things.
    /// </summary>
    [Fact]
    public void No_run_skill_yields_a_static_report_naming_that_reason_instead()
    {
        string section = DesignReviewSection.Compose("DESIGN REFERENCE: none\n", NoRunSkill);

        section.Should().Contain("**Drive:** static");
        section.Should().Contain("no run skill on its ledger");
        section.Should().NotContain("driving turned off");
    }

    /// <summary>
    /// The setting on with a run skill: the walk is reported in a Driven section, each screen
    /// with the screenshot it cites and the automated audit that ran on it, and the accessibility
    /// lens is not marked static.
    /// </summary>
    [Fact]
    public void The_setting_on_with_a_run_skill_yields_the_driven_section_and_its_screenshots()
    {
        string section = DesignReviewSection.Compose(
            "DESIGN REFERENCE: https://figma.com/file/abc\n"
            + "DESIGN SYSTEM: a component library under src/components\n"
            + "DESIGN LENS: accessibility\nFocus never reaches the dismiss control.\n"
            + "DRIVEN SCREEN: Settings, empty state\n"
            + "SCREENSHOT: runs/01a0/screens/settings-empty.png\n"
            + "ACCESSIBILITY AUDIT: two contrast violations on the secondary label\n"
            + "The illustration pushes the only action below the fold at 900px.\n"
            + "DRIVEN SCREEN: Settings, one item\n"
            + "SCREENSHOT: runs/01a0/screens/settings-one.png\n"
            + "ACCESSIBILITY AUDIT: no violations\n"
            + "The row's hover state is the only affordance that it is clickable.\n",
            Driven);

        section.Should().Contain("**Drive:** driven");
        section.Should().Contain("walked 2 screen(s)");
        section.Should().Contain("#### Driven");
        section.Should().Contain("**Settings, empty state**");
        section.Should().Contain("runs/01a0/screens/settings-empty.png");
        section.Should().Contain("runs/01a0/screens/settings-one.png");
        section.Should().Contain("Accessibility audit: two contrast violations on the secondary label");
        section.Should().Contain("Accessibility audit: no violations");
        section.Should().Contain("The illustration pushes the only action below the fold at 900px.");
        section.Should().NotContain("Static: read from the markup",
            "the accessibility lens is only marked static when nothing was driven");
    }

    /// <summary>
    /// A screenshot cited inside a lens block stays in that block, beside the finding it
    /// supports. Lifting every screenshot into a list of its own is exactly the separation this
    /// report must not introduce.
    /// </summary>
    [Fact]
    public void A_screenshot_cited_beside_a_finding_stays_beside_it()
    {
        string section = DesignReviewSection.Compose(
            "DESIGN LENS: look-and-feel\n"
            + "The heading crowds the tab strip.\n"
            + "SCREENSHOT: runs/01a0/screens/header.png\n"
            + "DRIVEN SCREEN: Header\nSCREENSHOT: runs/01a0/screens/header.png\n",
            Driven);

        int finding = section.IndexOf("The heading crowds the tab strip.", StringComparison.Ordinal);
        int citation = section.IndexOf("runs/01a0/screens/header.png", StringComparison.Ordinal);
        finding.Should().BeGreaterThanOrEqualTo(0);
        citation.Should().BeGreaterThan(finding);
        section.IndexOf("#### Driven", StringComparison.Ordinal).Should().BeGreaterThan(citation,
            "the citation beside the finding comes before the Driven section, not only inside it");
    }

    /// <summary>
    /// A review authorised to drive that reported no walked screen. The report says exactly that
    /// rather than printing a driven claim with nothing behind it, and rather than quietly
    /// re-describing itself as a static review nobody decided on.
    /// </summary>
    [Fact]
    public void A_review_authorised_to_drive_that_walked_nothing_says_so()
    {
        string section = DesignReviewSection.Compose("DESIGN REFERENCE: none\n", Driven);

        section.Should().Contain("driving was authorised for this review, and the session reported walking no");
        section.Should().Contain("#### Driven");
        section.Should().Contain("there is no walk to show and no screenshot to cite");
    }

    /// <summary>
    /// The prompt teaches this grammar inside indented code blocks, so a session copying the
    /// shape it was shown emits indented marker lines. The parser reads them: a report that
    /// silently lost every lens because the session followed the example's own indentation is a
    /// failure no assertion over an unindented fixture would ever catch.
    /// </summary>
    [Fact]
    public void Markers_indented_the_way_the_prompts_own_examples_show_them_still_parse()
    {
        string section = DesignReviewSection.Compose(
            "    DESIGN REFERENCE: https://figma.com/file/abc\n"
            + "    DESIGN SYSTEM: tokens in src/styles/tokens.css\n"
            + "    DESIGN LENS: motion\n"
            + "    The drawer eases over 600ms with no reduced-motion query.\n"
            + "    DRIVEN SCREEN: Settings\n"
            + "    SCREENSHOT: runs/01a0/screens/settings.png\n"
            + "    ACCESSIBILITY AUDIT: one contrast violation\n",
            Driven);

        section.Should().Contain("**Proposed design:** https://figma.com/file/abc");
        section.Should().Contain("**Design system:** tokens in src/styles/tokens.css");
        section.Should().Contain("The drawer eases over 600ms with no reduced-motion query.");
        section.Should().Contain("walked 1 screen(s)");
        section.Should().Contain("runs/01a0/screens/settings.png");
        section.Should().Contain("Accessibility audit: one contrast violation");
    }

    /// <summary>
    /// A static review whose session nonetheless reported walking screens. The block it wrote is
    /// still withheld — a Driven section under a drive line that just said nothing was seen
    /// running would contradict itself — but the withholding is SAID, on the same terms the
    /// conformance lens says it when it drops a judgment made against no reference. A session
    /// claiming a walk it was told not to take is drift a reader needs to see, and silently
    /// deleting the claim is exactly how it would go unnoticed.
    /// </summary>
    [Fact]
    public void A_static_review_that_reported_walking_screens_says_so_rather_than_dropping_it()
    {
        string section = DesignReviewSection.Compose(
            "DESIGN REFERENCE: none\n"
            + "DRIVEN SCREEN: Settings\n"
            + "SCREENSHOT: runs/01a0/screens/settings.png\n"
            + "The save button never enables.\n",
            SettingOff);

        section.Should().NotContain("#### Driven");
        section.Should().Contain("reported walking 1 screen(s) anyway");
        section.Should().Contain("withheld");
        section.Should().NotContain("The save button never enables.");
    }

    /// <summary>A walked screen with no screenshot is a stated gap, not a screen that quietly has one.</summary>
    [Fact]
    public void A_walked_screen_with_no_screenshot_admits_it()
    {
        string section = DesignReviewSection.Compose(
            "DRIVEN SCREEN: Settings\nThe save button never enables.\n", Driven);

        section.Should().Contain("No screenshot recorded for this screen.");
        section.Should().Contain("Accessibility audit: none reported for this screen.");
    }

    /// <summary>
    /// The offer is present exactly when there is a run skill to honour it with, driven or not,
    /// and it is a question rather than something the session announces it is doing.
    /// </summary>
    [Fact]
    public void The_offer_is_present_with_a_run_skill_and_absent_without()
    {
        DesignReviewSection.Compose("DESIGN REFERENCE: none\n", Driven)
            .Should().Contain("Would you like this branch run locally so you can walk it yourself?");

        DesignReviewSection.Compose("DESIGN REFERENCE: none\n", SettingOff)
            .Should().Contain("Would you like this branch run locally so you can walk it yourself?",
                "a static review is if anything the one a reviewer most wants to walk in person");

        DesignReviewSection.Compose("DESIGN REFERENCE: none\n", NoRunSkill)
            .Should().NotContain("Would you like this branch run locally",
                "there is nothing that says how to stand this project up, so the offer cannot be honoured");
    }

    /// <summary>
    /// A session that ignored the contract entirely still composes into a readable report: its
    /// prose is kept, every lens says it was not answered, and nothing claims more than that —
    /// including about the reference, which this session never looked for as far as anyone knows.
    /// </summary>
    [Fact]
    public void A_session_that_wrote_no_markers_still_composes_into_an_honest_report()
    {
        string section = DesignReviewSection.Compose(
            "It all looks fine to me.\n\nVERDICT: merge-ready", NoRunSkill);

        section.Should().Contain("It all looks fine to me.");
        section.Should().Contain("**Proposed design:** not reported");
        section.Should().NotContain("the session looked on the linked task or issue",
            "this session reported nothing about a reference, so nothing here says it went looking");
        section.Should().Contain("Not answered — the session wrote nothing under this lens.");
    }

    /// <summary>
    /// The two lines the shared verdict contract requires at the end of every review's output
    /// arrive after the last lens or screen block, and they belong to the session rather than to
    /// whichever block happened to be open. A verdict filed under a lens reads as that lens's own
    /// prose, and a verdict filed under a screen block a static review withholds disappears with
    /// it.
    /// </summary>
    [Fact]
    public void The_verdict_and_drift_lines_belong_to_the_session_not_to_the_last_block()
    {
        string underALens = DesignReviewSection.Compose(
            "DESIGN LENS: css-practice\nThree rules duplicate the card shadow.\n\n"
            + "RUN-SKILL DRIFT: no\n\nVERDICT: needs-fixes\n",
            NoRunSkill);

        underALens.IndexOf("VERDICT: needs-fixes", StringComparison.Ordinal).Should().BeLessThan(
            underALens.IndexOf("#### CSS practice", StringComparison.Ordinal),
            "the verdict sits in the preamble above the lenses, not inside the last one answered");

        string underAWithheldScreen = DesignReviewSection.Compose(
            "DRIVEN SCREEN: Settings\nThe save button never enables.\n\nVERDICT: needs-fixes\n",
            SettingOff);

        underAWithheldScreen.Should().Contain("VERDICT: needs-fixes",
            "withholding an unauthorised walk must not take the session's own verdict with it");
        underAWithheldScreen.Should().NotContain("The save button never enables.");
    }

    /// <summary>A lens slug this build does not know keeps the text and claims no lens for it.</summary>
    [Fact]
    public void An_unreadable_lens_slug_keeps_its_text_without_claiming_a_lens()
    {
        string section = DesignReviewSection.Compose(
            "DESIGN REFERENCE: https://figma.com/file/abc\n"
            + "DESIGN LENS: internationalization\nThe date format is hard-coded to en-US.\n",
            NoRunSkill);

        section.Should().Contain("The date format is hard-coded to en-US.");
        foreach (DesignReviewLens lens in DesignReviewLens.All)
        {
            section.Should().Contain(
                $"The usual reason is that {lens.NotApplicableBecause}, but that is not something "
                + "the session reported.");
        }
    }

    /// <summary>
    /// The composed section is what the findings report actually carries for the designer —
    /// checked through the report composer itself rather than only through
    /// <see cref="DesignReviewSection.Compose"/>, so the registry's own wiring of the two is
    /// under test and not just the composer.
    /// </summary>
    [Fact]
    public async Task The_findings_report_carries_the_composed_design_section()
    {
        string runDirectory = Path.Combine(Path.GetTempPath(), $"h9k-design-report-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runDirectory);
        try
        {
            await File.WriteAllTextAsync(
                RunPaths.ReviewLensFindingsFile(runDirectory, 1, DesignReviewSection.SessionSlug),
                "DESIGN REFERENCE: none\n"
                + "DESIGN SYSTEM: none\n"
                + "DESIGN LENS: css-practice\nThree rules duplicate the card shadow.\n\n"
                + "RUN-SKILL DRIFT: no\n\nVERDICT: needs-fixes");

            string body = await PrReviewEngine.ComposePersonaSectionsAsync(
                runDirectory,
                ReviewPersonaRegistry.Recorded(
                    [ReviewPersona.Designer], [ReviewPersona.Designer], null, fellBackToEngineer: false,
                    [new ReviewDriveDecision(ReviewPersona.Designer, SettingOn: false, ProjectHasRunSkill: true)]),
                new Dictionary<string, ReviewPersonaSessionFailure>(),
                CancellationToken.None);

            body.Should().Contain("## Design review");
            body.Should().Contain("### Design (seven lenses)");
            body.Should().Contain("Run-skill drift: checked, no");
            body.Should().Contain("**Drive:** static");
            body.Should().Contain("Three rules duplicate the card shadow.");
            body.Should().Contain("#### Look and feel");
            body.Should().Contain("Would you like this branch run locally");
        }
        finally
        {
            Directory.Delete(runDirectory, recursive: true);
        }
    }

    /// <summary>
    /// The designer's findings file missing entirely — a session that never wrote one, with no
    /// recorded failure to explain it. The report says exactly that and stops: handing an absent
    /// file to the composer would lay out a full, confident section, seven lenses deep, over a
    /// review whose output does not exist.
    /// </summary>
    [Fact]
    public async Task A_design_session_with_no_findings_file_gets_one_line_rather_than_a_composed_section()
    {
        string runDirectory = Path.Combine(Path.GetTempPath(), $"h9k-design-report-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runDirectory);
        try
        {
            string body = await PrReviewEngine.ComposePersonaSectionsAsync(
                runDirectory,
                ReviewPersonaRegistry.Recorded(
                    [ReviewPersona.Designer], [ReviewPersona.Designer], null, fellBackToEngineer: false),
                new Dictionary<string, ReviewPersonaSessionFailure>(),
                CancellationToken.None);

            body.Should().Contain("## Design review");
            body.Should().Contain("(no findings recorded)");
            body.Should().NotContain("Not answered — the session wrote nothing");
            body.Should().NotContain("**Drive:**");
            body.Should().NotContain("Would you like this branch run locally");
        }
        finally
        {
            Directory.Delete(runDirectory, recursive: true);
        }
    }

    private static IReadOnlyList<string> HeadingOrder(string section) =>
    [
        .. section.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(line => line.StartsWith("#### ", StringComparison.Ordinal))
            .Select(line => line[5..])
            .Where(heading => DesignReviewLens.All.Any(lens => lens.Heading == heading)),
    ];
}

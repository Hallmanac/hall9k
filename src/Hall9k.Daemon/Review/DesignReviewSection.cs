using System.Text;
using Hall9k.Domain.Features.Run;

namespace Hall9k.Daemon.Review;

/// <summary>
/// One screen the design session actually walked in the running product, as it reported it.
/// </summary>
/// <param name="Label">What the session called the screen or flow.</param>
/// <param name="Screenshots">The screenshot paths it cited for this screen, in the order cited. Empty is a stated gap, never smoothed over.</param>
/// <param name="AccessibilityAudit">What the automated audit said here, or null when the session reported none.</param>
/// <param name="Body">Everything else the session wrote under this screen: the findings the walk produced.</param>
public sealed record DesignReviewDrivenScreen(
    string Label, IReadOnlyList<string> Screenshots, string? AccessibilityAudit, string Body);

/// <summary>
/// What a design session's own findings file says, read back (idea b9b09779, piece 3). Every
/// field is either something the session actually wrote or an admitted absence — nothing here is
/// filled in on the session's behalf.
/// </summary>
/// <param name="Reference">The proposed design the session found named, or null when it wrote the nothing word or never answered at all — <paramref name="ReferenceAnswered"/> tells those two apart.</param>
/// <param name="ReferenceAnswered">Whether the session wrote the reference marker at all. False is "this session never answered", which is a different fact from "it looked and found none" and is reported as one.</param>
/// <param name="DesignSystem">The design system it found in the repository, or null on the same two-way terms as <paramref name="Reference"/>.</param>
/// <param name="DesignSystemAnswered">Whether the session wrote the design-system marker at all, on the identical terms as <paramref name="ReferenceAnswered"/>.</param>
/// <param name="Lenses">Each lens the session answered under, keyed by slug. A lens absent from here is one it said nothing about.</param>
/// <param name="DrivenScreens">The screens it walked, in the order walked. Empty for a review that did not drive, and for one that was meant to and did not.</param>
/// <param name="Preamble">Anything the session wrote outside a lens or screen block: any framing it chose before its first marker, plus the verdict line and the run-skill drift answer the shared verdict contract requires last, which are lifted here rather than swallowed by whichever block happened to be open when they arrived.</param>
public sealed record DesignReviewFindings(
    string? Reference,
    bool ReferenceAnswered,
    string? DesignSystem,
    bool DesignSystemAnswered,
    IReadOnlyDictionary<string, string> Lenses,
    IReadOnlyList<DesignReviewDrivenScreen> DrivenScreens,
    string Preamble)
{
    /// <summary>
    /// Whether the session wrote nothing at all — no marker, no prose. Distinguished from a
    /// session that answered some lenses and not others, because the composed report prints a
    /// line under every unanswered lens, and seven of those over an empty file would read as a
    /// review that happened and found nothing. Keyed on whether each marker was written rather
    /// than on what it said: a file holding only the nothing word is a session that answered.
    /// </summary>
    public bool Empty =>
        !ReferenceAnswered && !DesignSystemAnswered && Lenses.Count == 0 && DrivenScreens.Count == 0
        && Preamble.IsBlank();
}

/// <summary>
/// The design review's half of the findings report: the platform composes it from what the
/// session wrote, rather than carrying the session's file in verbatim the way every engineer
/// session's is (idea b9b09779, piece 3).
/// <para>
/// It is composed because three things in this report are the platform's to say and not the
/// reviewing agent's. The seven lenses appear in a fixed order and a lens the change did not
/// touch says so in one line rather than going missing, so two design reports of two different
/// pull requests read the same way and a silent section is never mistaken for a clean one.
/// Whether the session drove the product is what the run decided and recorded at dispatch
/// (<see cref="ReviewDriveDecision"/>), so a report cannot claim a walk the dispatch never
/// authorised. And the closing offer to run the branch for the reviewer is present exactly when
/// there is a run skill to run it with — a question in the report, never an action, and never
/// one the session talks itself into or out of.
/// </para>
/// </summary>
public static class DesignReviewSection
{
    /// <summary>
    /// This session's name in the run directory and on the run stream — the slug its findings
    /// file is written under (<c>review-1-design-findings.md</c>), beside the engineer's own
    /// adversarial and conformance files.
    /// </summary>
    public const string SessionSlug = "design";

    /// <summary>Starts one lens's block; the rest of the line is the lens slug (<see cref="DesignReviewLens"/>).</summary>
    public const string LensMarker = "DESIGN LENS:";

    /// <summary>The proposed design the session found named on the task, the issue, or the pull request body — or the word this class reads as none.</summary>
    public const string ReferenceMarker = "DESIGN REFERENCE:";

    /// <summary>The design system the session found in the repository — or the word this class reads as none.</summary>
    public const string DesignSystemMarker = "DESIGN SYSTEM:";

    /// <summary>Starts one walked screen's block; the rest of the line names the screen or flow.</summary>
    public const string DrivenScreenMarker = "DRIVEN SCREEN:";

    /// <summary>One screenshot path, cited beside the finding it supports.</summary>
    public const string ScreenshotMarker = "SCREENSHOT:";

    /// <summary>What the automated accessibility audit reported on the screen whose block this sits in.</summary>
    public const string AccessibilityAuditMarker = "ACCESSIBILITY AUDIT:";

    /// <summary>
    /// The word a session writes when it found nothing — no proposed design named anywhere, or no
    /// design system in the repository. Its own word rather than an empty value, so "the session
    /// looked and found nothing" and "the session never answered" stay different facts.
    /// </summary>
    public const string NothingWord = "none";

    /// <summary>Every marker this class parses, for the guard that keeps them out of template prose.</summary>
    public static IReadOnlyList<string> Markers { get; } =
    [
        LensMarker, ReferenceMarker, DesignSystemMarker, DrivenScreenMarker, ScreenshotMarker,
        AccessibilityAuditMarker,
    ];

    /// <summary>
    /// The design session's part of the findings report, composed from its own findings file and
    /// from what this run decided about driving.
    /// </summary>
    public static string Compose(string sessionText, ReviewDriveDecision drive)
    {
        DesignReviewFindings findings = Parse(sessionText);
        StringBuilder body = new();

        // An empty findings file, said first and said plainly. Every lens below it still prints,
        // because the fixed order is the point of composing this section at all — but a reader
        // who sees seven unanswered lenses has to know they came from a session that wrote
        // nothing, not from seven judgments that the change touched nothing.
        if (findings.Empty)
        {
            body.Append(
                "\n**This session's findings file is empty.** It wrote no lens, no reference, no design "
                + "system, and no prose at all. Everything below is the report's own fixed shape over "
                + "nothing, not a review that looked and found nothing.\n");
        }

        body.Append('\n').Append(DriveLine(drive, findings.DrivenScreens.Count)).Append('\n');
        body.Append('\n').Append(ReferenceLine(findings.Reference, findings.ReferenceAnswered)).Append('\n');
        body.Append('\n').Append(DesignSystemLine(findings.DesignSystem, findings.DesignSystemAnswered)).Append('\n');

        if (findings.Preamble.IsNotBlank())
        {
            body.Append('\n').Append(findings.Preamble).Append('\n');
        }

        foreach (DesignReviewLens lens in DesignReviewLens.All)
        {
            body.Append($"\n#### {lens.Heading}\n");
            body.Append('\n').Append(LensBody(lens, findings, drive)).Append('\n');
        }

        AppendDrivenSection(body, findings.DrivenScreens, drive);
        AppendOffer(body, drive);
        return body.ToString();
    }

    /// <summary>
    /// What a design session wrote, read back off its findings file. Tolerant throughout: a file
    /// with no markers at all parses as a preamble and nothing else, which composes into a report
    /// whose every lens says it was not answered — the honest read of a session that ignored the
    /// contract, rather than a crash or a silently empty section.
    /// </summary>
    public static DesignReviewFindings Parse(string? sessionText)
    {
        string[] lines = (sessionText ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        Dictionary<string, StringBuilder> lenses = new(StringComparer.Ordinal);
        List<DesignReviewDrivenScreen> screens = [];
        StringBuilder preamble = new();
        StringBuilder current = preamble;
        string? reference = null;
        string? designSystem = null;
        bool referenceAnswered = false;
        bool designSystemAnswered = false;

        // The screen being built, if any. Held apart from `current` so a SCREENSHOT or audit line
        // lands on the screen it sits under rather than only in that screen's prose.
        string? screenLabel = null;
        List<string> screenShots = [];
        string? screenAudit = null;
        StringBuilder screenBody = new();

        void CloseScreen()
        {
            if (screenLabel is null)
            {
                return;
            }

            screens.Add(new DesignReviewDrivenScreen(screenLabel, [.. screenShots], screenAudit, Trimmed(screenBody)));
            screenLabel = null;
            screenShots = [];
            screenAudit = null;
            screenBody = new StringBuilder();
        }

        foreach (string line in lines)
        {
            if (Value(line, LensMarker) is { } lensSlug)
            {
                CloseScreen();
                DesignReviewLens lens = DesignReviewLens.Read(lensSlug);

                // An unreadable lens slug goes to the preamble rather than being filed under a
                // name nothing prints: the text still reaches the reader, and no section claims
                // to be an answer under a lens this build does not have.
                current = lens.HasValue ? Section(lenses, lens.Slug) : preamble;
                continue;
            }

            if (Value(line, DrivenScreenMarker) is { } label)
            {
                CloseScreen();
                screenLabel = label;
                current = screenBody;
                continue;
            }

            if (Value(line, ReferenceMarker) is { } named)
            {
                reference = IsNothing(named) ? null : named;
                referenceAnswered = true;
                continue;
            }

            if (Value(line, DesignSystemMarker) is { } system)
            {
                designSystem = IsNothing(system) ? null : system;
                designSystemAnswered = true;
                continue;
            }

            if (Value(line, ScreenshotMarker) is { } shot && shot.IsNotBlank())
            {
                // Kept in the prose as well as on the screen: a screenshot cited inside a block
                // is cited beside the finding it supports, and lifting it out into a list of its
                // own is exactly the separation this report is not allowed to introduce.
                if (screenLabel is not null)
                {
                    screenShots.Add(shot);
                }

                current.Append(line.Trim()).Append('\n');
                continue;
            }

            if (Value(line, AccessibilityAuditMarker) is { } audit && screenLabel is not null)
            {
                screenAudit = audit;
                continue;
            }

            // The shared verdict contract puts these two last, which means they land after every
            // lens and screen block this file has — and a block is open by then, so appended
            // where they fell they would read as that lens's own prose, or be withheld entirely
            // along with a screen block a static review does not print. They belong to the
            // session as a whole, which is what the preamble is (independent pre-PR review,
            // cycle 1, adversarial lens).
            if (IsSessionWideContractLine(line))
            {
                preamble.Append(line).Append('\n');
                continue;
            }

            current.Append(line).Append('\n');
        }

        CloseScreen();
        return new DesignReviewFindings(
            reference,
            referenceAnswered,
            designSystem,
            designSystemAnswered,
            lenses.ToDictionary(entry => entry.Key, entry => Trimmed(entry.Value), StringComparer.Ordinal),
            screens,
            Trimmed(preamble));
    }

    /// <summary>
    /// Whether this line is one of the two the shared verdict contract requires at the end of
    /// every review's own output, rather than anything about a lens or a screen. Matched the same
    /// way <see cref="ReviewResultParser"/> matches them — a trimmed start of line, case
    /// insensitive — so a session's line either counts for both this report's layout and the
    /// engine's own parse, or for neither.
    /// </summary>
    private static bool IsSessionWideContractLine(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.StartsWith(ReviewResultParser.VerdictMarker, StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith(ReviewResultParser.RunSkillDriftMarker, StringComparison.OrdinalIgnoreCase);
    }

    private static StringBuilder Section(Dictionary<string, StringBuilder> lenses, string slug)
    {
        if (!lenses.TryGetValue(slug, out StringBuilder? section))
        {
            section = new StringBuilder();
            lenses[slug] = section;
        }

        return section;
    }

    /// <summary>
    /// What this run decided about driving and what the session actually reported walking, in one
    /// line. The two can disagree — a session authorised to drive that walked nothing — and the
    /// line says so rather than reporting the decision as though it were the outcome.
    /// </summary>
    private static string DriveLine(ReviewDriveDecision drive, int walkedScreens) =>
        !drive.Drives
            ? "**Drive:** static — this review read the diff and the project's design files only, "
              + $"because {drive.WhyNotDriven}. Nothing below was seen running."
              + StaticSessionWalkedAnyway(walkedScreens)
        : walkedScreens > 0
            ? "**Drive:** driven — the session stood this project's product up on the review worktree "
              + $"and walked {walkedScreens} screen(s), cited below under Driven."
            : "**Drive:** driving was authorised for this review, and the session reported walking no "
              + "screens at all. Read every lens below as a static read: whatever the reason, nothing "
              + "here was seen running.";

    /// <summary>
    /// The clause a static review's drive line ends with when the session reported walking
    /// screens regardless. The blocks themselves stay withheld — <see cref="AppendDrivenSection"/>
    /// prints nothing for a review that did not drive, because a Driven section sitting under a
    /// line that just said nothing was seen running is a report contradicting itself — but the
    /// withholding is stated rather than silent, exactly as <see cref="LensBody"/> states it when
    /// the conformance lens drops a judgment made against no reference. A session claiming a walk
    /// its own prompt told it not to take is drift, and a reader has to be able to see it.
    /// </summary>
    private static string StaticSessionWalkedAnyway(int walkedScreens) =>
        walkedScreens == 0
            ? string.Empty
            : $" The session reported walking {walkedScreens} screen(s) anyway, which this review was "
              + "told not to do; those blocks are withheld from this report rather than printed as a "
              + "walk nobody authorised, and they are still in this session's own findings file.";

    /// <summary>
    /// What the session found named, that it looked and found nothing, or that it never said —
    /// three outcomes, never two. The third is not the second: a report that printed "the session
    /// looked and found none" over a session that wrote no marker at all would be asserting an
    /// observation nobody made, which is the one thing this whole section is built not to do (see
    /// <see cref="NothingWord"/>, whose existence is that distinction).
    /// </summary>
    private static string ReferenceLine(string? reference, bool answered) =>
        reference.IsNotBlank() ? $"**Proposed design:** {reference}"
        : answered
            ? "**Proposed design:** no reference supplied — the session looked on the linked task or "
              + "issue and in the pull request body, and found no Figma link, image set, or prototype "
              + "named in any of them."
            : "**Proposed design:** not reported — this session never answered whether a proposed design "
              + "was named anywhere, so this report cannot say one way or the other. Nothing below is "
              + "judged against a design, on the same terms as if none had been found.";

    /// <summary>The design system's own three outcomes, on the identical terms <see cref="ReferenceLine"/> states.</summary>
    private static string DesignSystemLine(string? designSystem, bool answered) =>
        designSystem.IsNotBlank() ? $"**Design system:** {designSystem}"
        : answered
            ? "**Design system:** none found — the session looked for tokens, a component library, and a "
              + "documented system in this repository and found none, so nothing below is judged against one."
            : "**Design system:** not reported — this session never answered whether this repository has "
              + "one, so this report says neither that it does nor that it does not, and nothing below is "
              + "judged against one.";

    /// <summary>
    /// One lens's body: what the session wrote under it, or the one line that keeps the section
    /// present when it wrote nothing. Two lenses get a sentence of their own on top, because
    /// what they are allowed to conclude depends on facts outside the session's own prose: the
    /// conformance lens judges nothing when no reference was supplied, and the accessibility lens
    /// is a static read whenever nothing was driven.
    /// </summary>
    private static string LensBody(DesignReviewLens lens, DesignReviewFindings findings, ReviewDriveDecision drive)
    {
        string written = findings.Lenses.TryGetValue(lens.Slug, out string? body) ? body : string.Empty;
        if (lens == DesignReviewLens.ProposedDesign && findings.Reference is null)
        {
            // What the session wrote here is withheld rather than printed, because printing it
            // is exactly the judgment against an imagined design this lens must not make. Said
            // out loud when there was something, so the withholding is visible rather than a
            // silent drop: a reader who cannot see that text can still go and read the session's
            // own findings file.
            return "No reference supplied. Nothing is judged against an imagined design: name a Figma "
                + "link, an image set, or a prototype on the task, the linked issue, or the pull request "
                + "body, and this lens has something to read the change against."
                + (written.IsBlank()
                    ? string.Empty
                    : " The session wrote a conformance judgment here anyway; it is withheld from this "
                      + "report for that reason, and it is still in this session's own findings file.");
        }

        if (written.IsBlank())
        {
            // The lens's own half-sentence, attributed rather than asserted. "Not applicable: the
            // change touches no stylesheet" is a statement about the diff, and a session that
            // simply skipped this lens made no such statement — the same unobserved-fact guess
            // ReferenceLine and DesignSystemLine above refuse to make, found by sweeping this
            // fix's own defect class.
            return $"Not answered — the session wrote nothing under this lens. The usual reason is "
                + $"that {lens.NotApplicableBecause}, but that is not something the session reported.";
        }

        return lens == DesignReviewLens.Accessibility && !drive.Drives
            ? "Static: read from the markup, with no automated audit on a running screen. Every finding "
              + $"below is a static one.\n\n{written}"
            : written;
    }

    private static void AppendDrivenSection(
        StringBuilder body, IReadOnlyList<DesignReviewDrivenScreen> screens, ReviewDriveDecision drive)
    {
        if (!drive.Drives)
        {
            return;
        }

        body.Append("\n#### Driven\n");
        if (screens.Count == 0)
        {
            body.Append(
                "\nNothing. This review was authorised to stand the product up and reported no walked "
                + "screen, so there is no walk to show and no screenshot to cite.\n");
            return;
        }

        foreach (DesignReviewDrivenScreen screen in screens)
        {
            body.Append($"\n**{screen.Label}**\n");
            body.Append('\n').Append(screen.Screenshots.Count > 0
                ? "Screenshots: " + string.Join(", ", screen.Screenshots)
                : "No screenshot recorded for this screen.").Append('\n');
            body.Append('\n').Append(screen.AccessibilityAudit.IsNotBlank()
                ? $"Accessibility audit: {screen.AccessibilityAudit}"
                : "Accessibility audit: none reported for this screen.").Append('\n');
            if (screen.Body.IsNotBlank())
            {
                body.Append('\n').Append(screen.Body).Append('\n');
            }
        }
    }

    /// <summary>
    /// The closing offer, present exactly when there is a run skill to honour it with, and
    /// phrased as a question the reviewer answers rather than as something the session then goes
    /// and does. Deliberately not keyed on whether the session drove: a static review is if
    /// anything the one a reviewer most wants to walk in person.
    /// </summary>
    private static void AppendOffer(StringBuilder body, ReviewDriveDecision drive)
    {
        if (!drive.CanOfferToRunItLive)
        {
            return;
        }

        body.Append(
            "\nWould you like this branch run locally so you can walk it yourself? This project has a run "
            + "skill, so the app can be stood up on this pull request's own checkout and handed to you on "
            + "a port. Say the word in your orchestrator window and it happens, with the command and the "
            + "identity it needs at the end of this report (Running this branch locally); nothing starts "
            + "on its own.\n");
    }

    private static string? Value(string line, string marker)
    {
        string trimmed = line.TrimStart();
        return trimmed.StartsWith(marker, StringComparison.OrdinalIgnoreCase)
            ? trimmed[marker.Length..].Trim()
            : null;
    }

    private static bool IsNothing(string value) =>
        value.IsBlank() || string.Equals(value, NothingWord, StringComparison.OrdinalIgnoreCase);

    private static string Trimmed(StringBuilder builder) => builder.ToString().Trim('\n', '\r', ' ', '\t');
}

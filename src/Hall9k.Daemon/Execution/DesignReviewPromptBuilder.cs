using System.Text;
using Hall9k.Connectors.Prompts;
using Hall9k.Connectors.Text;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// The designer persona's review prompt (idea b9b09779, piece 3): one session that reads a pull
/// request for user experience, conformance to the proposed design, motion, CSS practice,
/// accessibility, look and feel, and the project's own design system — and, when this project
/// has driving turned on and a run skill to drive with, stands the product up on the review
/// checkout and walks it.
/// <para>
/// Its own builder rather than another lens inside <see cref="AgentPromptBuilder"/>'s review
/// family, because the one rule that family shares and this session cannot obey is
/// <c>AppendReviewMechanics</c>'s: no builds, no runs, nothing started in the worktree. That rule
/// exists because two engineer lenses share a checkout concurrently and would collide over
/// <c>obj/</c> and <c>bin/</c>; this session runs alone (<c>PrReviewEngine</c> dispatches the
/// plan's sessions one after another) and its whole job on a driving project is to start
/// something. What it does share it borrows rather than restates:
/// <see cref="AgentPromptBuilder.AppendFindingContract"/> and
/// <see cref="AgentPromptBuilder.AppendVerdictContract"/>, so the verdict
/// <c>PrReviewEngine.HasUsableVerdict</c> screens for and the standing run-skill-drift question
/// every review carries are the same words here as everywhere else.
/// </para>
/// </summary>
public static class DesignReviewPromptBuilder
{
    /// <summary>
    /// The package this builder's prose ships under in <c>.claude/templates</c> — and in the
    /// canonical and release-payload copies published from it. Named as a literal in
    /// <c>InstallCommand.ValidateReleasePayload</c>'s own list rather than imported, the same as
    /// the other two Daemon-side builders, because the CLI never references this project.
    /// </summary>
    public const string TemplateDirectory = "design-review-prompt-builder";

    public static string Build(ReviewPersonaPromptRequest request)
    {
        ReviewDriveDecision drive = request.Drive ?? ReviewDriveDecision.NoneFor(ReviewPersona.Designer);
        StringBuilder prompt = new();
        const string file = $"{TemplateDirectory}/build.md";

        prompt.AppendLine(PromptTemplates.Load(file, "title"));
        prompt.AppendLine();
        PromptTemplates.AppendTemplate(prompt, file, "intro");
        prompt.AppendLine();
        PromptTemplates.AppendTemplate(
            prompt, file, "what-you-are-reading",
            new Dictionary<string, string> { ["BaseBranch"] = request.BaseBranch });
        prompt.AppendLine();

        // Before anything the session is asked to judge: what a human told this run to prioritise
        // on a retry, and then the task's own framing. The operator's instruction comes first for
        // the same reason WorkPromptBuilder puts it ahead of the objective — it is the one part of
        // the prompt somebody typed for this particular run — and it reaches the design session at
        // all because `h9k task retry --reason` is as live on a pr-review task as on any other
        // (AgentPromptBuilder.BuildPrReviewLens's own comment).
        WorkPromptBuilder.AppendOperatorGuidanceSection(prompt, request.Task);
        AppendTaskContextSection(prompt, request.Task);
        AppendReferenceSection(prompt);
        AppendDesignSystemSection(prompt);
        AppendDriveSection(prompt, drive, request.RunSkill);
        AppendLensesSection(prompt);
        AppendReportSection(prompt);

        // The same two sections every other review in this platform ends with, borrowed rather
        // than restated (this class's own doc). The foreign-pull-request override is what makes
        // the severity bar, the scope rule and the finding-report wording read as somebody
        // else's open pull request rather than this task's own branch.
        AgentPromptBuilder.ReviewMechanicsOverride mechanics = new(
            request.BaseBranch, GatesObserved: false, DiffIsForeignPullRequest: true);
        AgentPromptBuilder.AppendFindingContract(prompt, request.Project, ReviewMode.Discovery, mechanics);
        AgentPromptBuilder.AppendVerdictContract(prompt, cycle: 1, ReviewMode.Discovery, mechanics);

        prompt.AppendLine();
        // sessionRunsGates: false — this session never runs the project's verification gates.
        // It may well start the product itself, which the "does not run gates" wording allows
        // for in as many words ("in case anything you do run needs it"); what it must not do is
        // reach for a harness background tool, which both variants forbid identically.
        WorkPromptBuilder.AppendForegroundGatesRule(
            prompt, request.CommandTimeout ?? ClaudeSettingsFile.DefaultCommandTimeout, sessionRunsGates: false);
        if (drive.Drives)
        {
            // That rule's own no-host-load half ends with "this session runs nothing itself",
            // which is true of every other read-only leg and not quite true here: a driving
            // review starts the product. Corrected in one sentence rather than by widening the
            // shared fragment, which would change what two unrelated legs are handed.
            PromptTemplates.AppendTemplate(prompt, file, "gates-rule-scope-when-driving");
        }

        prompt.AppendLine();
        PromptTemplates.AppendTemplate(prompt, file, "closing");
        return prompt.ToString();
    }

    /// <summary>
    /// The task's own objective, acceptance criteria and imported agent context — the same three
    /// the engineer's conformance lens is handed on a foreign pull request
    /// (<see cref="AgentPromptBuilder.BuildConformanceReview"/>), and the reason this session's
    /// registry entry declares <c>SeesTaskContext: true</c>.
    /// <para>
    /// It is not optional garnish here: the reference section below tells this session to look for
    /// the proposed design in "this task's own agent context and linked work item" first, and a
    /// pr-review task is where the pull request's own title, body and linked item actually live
    /// (<c>WorkItemContext.Compose</c>). A design prompt that withheld them would send every
    /// session looking for a Figma link it was never shown and the conformance lens would go quiet
    /// on every review (independent pre-PR review, cycle 1, adversarial lens).
    /// </para>
    /// </summary>
    private static void AppendTaskContextSection(StringBuilder prompt, TaskDetails task)
    {
        const string file = $"{TemplateDirectory}/task-context.md";
        prompt.AppendLine(PromptTemplates.Load(file, "heading"));
        prompt.AppendLine();
        PromptTemplates.AppendTemplate(prompt, file, "objective-lead");
        prompt.AppendLine();
        prompt.AppendLine(task.Objective);
        prompt.AppendLine();
        PromptTemplates.AppendTemplate(prompt, file, "objective-body");
        prompt.AppendLine();
        if (task.AcceptanceCriteria.Count > 0)
        {
            prompt.AppendLine(PromptTemplates.Load(file, "acceptance-criteria-heading"));
            foreach (string criterion in task.AcceptanceCriteria)
            {
                prompt.AppendLine($"- {criterion}");
            }

            prompt.AppendLine();
        }

        prompt.AppendLine(PromptTemplates.Load(file, "context-heading"));
        prompt.AppendLine();
        if (task.AgentContext.IsBlank())
        {
            // Said rather than left out. A session handed no Context section at all cannot tell
            // "this task imported nothing" from "the platform decided not to show you", and the
            // difference decides how hard it hunts for the reference in the two other places.
            PromptTemplates.AppendTemplate(prompt, file, "no-context");
            prompt.AppendLine();
            return;
        }

        PromptTemplates.AppendTemplate(prompt, file, "context-lead");
        prompt.AppendLine();
        // Verbatim, unwrapped: WorkItemContext.Compose already fences the imported body it quotes
        // with a run of backticks that body cannot close, so re-fencing the whole context here
        // would nest one quote inside another and hide that boundary rather than draw it. Exactly
        // what BuildConformanceReview does with the same field.
        prompt.AppendLine(task.AgentContext);
        prompt.AppendLine();
        if (task.ExternalReference.IsNotBlank() && WorkItemContext.CarriesQuotedDescription(task.AgentContext))
        {
            PromptTemplates.AppendTemplate(prompt, file, "context-is-data");
            prompt.AppendLine();
        }
    }

    private static void AppendReferenceSection(StringBuilder prompt)
    {
        const string file = $"{TemplateDirectory}/reference.md";
        prompt.AppendLine(PromptTemplates.Load(file, "heading"));
        prompt.AppendLine();
        PromptTemplates.AppendTemplate(prompt, file, "how-to-find-it");
        prompt.AppendLine();
        PromptTemplates.AppendTemplate(
            prompt, file, "report-it",
            new Dictionary<string, string>
            {
                ["ReferenceMarker"] = DesignReviewSection.ReferenceMarker,
                ["NothingWord"] = DesignReviewSection.NothingWord,
            });
        prompt.AppendLine();
        PromptTemplates.AppendTemplate(prompt, file, "no-reference-rule");
        prompt.AppendLine();
    }

    private static void AppendDesignSystemSection(StringBuilder prompt)
    {
        const string file = $"{TemplateDirectory}/design-system.md";
        prompt.AppendLine(PromptTemplates.Load(file, "heading"));
        prompt.AppendLine();
        PromptTemplates.AppendTemplate(prompt, file, "how-to-look");
        prompt.AppendLine();
        PromptTemplates.AppendTemplate(
            prompt, file, "report-it",
            new Dictionary<string, string>
            {
                ["DesignSystemMarker"] = DesignReviewSection.DesignSystemMarker,
                ["NothingWord"] = DesignReviewSection.NothingWord,
            });
        prompt.AppendLine();
        PromptTemplates.AppendTemplate(prompt, file, "cite-the-token");
        prompt.AppendLine();
    }

    /// <summary>
    /// Either the driving half or the static half, never both and never a hedge between them. A
    /// session told "drive if you can" decides for itself whether it could, and the report would
    /// then be describing a choice nobody recorded; the run decided this at dispatch
    /// (<see cref="ReviewDriveDecision"/>) and this section states the decision as a fact.
    /// </summary>
    private static void AppendDriveSection(StringBuilder prompt, ReviewDriveDecision drive, string? runSkill)
    {
        const string file = $"{TemplateDirectory}/drive.md";
        prompt.AppendLine(PromptTemplates.Load(file, "heading"));
        prompt.AppendLine();
        if (!drive.Drives)
        {
            PromptTemplates.AppendTemplate(
                prompt, file, "static-lead",
                new Dictionary<string, string> { ["WhyNotDriven"] = Capitalized(drive.WhyNotDriven) });
            prompt.AppendLine();
            PromptTemplates.AppendTemplate(prompt, file, "static-honesty");
            prompt.AppendLine();
            PromptTemplates.AppendTemplate(prompt, file, "static-accessibility");
            prompt.AppendLine();
            return;
        }

        PromptTemplates.AppendTemplate(prompt, file, "driven-lead");
        prompt.AppendLine();

        // The dispatch recorded that this project had a run skill, and the text is not here. The
        // decision is deliberately never re-resolved (this method's own doc), so the two can
        // disagree when a skill is replaced or removed between the primary session's dispatch and
        // a follow-on session's — and the report will say "driven" either way, off the recorded
        // decision. Said plainly rather than fenced as an empty block under a paragraph promising
        // this project's own account of how it is stood up.
        if (runSkill.IsBlank())
        {
            PromptTemplates.AppendTemplate(prompt, file, "driven-run-skill-missing");
            prompt.AppendLine();
            PromptTemplates.AppendTemplate(prompt, file, "driven-mechanics");
            prompt.AppendLine();
            PromptTemplates.AppendTemplate(prompt, file, "driven-walk");
            prompt.AppendLine();
            PromptTemplates.AppendTemplate(prompt, file, "driven-accessibility");
            prompt.AppendLine();
            AppendDrivenReport(prompt, file);
            return;
        }

        PromptTemplates.AppendTemplate(prompt, file, "driven-run-skill");
        prompt.AppendLine();

        // Fenced rather than inlined: the run skill is another author's document reaching this
        // prompt verbatim, and a heading inside it must not read as a heading of this prompt's
        // own. The fence itself comes from RelayedText.FenceFor rather than being three backticks
        // typed here, for the reason that method exists: a run skill is required to carry
        // copy-pasteable commands, so it routinely holds its own fenced code blocks, and a fixed
        // three-backtick quote would end at the first of them and hand the rest of the document
        // to the session as this prompt's own instructions — the very boundary the fence is for.
        string relayed = (runSkill ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
        string fence = RelayedText.FenceFor(relayed);
        prompt.AppendLine(fence);
        foreach (string line in relayed.Split('\n'))
        {
            prompt.AppendLine(line);
        }

        prompt.AppendLine(fence);
        prompt.AppendLine();
        PromptTemplates.AppendTemplate(prompt, file, "driven-mechanics");
        prompt.AppendLine();
        PromptTemplates.AppendTemplate(prompt, file, "driven-walk");
        prompt.AppendLine();
        PromptTemplates.AppendTemplate(prompt, file, "driven-accessibility");
        prompt.AppendLine();
        AppendDrivenReport(prompt, file);
    }

    /// <summary>The walked-screen grammar, shared by both driving paths so the two cannot drift.</summary>
    private static void AppendDrivenReport(StringBuilder prompt, string file)
    {
        PromptTemplates.AppendTemplate(
            prompt, file, "driven-report",
            new Dictionary<string, string>
            {
                ["DrivenScreenMarker"] = DesignReviewSection.DrivenScreenMarker,
                ["ScreenshotMarker"] = DesignReviewSection.ScreenshotMarker,
                ["AuditMarker"] = DesignReviewSection.AccessibilityAuditMarker,
            });
        prompt.AppendLine();
    }

    private static void AppendLensesSection(StringBuilder prompt)
    {
        const string file = $"{TemplateDirectory}/lenses.md";
        prompt.AppendLine(PromptTemplates.Load(file, "heading"));
        prompt.AppendLine();
        PromptTemplates.AppendTemplate(prompt, file, "intro");
        prompt.AppendLine();
        foreach (DesignReviewLens lens in DesignReviewLens.All)
        {
            PromptTemplates.AppendTemplate(prompt, file, lens.Slug);
            prompt.AppendLine();
        }
    }

    private static void AppendReportSection(StringBuilder prompt)
    {
        const string file = $"{TemplateDirectory}/report.md";
        prompt.AppendLine(PromptTemplates.Load(file, "heading"));
        prompt.AppendLine();
        PromptTemplates.AppendTemplate(
            prompt, file, "shape",
            new Dictionary<string, string>
            {
                ["ReferenceMarker"] = DesignReviewSection.ReferenceMarker,
                ["DesignSystemMarker"] = DesignReviewSection.DesignSystemMarker,
                ["LensMarker"] = DesignReviewSection.LensMarker,
                // Rendered from the vocabulary itself rather than typed into the template, so a
                // lens added to DesignReviewLens cannot reach the report's fixed order while
                // staying absent from the list of slugs a session is told it may use.
                ["LensSlugList"] = string.Join(
                    "\n", DesignReviewLens.All.Select(lens => $"- `{lens.Slug}` — {lens.Heading}")),
            });
        prompt.AppendLine();
        PromptTemplates.AppendTemplate(prompt, file, "platform-writes-it");
        prompt.AppendLine();
    }

    /// <summary>
    /// <see cref="ReviewDriveDecision.WhyNotDriven"/> as the opening of a sentence. Null cannot
    /// reach here — the caller checked <see cref="ReviewDriveDecision.Drives"/> first — but an
    /// empty string still renders as an empty string rather than throwing on an index.
    /// </summary>
    private static string Capitalized(string? clause) =>
        clause.IsBlank() ? string.Empty : char.ToUpperInvariant(clause[0]) + clause[1..];
}

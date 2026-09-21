using System.Text;
using Hall9k.Connectors.Prompts;
using Hall9k.Connectors.Text;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// The QA persona's review of somebody else's already-open pull request (idea b9b09779, piece
/// 2). Its own builder and its own template directory rather than another lens inside
/// <see cref="AgentPromptBuilder"/>: the engineer's two lenses share one skeleton because they
/// are two readings of the same question, and this is a different question — blast radius,
/// coverage, and compliance — that reaches a different conclusion and is allowed to do something
/// the engineer's lenses are expressly forbidden from doing, which is run the suite and, on a
/// project that says so, start the product.
/// <para>
/// Every word of the prose lives in <c>.claude/templates/qa-review-prompt-builder</c> and is
/// loaded through <see cref="PromptTemplates"/>, the same as every other shipped prompt, so an
/// operator edits this review the way they edit the rest and the project override layer of piece
/// 6 reaches it without a second mechanism. What is NOT in the templates is the two contracts
/// the platform itself parses — the finding header and the verdict line — which are appended
/// from <see cref="AgentPromptBuilder"/>'s own shared fragments, so a QA finding is graded,
/// scoped, and screened exactly as any other review's is, and the standing run-skill drift
/// question rides along with them rather than needing a second copy here.
/// </para>
/// </summary>
public static class QaReviewPromptBuilder
{
    /// <summary>
    /// The package this builder's prose ships under in <c>.claude/templates</c> — and in the
    /// canonical and release-payload copies published from it. Named as a literal in
    /// <c>InstallCommand.ValidateReleasePayload</c>'s own list rather than imported, the same as
    /// the other Daemon-side builders, because the CLI never references this project.
    /// </summary>
    public const string TemplateDirectory = "qa-review-prompt-builder";

    /// <summary>
    /// The QA review prompt for one pull request. <see cref="ReviewPersonaPromptRequest.BaseBranch"/>
    /// is the pull request's own base ref, never <c>project.BaseBranch</c>, for the reason
    /// <see cref="AgentPromptBuilder.BuildPrReviewLens"/> states: the two disagree whenever the
    /// reviewed pull request targets something other than the project's default branch, and every
    /// session reviewing this pull request has to name the range it can actually reproduce. A
    /// request carrying no <see cref="ReviewPersonaPromptRequest.Drive"/> renders the no-drive,
    /// no-offer shape — what a caller that never resolved this project's setting should get,
    /// since the alternative is a session told it may launch a product on the strength of a fact
    /// nobody looked up.
    /// </summary>
    public static string Build(ReviewPersonaPromptRequest request)
    {
        ReviewDriveDecision drive = request.Drive ?? ReviewDriveDecision.NoneFor(ReviewPersona.Qa);
        StringBuilder prompt = new();
        AppendOpening(prompt, request.BaseBranch);
        AppendObjective(prompt, request.Task);
        AppendBlastRadius(prompt);
        AppendChecks(prompt, request.Project, request.BaseBranch, drive, request.RunSkill);
        AppendReportShape(prompt);

        // The two sections the platform itself parses, taken whole from the shared contract
        // rather than restated here: a QA finding is read by the identical parser, screened by
        // the identical verdict check (PrReviewEngine.HasUsableVerdict), and carries the same
        // standing run-skill drift question every other review carries — which the finding
        // contract is where it lives (idea b9b09779, piece 1), so this gets it by reusing that
        // section rather than by remembering to add it.
        AgentPromptBuilder.ReviewMechanicsOverride foreignPullRequest =
            new(request.BaseBranch, DiffIsForeignPullRequest: true);
        AgentPromptBuilder.AppendFindingContract(
            prompt, request.Project, ReviewMode.Discovery, foreignPullRequest);
        AgentPromptBuilder.AppendVerdictContract(prompt, cycle: 1, ReviewMode.Discovery, foreignPullRequest);

        AppendRules(prompt, request.Task, request.CommandTimeout);
        AppendClosing(prompt, drive);
        return prompt.ToString();
    }

    private static void AppendOpening(StringBuilder prompt, string baseBranch)
    {
        const string file = $"{TemplateDirectory}/build.md";
        prompt.AppendLine(Fragment(file, "title"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "intro");
        prompt.AppendLine();
        AppendFragment(prompt, file, "range", ("BaseRef", baseBranch));
        prompt.AppendLine();
        AppendFragment(prompt, file, "where-you-are");
        prompt.AppendLine();
        AppendFragment(prompt, file, "gates-not-observed");
    }

    private static void AppendObjective(StringBuilder prompt, TaskDetails task)
    {
        const string file = $"{TemplateDirectory}/objective.md";
        prompt.AppendLine();
        prompt.AppendLine(Fragment(file, "heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "task-intro");
        prompt.AppendLine();
        prompt.AppendLine(task.Objective);
        if (task.AcceptanceCriteria.Count > 0)
        {
            prompt.AppendLine();
            prompt.AppendLine(Fragment(file, "criteria-heading"));
            foreach (string criterion in task.AcceptanceCriteria)
            {
                prompt.AppendLine($"- {criterion}");
            }
        }

        prompt.AppendLine();
        prompt.AppendLine(Fragment(file, "basis-heading"));
        prompt.AppendLine();

        // The pull request's own title and description, plus whatever issue or card was imported
        // with it, arrive as the task's agent context — BuildPrReviewLens's own doc explains why
        // that is the only place a foreign pull request's intent lives. A blank one is a real
        // state of affairs a QA review has to be told about rather than left to infer from an
        // empty section, since the acceptance-criteria check below has nothing to read without it.
        if (task.AgentContext.IsNotBlank())
        {
            AppendFragment(prompt, file, "basis-intro");
            prompt.AppendLine();
            prompt.AppendLine(task.AgentContext);
            prompt.AppendLine();

            // The same boundary the engineer's conformance lens and the design review already
            // draw over the identical field, under the identical condition
            // (AgentPromptBuilder's "adopted-external-item", DesignReviewPromptBuilder's
            // "context-is-data"): the reference alone is not the question, because
            // `h9k task revise --context` replaces a quote with the owner's own words, and a
            // provenance claim over those would demote the person who dispatched the run.
            if (task.ExternalReference.IsNotBlank()
                && WorkItemContext.CarriesQuotedDescription(task.AgentContext))
            {
                AppendFragment(prompt, file, "basis-is-data");
                prompt.AppendLine();
            }

            AppendFragment(prompt, file, "basis-thin");
        }
        else
        {
            AppendFragment(prompt, file, "basis-none");
        }
    }

    private static void AppendBlastRadius(StringBuilder prompt)
    {
        const string file = $"{TemplateDirectory}/blast-radius.md";
        prompt.AppendLine();
        prompt.AppendLine(Fragment(file, "heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "intro");
        prompt.AppendLine();
        AppendFragment(prompt, file, "groups");
        prompt.AppendLine();
        AppendFragment(prompt, file, "plain-language");
        prompt.AppendLine();
        AppendFragment(
            prompt, file, "entry-shape",
            ("MapMarker", ReviewResultParser.BlastRadiusMarker),
            ("EntryTagKey", ReviewResultParser.MapEntryTagKey),
            ("ExampleEntry", ReviewResultParser.ExampleMapEntryPlaceholder),
            ("CoverageTagKey", ReviewResultParser.CoverageTagKey),
            ("CoveredWord", QaCoverageVerdict.Covered.Value));
        prompt.AppendLine();
        AppendFragment(prompt, file, "verdict-heading");
        prompt.AppendLine();
        AppendFragment(
            prompt, file, "verdicts",
            ("CoveredWord", QaCoverageVerdict.Covered.Value),
            ("NewTestWord", QaCoverageVerdict.NewTest.Value),
            ("WalkThroughWord", QaCoverageVerdict.WalkThrough.Value));
        prompt.AppendLine();
        AppendFragment(prompt, file, "no-gap");
    }

    private static void AppendChecks(
        StringBuilder prompt, ProjectDetails project, string baseBranch, ReviewDriveDecision drive,
        string? runSkill)
    {
        const string file = $"{TemplateDirectory}/checks.md";
        prompt.AppendLine();
        prompt.AppendLine(Fragment(file, "tests-heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "tests-intro");
        prompt.AppendLine();
        if (project.VerifyCommands.Count > 0)
        {
            AppendFragment(prompt, file, "tests-gates");
            prompt.AppendLine();
            foreach (VerifyCommand gate in project.VerifyCommands)
            {
                AppendGateLine(prompt, file, gate);
            }
        }
        else
        {
            AppendFragment(prompt, file, "tests-no-gates");
        }

        prompt.AppendLine();
        AppendFragment(
            prompt, file, "tests-report",
            ("EndToEndMarker", ReviewResultParser.EndToEndMarker),
            // A choice placeholder rather than the three real lines the drift question uses, and
            // composed from the words themselves so it cannot drift from them: a session that
            // quotes its instructions and then never answers would otherwise have the last of
            // three echoed marker lines read as an observation nobody made — and the worst of
            // the three to fabricate is "this project has no end-to-end tests".
            ("OutcomeChoices",
                $"<{QaEndToEndOutcome.Pass.Value}|{QaEndToEndOutcome.Fail.Value}|{QaEndToEndOutcome.Absent.Value}>"),
            ("PassWord", QaEndToEndOutcome.Pass.Value),
            ("FailWord", QaEndToEndOutcome.Fail.Value),
            ("AbsentWord", QaEndToEndOutcome.Absent.Value));
        prompt.AppendLine();
        AppendFragment(prompt, file, "tests-evidence", ("BaseRef", baseBranch));

        prompt.AppendLine();
        AppendDriveSection(prompt, file, drive, runSkill);
        prompt.AppendLine();
        AppendConventionChecks(prompt, file, project);
    }

    /// <summary>
    /// One recorded gate's own line. An ordinary gate prints its command, which is the evidence
    /// this section is for; a host-coupled gate prints a sentence in its command's place naming
    /// the one thing this session may still run, the same substitution
    /// <c>WorkPromptBuilder.AppendGateLines</c> and <see cref="AgentPromptBuilder"/>'s own
    /// checklists make (Decisions Log #248 — a host-coupled gate's own command never appears in a
    /// prompt a session might run). It matters more here than anywhere else: this is the one
    /// review session told to run the suite for real, so a bare command printed under "use them
    /// as the starting point" is an instruction to race the daemon's own serialized host gate for
    /// the same container permits, which is #248's origin incident exactly.
    /// <para>
    /// The host-coupled branch is never handed the command as a parameter at all, rather than
    /// handed one its fragment happens not to print: a template edit that introduced a
    /// <c>{{Command}}</c> there would then leave the placeholder standing unsubstituted, which a
    /// reader sees, instead of quietly restoring the leak this closes.
    /// </para>
    /// </summary>
    private static void AppendGateLine(StringBuilder prompt, string file, VerifyCommand gate)
    {
        if (gate.IsHostCoupled)
        {
            AppendFragment(prompt, file, "tests-gate-host-coupled-line", ("Name", gate.Name));
            return;
        }

        AppendFragment(prompt, file, "tests-gate-line", ("Name", gate.Name), ("Command", gate.Command));
    }

    /// <summary>
    /// Whether this session launches the product, and the whole of what it is told either way.
    /// Never a hedge between the two: the run decided this once at dispatch
    /// (<see cref="ReviewDriveDecision"/>), and a session told "drive if you can" would decide
    /// for itself and leave the report describing a choice nobody recorded. The setting on with
    /// no run skill reads as the static branch deliberately, since a session told it may drive
    /// and handed nothing to drive with would go and invent a launch command, which is precisely
    /// the drift the run skill exists to record; the static branch names which of the two
    /// reasons applied rather than asserting the setting was off.
    /// </summary>
    private static void AppendDriveSection(
        StringBuilder prompt, string file, ReviewDriveDecision drive, string? runSkill)
    {
        if (!drive.Drives)
        {
            prompt.AppendLine(Fragment(file, "drive-off-heading"));
            prompt.AppendLine();
            AppendFragment(
                prompt, file, "drive-off",
                ("WhyNotDriven", Capitalized(drive.WhyNotDriven)),
                ("WalkThroughWord", QaCoverageVerdict.WalkThrough.Value),
                ("CoveredWord", QaCoverageVerdict.Covered.Value));
            return;
        }

        prompt.AppendLine(Fragment(file, "drive-on-heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "drive-on-intro");
        prompt.AppendLine();
        AppendRunSkill(prompt, file, runSkill);
        prompt.AppendLine();
        AppendFragment(prompt, file, "drive-on-port");
        prompt.AppendLine();
        AppendFragment(prompt, file, "drive-on-flows");
        prompt.AppendLine();
        AppendFragment(
            prompt, file, "drive-on-report",
            ("DrivenMarker", ReviewResultParser.DrivenMarker),
            ("ExampleDriven", ReviewResultParser.ExampleDrivenFlowPlaceholder));
    }

    /// <summary>
    /// The project's own account of how it is stood up, handed over verbatim rather than pointed
    /// at: it lives on the project's ledger (<see cref="ProjectRunSkillReader"/>), not at a path
    /// this session could open.
    /// <para>
    /// Fenced through <see cref="RelayedText.FenceFor"/> for the reason
    /// <see cref="DesignReviewPromptBuilder"/> states at its own relay: a run skill is required
    /// to carry copy-pasteable commands, so it routinely holds its own fenced blocks, and a fixed
    /// three-backtick quote would end at the first of them and hand the rest of somebody else's
    /// document to the session as this prompt's own instructions.
    /// </para>
    /// <para>
    /// A blank skill here is the dispatch and this session disagreeing: the run recorded that
    /// this project had one, and it was replaced or removed before this session ran. The decision
    /// is deliberately never re-resolved, so that is said plainly rather than fenced as an empty
    /// block under a paragraph promising a procedure.
    /// </para>
    /// </summary>
    private static void AppendRunSkill(StringBuilder prompt, string file, string? runSkill)
    {
        if (runSkill.IsBlank())
        {
            AppendFragment(prompt, file, "drive-on-skill-missing");
            return;
        }

        AppendFragment(prompt, file, "drive-on-skill");
        prompt.AppendLine();
        string relayed = runSkill.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
        string fence = RelayedText.FenceFor(relayed);
        prompt.AppendLine(fence);
        foreach (string line in relayed.Split('\n'))
        {
            prompt.AppendLine(line);
        }

        prompt.AppendLine(fence);
    }

    /// <summary>
    /// <see cref="ReviewDriveDecision.WhyNotDriven"/> as the opening of a sentence, the same way
    /// the design review opens its own static lead. Null cannot reach here (the caller checked
    /// <see cref="ReviewDriveDecision.Drives"/> first), and a blank one renders as nothing rather
    /// than throwing.
    /// </summary>
    private static string Capitalized(string? clause) =>
        clause.IsBlank() ? string.Empty : char.ToUpperInvariant(clause![0]) + clause[1..];

    private static void AppendConventionChecks(StringBuilder prompt, string file, ProjectDetails project)
    {
        prompt.AppendLine(Fragment(file, "conventions-heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "conventions-intro");
        prompt.AppendLine();
        AppendFragment(prompt, file, "conventions-repo");
        prompt.AppendLine();

        // The project's own recorded conventions, quoted rather than pointed at: this session is
        // reading somebody else's checkout, so the repository it is standing in is not necessarily
        // where this project's conventions are written down, and a pointer it cannot resolve
        // reads as no conventions at all.
        AppendFragment(
            prompt, file,
            project.WritingConventions == WritingConventions.Default
                ? "conventions-writing-platform-default"
                : "conventions-writing");
        prompt.AppendLine();
        prompt.AppendLine($"> {project.WritingConventions.Value}");

        prompt.AppendLine();
        AppendFragment(prompt, file, "conventions-decisions");
        prompt.AppendLine();
        AppendFragment(prompt, file, "conventions-criteria");
    }

    private static void AppendReportShape(StringBuilder prompt)
    {
        const string file = $"{TemplateDirectory}/findings-report.md";
        prompt.AppendLine();
        prompt.AppendLine(Fragment(file, "heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "intro");
        prompt.AppendLine();
        AppendFragment(prompt, file, "order");
        prompt.AppendLine();
        AppendFragment(prompt, file, "cite-the-map");
        prompt.AppendLine();
        AppendFragment(prompt, file, "what-is-a-finding");
    }

    private static void AppendRules(StringBuilder prompt, TaskDetails task, TimeSpan? commandTimeout)
    {
        const string file = $"{TemplateDirectory}/rules.md";
        prompt.AppendLine();
        prompt.AppendLine(Fragment(file, "heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "read-only-branch");
        AppendFragment(prompt, file, "never-post");
        AppendFragment(prompt, file, "no-fixing");
        AppendFragment(prompt, file, "clean-up");
        WorkPromptBuilder.AppendExternalInteractionLoggingRule(prompt, task.Id);
        AppendFragment(prompt, file, "foreground-lead");
        // sessionRunsGates: true, unlike every other review leg — this is the one review that is
        // supposed to run the suite, so it gets the wording that tells it the real per-command
        // ceiling rather than the read-only leg's "in case anything you do run needs it".
        WorkPromptBuilder.AppendForegroundGatesRule(
            prompt, commandTimeout ?? ClaudeSettingsFile.DefaultCommandTimeout, sessionRunsGates: true);
    }

    /// <summary>
    /// The offer to run the branch for the reviewer, keyed on the run skill alone
    /// (<see cref="ReviewDriveDecision.CanOfferToRunItLive"/>) and never on whether this session
    /// drove: the offer is a question about what the reviewer might want next, and it is as
    /// answerable after a static review as after a driven one.
    /// </summary>
    private static void AppendClosing(StringBuilder prompt, ReviewDriveDecision drive)
    {
        const string file = $"{TemplateDirectory}/closing.md";
        prompt.AppendLine();
        prompt.AppendLine(Fragment(file, "heading"));
        prompt.AppendLine();
        if (!drive.CanOfferToRunItLive)
        {
            AppendFragment(prompt, file, "no-offer");
            return;
        }

        AppendFragment(prompt, file, "offer");
        prompt.AppendLine();
        AppendFragment(prompt, file, "offer-run-skill");
    }

    private static string Fragment(string file, string name, params (string Key, string Value)[] values) =>
        values.Length == 0
            ? PromptTemplates.Load(file, name)
            : PromptTemplates.Load(file, name, values.ToDictionary(value => value.Key, value => value.Value));

    private static void AppendFragment(
        StringBuilder prompt, string file, string name, params (string Key, string Value)[] values) =>
        PromptTemplates.AppendTemplate(
            prompt, file, name, values.ToDictionary(value => value.Key, value => value.Value));
}

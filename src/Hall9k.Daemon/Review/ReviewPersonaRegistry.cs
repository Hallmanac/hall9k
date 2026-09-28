using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Daemon.Review;

/// <summary>
/// Everything a review session needs that does not depend on which persona is reviewing: the
/// pr-review task, its project, the detached checkout's branch, the pull request's own base ref
/// (never <c>project.BaseBranch</c> — see <c>AgentPromptBuilder.BuildPrReviewLens</c>), and the
/// gate timeout every prompt quotes. Passed whole so a persona's prompt function is a one-argument
/// callable the registry can hold, rather than a signature every future persona has to match by
/// hand.
/// </summary>
/// <param name="Drive">
/// What this run decided about standing the product up for the persona whose prompt is being
/// built (idea b9b09779, pieces 2 and 3) — resolved once at dispatch and recorded on the run stream, so
/// the prompt and the report it produces cannot disagree about whether the session was meant to
/// drive. Null for a persona that never drives (the engineer's two lenses), whose prompt has no
/// seam for it.
/// </param>
/// <param name="RunSkill">
/// This project's run skill, verbatim, when it has one on its ledger and the persona being
/// prompted may drive with it — the only thing that says how this particular project is stood up
/// locally, so a driving session follows it rather than inventing a command. Null whenever
/// <see cref="ReviewDriveDecision.ProjectHasRunSkill"/> is false, which is the same fact read
/// two ways: the decision's boolean is what the run records and the report reads, and this is the
/// text the prompt carries.
/// </param>
public sealed record ReviewPersonaPromptRequest(
    TaskDetails Task, ProjectDetails Project, string Branch, string BaseBranch, TimeSpan? CommandTimeout,
    ReviewDriveDecision? Drive = null, string? RunSkill = null);

/// <summary>
/// One agent session a persona's review is made of. Most personas are one session; the engineer's
/// is two, because today's pull-request review has always been two independent lenses over the
/// same diff (Decisions Log #59).
/// </summary>
/// <param name="Slug">
/// This session's name in the run directory and on the run stream, unique across the whole plan.
/// The engineer's two are <c>adversarial</c> and <c>conformance</c> deliberately: they are the
/// <see cref="ReviewLens"/> slugs the findings files have always been written under
/// (<see cref="Hall9k.Domain.Infrastructure.Storage.RunPaths.ReviewLensFindingsFile"/>), so a
/// pr-review run's artifact layout is unchanged by personas existing.
/// </param>
/// <param name="Heading">This session's own heading inside its persona's section of the findings report.</param>
/// <param name="RoleName">
/// The <see cref="SessionRoleName"/> this session's agent process is launched under. Named by the
/// registry rather than derived from <paramref name="Slug"/> so the engineer's two keep the exact
/// names the interaction rules and the phase line already key on.
/// </param>
/// <param name="SeesTaskContext">
/// Whether this session's own prompt hands it the task's objective, acceptance criteria and agent
/// context — which decides how strictly its verdict is screened, since a reviewer that never saw
/// them cannot be held to naming a finding against them (<c>PrReviewEngine.HasUsableVerdict</c>).
/// A property of the session's prompt, declared here beside the prompt that decides it, rather
/// than inferred from where the session lands in the plan: the engineer's adversarial lens is
/// first and its conformance lens second only because the engineer is the only registered
/// persona, and a session's own screening must not change with what else its assignee declared.
/// </param>
/// <param name="ComposeSection">
/// How this session's raw findings file becomes its part of the report, when the platform rather
/// than the session decides the section's shape (idea b9b09779, piece 3: the design review owes a
/// section per lens in a fixed order, a stated drive state, and an offer whose presence is the
/// platform's call, not the reviewing agent's). Null — every engineer session — means the file is
/// carried into the report verbatim, exactly as a pr-review report has always carried it.
/// </param>
public sealed record ReviewPersonaSession(
    ReviewPersona Persona,
    string Slug,
    string Heading,
    string RoleName,
    bool SeesTaskContext,
    Func<ReviewPersonaPromptRequest, string> BuildPrompt,
    Func<string, ReviewDriveDecision, string>? ComposeSection = null);

/// <summary>
/// One persona's registration: what it reviews for, and the sessions that do the reviewing. A
/// persona with no sessions is declared but not yet buildable — <see cref="IsRegistered"/> is
/// false, and every surface names it as skipped rather than dropping it.
/// </summary>
/// <param name="FailureFailsTheRun">
/// Whether a session of this persona dying takes the whole pr-review run down with it. True for
/// the engineer, which is the review the platform has always run and whose failure has always
/// failed the run; false for an additional persona, whose failure is recorded and named in the
/// report instead, so one persona's bad session never costs the others their findings.
/// </param>
/// <param name="CanDriveTheProduct">
/// Whether this persona's review is one that may stand the project's product up and drive it
/// (idea b9b09779, pieces 2 and 3). True for the designer and for QA; false
/// for the engineer, whose review reads a diff and has never started anything. It is what tells
/// the dispatch to resolve a <see cref="ReviewDriveDecision"/> at all, so a persona that cannot
/// drive never records one and never carries a drive sentence in its report.
/// </param>
public sealed record ReviewPersonaEntry(
    ReviewPersona Persona,
    string Criteria,
    bool FailureFailsTheRun,
    IReadOnlyList<ReviewPersonaSession> Sessions,
    bool CanDriveTheProduct = false)
{
    /// <summary>Whether this persona has a review prompt to dispatch at all.</summary>
    public bool IsRegistered => Sessions.Count > 0;
}

/// <summary>
/// What one pr-review run will actually do, resolved once from the assignee's declared personas.
/// </summary>
/// <param name="Requested">The personas the assignee is reviewed through, in the fixed order — <see cref="ReviewPersona.ForReview"/>.</param>
/// <param name="Ran">Those of <paramref name="Requested"/> the registry has a prompt for.</param>
/// <param name="Skipped">Those it does not, named in the report rather than silently ignored.</param>
/// <param name="FellBackToEngineer">
/// True when <paramref name="Requested"/> held nothing the registry could run and the engineer's
/// review was substituted so the pull request was not left unreviewed. Recorded explicitly, and
/// said plainly in the report, because it is the one case where a review runs that nobody declared.
/// </param>
/// <param name="Sessions">Every session to dispatch, flattened in persona order then session order. The first is the run's own primary session.</param>
/// <param name="DriveDecisions">
/// One entry per persona in <paramref name="Ran"/> that can drive the product, as this run
/// resolved it (idea b9b09779, piece 3). Empty on a plan built with none supplied, which is what
/// every caller that has no project stream to read passes.
/// </param>
/// <param name="ForkSkipped">
/// The subset of <paramref name="Skipped"/> skipped specifically because this review's head sits
/// on a fork (security review idea 6be68ee2, process-injection finding 1), not because the
/// persona has no review prompt registered — see <see cref="Plan"/>'s own <c>isForkHead</c>
/// parameter. Empty for a plan built for a non-fork head.
/// </param>
/// <param name="ForkSkipReason">Why every persona in <paramref name="ForkSkipped"/> was skipped, verbatim. Null exactly when that list is empty.</param>
/// <param name="DocsOnlySkipped">
/// The subset of <paramref name="Skipped"/> skipped specifically because every path this pull
/// request changed matched this project's own non-executable-path set (idea 6be68ee2, phase two)
/// — never because the persona has no review prompt registered. Today this can only ever hold
/// <see cref="ReviewPersona.Security"/>: a diff that touches nothing buildable or testable cannot
/// introduce the classes of defect that persona hunts for. Empty for a run whose diff touched
/// anything outside that set, and for one whose stream predates this skip existing.
/// </param>
/// <param name="DocsOnlySkipReason">Why every persona in <paramref name="DocsOnlySkipped"/> was skipped, verbatim. Null exactly when that list is empty.</param>
public sealed record ReviewPersonaPlan(
    IReadOnlyList<ReviewPersona> Requested,
    IReadOnlyList<ReviewPersona> Ran,
    IReadOnlyList<ReviewPersona> Skipped,
    bool FellBackToEngineer,
    IReadOnlyList<ReviewPersonaSession> Sessions,
    IReadOnlyList<ReviewDriveDecision> DriveDecisions,
    IReadOnlyList<ReviewPersona> ForkSkipped,
    string? ForkSkipReason,
    IReadOnlyList<ReviewPersona> DocsOnlySkipped,
    string? DocsOnlySkipReason)
{
    /// <summary>
    /// What this run decided about <paramref name="persona"/> driving. A persona with nothing
    /// recorded falls back to <see cref="ReviewDriveDecision.NoneFor"/> — the persona's own
    /// default setting and no run skill — which is the honest read of a run that never recorded
    /// one: it cannot have driven, because nothing told it how.
    /// </summary>
    public ReviewDriveDecision DriveFor(ReviewPersona persona) =>
        DriveDecisions.FirstOrDefault(decision => decision.Persona == persona)
        ?? ReviewDriveDecision.NoneFor(persona);
}

/// <summary>
/// The persona registry (idea b9b09779, piece 1): the one place a review persona is mapped to its
/// prompt and its criteria. A pr-review run reads the assignee's declared personas, asks this for
/// a plan, and dispatches exactly what the plan names — so the QA persona's review (piece 2) was
/// an entry here plus its prompt, the designer's (piece 3) was the same, and nothing in the
/// dispatch, the report, or the CLI had to learn about either. The Security persona (idea 6be68ee2,
/// phase two) is the odd one of the four: it is never declared at all, it is folded into
/// <see cref="Plan"/> on top of whatever the assignee declared, gated only by a project's own
/// on/off setting rather than by <see cref="ReviewPersona.Declarable"/>.
/// <para>
/// Every persona in <see cref="ReviewPersona.All"/> has an entry and every one now has sessions, so
/// nothing a member can declare is skipped today. The machinery that says
/// otherwise stays: <see cref="ReviewPersonaEntry.IsRegistered"/>, the plan's own
/// <see cref="ReviewPersonaPlan.Skipped"/> list, and its fall back to the engineer are what make
/// the next persona added to the vocabulary visible rather than silent between the moment it can
/// be declared and the moment its review exists. They are unreachable on today's set by
/// construction, not by accident.
/// </para>
/// </summary>
public static class ReviewPersonaRegistry
{
    private static readonly ReviewPersonaEntry EngineerEntry = new(
        ReviewPersona.Engineer,
        "Code, logic and functionality: is this change correct, and does it do what the pull "
        + "request says it does?",
        FailureFailsTheRun: true,
        [
            new ReviewPersonaSession(
                ReviewPersona.Engineer,
                ReviewLens.Adversarial.Slug,
                "Adversarial (full depth)",
                SessionRoleName.ReviewAdversarial(PrReviewCycle),
                // The adversarial lens is deliberately never handed the task's own objective or
                // acceptance criteria (AgentPromptBuilder.BuildPrReviewLens): it hunts defect
                // classes without being told what the change was supposed to do.
                SeesTaskContext: false,
                request => BuildLens(request, ReviewLens.Adversarial)),
            new ReviewPersonaSession(
                ReviewPersona.Engineer,
                ReviewLens.Conformance.Slug,
                "Conformance (weighted — thin basis reads as context notes, not blockers)",
                SessionRoleName.ReviewConformance(PrReviewCycle),
                SeesTaskContext: true,
                request => BuildLens(request, ReviewLens.Conformance)),
        ]);

    /// <summary>
    /// The QA review's session slug, which is what its findings file on disk is named
    /// (<see cref="Hall9k.Domain.Infrastructure.Storage.RunPaths.ReviewLensFindingsFile"/>) and
    /// what the run stream records it as. A plain persona name rather than a lens name, unlike
    /// the engineer's two: QA is one session and has no second lens to be distinguished from.
    /// </summary>
    public const string QaSlug = "qa";

    private static readonly ReviewPersonaEntry QaEntry = new(
        ReviewPersona.Qa,
        "Compliance and functionality through the lens of blast radius: what changed, what "
        + "adjacent behaviour is owed a regression test, and what new automated end-to-end tests "
        + "this change earns.",
        FailureFailsTheRun: false,
        [
            new ReviewPersonaSession(
                ReviewPersona.Qa,
                QaSlug,
                "QA (blast radius, coverage, and compliance)",
                SessionRoleName.ReviewQa(PrReviewCycle),
                // Unlike the engineer's adversarial lens, this one is handed the task's own
                // objective and the pull request's imported context, and it has to be: one of
                // the four standards it grades against is the acceptance criteria on whatever
                // this pull request is linked to, and that basis arrives nowhere else.
                SeesTaskContext: true,
                QaReviewPromptBuilder.Build),
        ],
        CanDriveTheProduct: true);

    private static readonly ReviewPersonaEntry DesignerEntry = new(
        ReviewPersona.Designer,
        "User experience, conformance to the proposed design, transitions and animations, CSS "
        + "practice, accessibility, and the project's own design system where it has one.",
        FailureFailsTheRun: false,
        [
            new ReviewPersonaSession(
                ReviewPersona.Designer,
                DesignReviewSection.SessionSlug,
                "Design (seven lenses)",
                SessionRoleName.ReviewDesign(PrReviewCycle),
                // Handed the task's own context, unlike the engineer's adversarial lens: the
                // conformance lens here judges the change against a proposed design named on the
                // linked task or issue, and a session that never saw the task cannot find it.
                SeesTaskContext: true,
                DesignReviewPromptBuilder.Build,
                DesignReviewSection.Compose),
        ],
        CanDriveTheProduct: true);

    /// <summary>
    /// The Security review's session slug — a plain persona name, the same reason
    /// <see cref="QaSlug"/> is one: Security is one session and has no second lens to be
    /// distinguished from.
    /// </summary>
    public const string SecuritySlug = "security";

    private static readonly ReviewPersonaEntry SecurityEntry = new(
        ReviewPersona.Security,
        "Injection, secrets handling, authentication and authorization, unsafe process, file, or "
        + "network use, dependency changes, and CI or release workflow changes — whether this "
        + "change introduces a vulnerability into the project, not whether it attacks the host "
        + "(idea 6be68ee2, phase two).",
        FailureFailsTheRun: false,
        [
            new ReviewPersonaSession(
                ReviewPersona.Security,
                SecuritySlug,
                "Security (injection, secrets, auth, unsafe I/O, dependencies, CI/release)",
                SessionRoleName.ReviewSecurity(PrReviewCycle),
                // Unlike QA's and the designer's own sessions, this one is never handed the
                // task's own objective, acceptance criteria, or agent context: it hunts classes
                // of defect over the diff itself and must not be steered by an outsider's own
                // description of what the change is for (security review idea 6be68ee2).
                SeesTaskContext: false,
                SecurityReviewPromptBuilder.Build),
        ],
        // Never drives the product: this lens reads the diff and the files it touches, and has
        // no more reason to stand the project up than the engineer's own two lenses do.
        CanDriveTheProduct: false);

    /// <summary>
    /// A pr-review run never re-reviews — one pass per persona session, no cycle loop — so every
    /// session name and findings file this registry produces reads as cycle 1 always, exactly as
    /// <c>PrReviewEngine</c>'s own dispatch already did before personas existed.
    /// </summary>
    private const int PrReviewCycle = 1;

    /// <summary>This persona's registration. Never null: every persona in the fixed set has one, registered or not.</summary>
    public static ReviewPersonaEntry For(ReviewPersona persona) =>
        persona == ReviewPersona.Qa ? QaEntry
        : persona == ReviewPersona.Designer ? DesignerEntry
        : persona == ReviewPersona.Security ? SecurityEntry
        : EngineerEntry;

    /// <summary>
    /// The reason every fork-skipped persona is named with, in the plan and in the report
    /// (security review idea 6be68ee2, process-injection finding 1) — see <see cref="Plan"/>'s
    /// own <c>isForkHead</c> parameter.
    /// </summary>
    public const string ForkSkipReason =
        "this pull request's head sits on a fork, so nothing this platform runs against it may "
        + "build, test, or drive the product it belongs to — only the engineer's two read-only "
        + "lenses run against the diff";

    /// <summary>
    /// The reason the Security persona is named with when every path this pull request changed
    /// matched this project's own non-executable-path set (idea 6be68ee2, phase two, Decisions Log
    /// #252) — see <see cref="Plan"/>'s own <c>everyChangedPathIsNonExecutable</c> parameter.
    /// </summary>
    public const string DocsOnlySkipReason =
        "every path this pull request changed matched this project's own non-executable-path set, "
        + "so nothing in this diff can introduce the classes of defect this review hunts for";

    /// <summary>
    /// What a run assigned to a member holding <paramref name="declared"/> will do. Declaring
    /// nothing reads as the engineer's review (<see cref="ReviewPersona.ForReview"/>), which is
    /// what keeps this invisible to everyone who never declares a persona.
    /// </summary>
    /// <param name="isForkHead">
    /// Whether this pull request's head sits on a fork of the project's own repository
    /// (<c>PullRequestFacts.IsCrossRepository</c>, security review idea 6be68ee2, process-injection
    /// finding 1) — a head repository can never change once opened, so this is resolved once at
    /// dispatch and never re-read. True skips every persona registered
    /// <see cref="ReviewPersonaEntry.CanDriveTheProduct"/> — QA and the designer today — rather
    /// than running a session against the fork's own code with this project's build or test
    /// tools, or anything else that could execute it. A declaration holding nothing but
    /// fork-skipped personas falls back to the engineer's review below, the same fallback an
    /// unregistered persona already gets, so a fork head is never left unreviewed. The Security
    /// persona is never fork-skipped: it never drives the product
    /// (<see cref="ReviewPersonaEntry.CanDriveTheProduct"/> is false on its entry), so a fork head
    /// leaves it running against the diff exactly as the engineer's two lenses do.
    /// </param>
    /// <param name="securityReviewEnabled">
    /// This project's own security-review setting (<c>h9k project set --security-review</c>,
    /// default on, resolved the <see cref="Hall9k.Domain.Features.Project.AutoPrReviewSetting"/>
    /// way) — whether the Security persona is appended to this plan at all, on top of whatever
    /// the assignee declared. Unlike every other persona, Security is never something a member declares
    /// (<see cref="ReviewPersona.Parse"/> refuses it), so it is folded in here rather than read out
    /// of <paramref name="declared"/>; a <paramref name="declared"/> list that somehow does carry
    /// it is de-duplicated rather than doubled.
    /// </param>
    /// <param name="everyChangedPathIsNonExecutable">
    /// Whether every path this pull request changed matched this project's own non-executable-path
    /// set (idea 6be68ee2, phase two, Decisions Log #252) — a diff that touches nothing buildable
    /// or testable cannot introduce the classes of defect this review hunts for. Has no effect
    /// when <paramref name="securityReviewEnabled"/> is false, since there is nothing to skip
    /// either way.
    /// </param>
    public static ReviewPersonaPlan Plan(
        IEnumerable<ReviewPersona>? declared, IEnumerable<ReviewDriveDecision>? driveDecisions = null,
        bool isForkHead = false, bool securityReviewEnabled = true, bool everyChangedPathIsNonExecutable = false)
    {
        IReadOnlyList<ReviewPersona> declaredRequested =
            ReviewPersona.ForReview(declared?.Where(persona => persona != ReviewPersona.Security));

        // Appended after ForReview's own no-declaration fallback runs, never woven into it: an
        // assignee who declared nothing still reads as "the engineer, plus Security" rather than
        // Security alone masking the ordinary fallback.
        IReadOnlyList<ReviewPersona> requested = securityReviewEnabled
            ? [.. declaredRequested, ReviewPersona.Security]
            : declaredRequested;

        IReadOnlyList<ReviewPersona> forkSkipped = isForkHead
            ? [.. requested.Where(persona => For(persona).IsRegistered && For(persona).CanDriveTheProduct)]
            : [];
        IReadOnlyList<ReviewPersona> docsOnlySkipped = securityReviewEnabled && everyChangedPathIsNonExecutable
            ? [ReviewPersona.Security]
            : [];

        // What the assignee's OWN declared personas actually produced — never Security's own
        // addition, which is not something they declared and must never stand in for it. This is
        // the "did the review they asked for happen" question the fall-back below answers; if
        // Security's own presence in the combined Ran list decided it instead, a fork head
        // declaring only drive-capable personas would read as reviewed (Security ran) while the
        // review the assignee actually asked for silently never happened and nothing ever said so.
        IReadOnlyList<ReviewPersona> declaredRan =
            [.. declaredRequested.Where(persona =>
                For(persona).IsRegistered && !forkSkipped.Contains(persona) && !docsOnlySkipped.Contains(persona))];

        // Nothing the assignee declared can be run yet. Leaving the pull request entirely
        // unreviewed (of the review they asked for) would be the worse outcome: the review that
        // has always run stands in — added alongside Security's own run, never replacing it,
        // since Security running successfully is not itself a reason to skip the engineer's
        // review of the same diff. Never silent — Skipped still names every persona that did not
        // run, and FellBackToEngineer is recorded on the run stream.
        bool fellBack = declaredRan.Count == 0;
        IReadOnlyList<ReviewPersona> coreRan = fellBack ? [ReviewPersona.Engineer] : declaredRan;
        IReadOnlyList<ReviewPersona> securityRan =
            securityReviewEnabled && !docsOnlySkipped.Contains(ReviewPersona.Security)
                ? [ReviewPersona.Security]
                : [];
        IReadOnlyList<ReviewPersona> ran = [.. coreRan, .. securityRan];
        IReadOnlyList<ReviewPersona> skipped = [.. requested.Where(persona => !ran.Contains(persona))];

        return new ReviewPersonaPlan(
            requested, ran, skipped, fellBack, [.. ran.SelectMany(persona => For(persona).Sessions)],
            DrivesOf(ran, driveDecisions), forkSkipped, forkSkipped.Count > 0 ? ForkSkipReason : null,
            docsOnlySkipped, docsOnlySkipped.Count > 0 ? DocsOnlySkipReason : null);
    }

    /// <summary>
    /// The drive decisions this plan keeps: one per persona in <paramref name="ran"/> that can
    /// actually drive, taken from <paramref name="supplied"/> where the caller resolved one and
    /// defaulted to "no run skill, so no driving" where it did not. Filtered by
    /// <see cref="ReviewPersonaEntry.CanDriveTheProduct"/> rather than carried as passed, so a
    /// decision for a persona that does not run, or for one that never drives, cannot reach a
    /// report through a caller's mistake.
    /// </summary>
    private static IReadOnlyList<ReviewDriveDecision> DrivesOf(
        IReadOnlyList<ReviewPersona> ran, IEnumerable<ReviewDriveDecision>? supplied)
    {
        ReviewDriveDecision[] offered = supplied?.ToArray() ?? [];
        return
        [
            .. ran.Where(persona => For(persona).CanDriveTheProduct)
                .Select(persona =>
                    offered.FirstOrDefault(decision => decision.Persona == persona)
                    ?? ReviewDriveDecision.NoneFor(persona)),
        ];
    }

    /// <summary>
    /// The plan a run already recorded at dispatch (<c>PrReviewPersonasSelected</c>), rebuilt so
    /// the engine dispatches and reports exactly what that run set out to do — never a fresh
    /// <see cref="Plan"/> call, which would silently change a live run's shape if a persona were
    /// registered between its dispatch and its completion.
    /// <para>
    /// An empty <paramref name="ran"/> is a run whose stream predates review personas. It ran the
    /// engineer's review, because that was the only review there was, so it plans as one — what
    /// shipped, not a guess about what it meant.
    /// </para>
    /// </summary>
    public static ReviewPersonaPlan Recorded(
        IEnumerable<ReviewPersona>? requested, IEnumerable<ReviewPersona>? ran, IEnumerable<ReviewPersona>? skipped,
        bool fellBackToEngineer, IEnumerable<ReviewDriveDecision>? driveDecisions = null,
        IEnumerable<ReviewPersona>? forkSkipped = null, string? forkSkipReason = null,
        IEnumerable<ReviewPersona>? docsOnlySkipped = null, string? docsOnlySkipReason = null)
    {
        IReadOnlyList<ReviewPersona> recordedRan = ReviewPersona.Declared(ran);
        IReadOnlyList<ReviewPersonaSession> sessions = [.. recordedRan.SelectMany(persona => For(persona).Sessions)];

        // No sessions to rebuild: either the run predates review personas, or every persona it
        // ran has since lost its registration. Both plan as the engineer's review, which is the
        // one entry this registry can never be without, so Sessions is never empty and no caller
        // has to guard a plan with no primary session in it. securityReviewEnabled: false here,
        // deliberately, even though a project's own setting defaults on today: a run whose stream
        // predates review personas entirely did not run the Security persona either, and rebuilding
        // it from the registry's CURRENT defaults would misrepresent a run that already completed
        // — the exact drift this whole method exists to avoid (its own class doc, "never a fresh
        // Plan call").
        if (sessions.Count == 0)
        {
            return Plan(null, securityReviewEnabled: false);
        }

        IReadOnlyList<ReviewPersona> recordedRequested = ReviewPersona.Declared(requested);
        return new ReviewPersonaPlan(
            recordedRequested.Count == 0 ? recordedRan : recordedRequested,
            recordedRan,
            ReviewPersona.Declared(skipped),
            fellBackToEngineer,
            sessions,
            DrivesOf(recordedRan, driveDecisions),
            ReviewPersona.Declared(forkSkipped),
            forkSkipReason,
            ReviewPersona.Declared(docsOnlySkipped),
            docsOnlySkipReason);
    }

    private static string BuildLens(ReviewPersonaPromptRequest request, ReviewLens lens) =>
        AgentPromptBuilder.BuildPrReviewLens(
            request.Task, request.Project, request.Branch, lens, request.BaseBranch, request.CommandTimeout);
}

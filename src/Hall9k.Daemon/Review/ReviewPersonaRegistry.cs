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
/// built (idea b9b09779, piece 3) — resolved once at dispatch and recorded on the run stream, so
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
/// (idea b9b09779, pieces 2 and 3). True for the designer; true for QA once piece 2 lands; false
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
public sealed record ReviewPersonaPlan(
    IReadOnlyList<ReviewPersona> Requested,
    IReadOnlyList<ReviewPersona> Ran,
    IReadOnlyList<ReviewPersona> Skipped,
    bool FellBackToEngineer,
    IReadOnlyList<ReviewPersonaSession> Sessions,
    IReadOnlyList<ReviewDriveDecision> DriveDecisions)
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
/// a plan, and dispatches exactly what the plan names — so adding the QA persona's review (piece 2)
/// or the designer's (piece 3) is an entry here plus its prompt, and nothing in the dispatch, the
/// report, or the CLI has to learn about it.
/// <para>
/// All three personas have an entry; only the engineer has sessions. That asymmetry is the point:
/// the vocabulary a member can declare from is fixed and complete today
/// (<see cref="ReviewPersona.All"/>), while the reviews behind two of the three are still to be
/// built, and a declaration the platform cannot yet honour has to be visible rather than silent.
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

    private static readonly ReviewPersonaEntry QaEntry = new(
        ReviewPersona.Qa,
        "Compliance and functionality through the lens of blast radius: what changed, what "
        + "adjacent behaviour is owed a regression test, and what new automated end-to-end tests "
        + "this change earns.",
        FailureFailsTheRun: false,
        []);

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
    /// A pr-review run never re-reviews — one pass per persona session, no cycle loop — so every
    /// session name and findings file this registry produces reads as cycle 1 always, exactly as
    /// <c>PrReviewEngine</c>'s own dispatch already did before personas existed.
    /// </summary>
    private const int PrReviewCycle = 1;

    /// <summary>This persona's registration. Never null: every persona in the fixed set has one, registered or not.</summary>
    public static ReviewPersonaEntry For(ReviewPersona persona) =>
        persona == ReviewPersona.Qa ? QaEntry
        : persona == ReviewPersona.Designer ? DesignerEntry
        : EngineerEntry;

    /// <summary>
    /// What a run assigned to a member holding <paramref name="declared"/> will do. Declaring
    /// nothing reads as the engineer's review (<see cref="ReviewPersona.ForReview"/>), which is
    /// what keeps this invisible to everyone who never declares a persona.
    /// </summary>
    public static ReviewPersonaPlan Plan(
        IEnumerable<ReviewPersona>? declared, IEnumerable<ReviewDriveDecision>? driveDecisions = null)
    {
        IReadOnlyList<ReviewPersona> requested = ReviewPersona.ForReview(declared);
        IReadOnlyList<ReviewPersona> ran = [.. requested.Where(persona => For(persona).IsRegistered)];
        IReadOnlyList<ReviewPersona> skipped = [.. requested.Where(persona => !For(persona).IsRegistered)];

        // Nothing the assignee declared can be run yet. Leaving the pull request entirely
        // unreviewed would be the literal reading and the worse outcome: the review they asked for
        // does not exist, but the review that has always run does, and the report says plainly
        // that it stood in. Never silent — Skipped still names every persona that did not run, and
        // FellBackToEngineer is recorded on the run stream.
        bool fellBack = ran.Count == 0;
        if (fellBack)
        {
            ran = [ReviewPersona.Engineer];
        }

        return new ReviewPersonaPlan(
            requested, ran, skipped, fellBack, [.. ran.SelectMany(persona => For(persona).Sessions)],
            DrivesOf(ran, driveDecisions));
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
        bool fellBackToEngineer, IEnumerable<ReviewDriveDecision>? driveDecisions = null)
    {
        IReadOnlyList<ReviewPersona> recordedRan = ReviewPersona.Declared(ran);
        IReadOnlyList<ReviewPersonaSession> sessions = [.. recordedRan.SelectMany(persona => For(persona).Sessions)];

        // No sessions to rebuild: either the run predates review personas, or every persona it
        // ran has since lost its registration. Both plan as the engineer's review, which is the
        // one entry this registry can never be without, so Sessions is never empty and no caller
        // has to guard a plan with no primary session in it.
        if (sessions.Count == 0)
        {
            return Plan(null);
        }

        IReadOnlyList<ReviewPersona> recordedRequested = ReviewPersona.Declared(requested);
        return new ReviewPersonaPlan(
            recordedRequested.Count == 0 ? recordedRan : recordedRequested,
            recordedRan,
            ReviewPersona.Declared(skipped),
            fellBackToEngineer,
            sessions,
            DrivesOf(recordedRan, driveDecisions));
    }

    private static string BuildLens(ReviewPersonaPromptRequest request, ReviewLens lens) =>
        AgentPromptBuilder.BuildPrReviewLens(
            request.Task, request.Project, request.Branch, lens, request.BaseBranch, request.CommandTimeout);
}

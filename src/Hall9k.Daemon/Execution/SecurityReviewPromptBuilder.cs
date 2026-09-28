using System.Text;
using Hall9k.Connectors.Prompts;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// The Security persona's review of somebody else's already-open pull request (idea 6be68ee2,
/// phase two): the second of the two-phase security review, asking whether this change introduces
/// a vulnerability into the project rather than whether it attacks the host — the pre-flight
/// (f72ba499) and the membership gate have already answered that question before this session ever
/// starts. Its own builder and its own template directory beside
/// <c>.claude/templates/qa-review-prompt-builder</c>, never the vendor's own built-in
/// <c>/security-review</c> slash command, whose content this platform does not control and cannot
/// version, audit, or override the way it can its own prose.
/// <para>
/// Unlike every other persona registered in <see cref="ReviewPersonaRegistry"/>, this one is never
/// handed the task's own objective, acceptance criteria, or agent context — the pull request's own
/// description arrives nowhere in this prompt at all, deliberately: this session hunts classes of
/// defect over the diff itself and must not be steered by an outsider's own account of what the
/// change is for (<see cref="ReviewPersonaSession.SeesTaskContext"/> is false on its session).
/// </para>
/// </summary>
public static class SecurityReviewPromptBuilder
{
    /// <summary>
    /// The package this builder's prose ships under in <c>.claude/templates</c> — and in the
    /// canonical and release-payload copies published from it. Named as a literal in
    /// <c>InstallCommand.ValidateReleasePayload</c>'s own list rather than imported, the same as
    /// the other Daemon-side builders, because the CLI never references this project.
    /// </summary>
    public const string TemplateDirectory = "security-review-prompt-builder";

    /// <summary>
    /// The Security review prompt for one pull request. <see cref="ReviewPersonaPromptRequest.BaseBranch"/>
    /// is the pull request's own base ref, never <c>project.BaseBranch</c>, for the reason
    /// <see cref="AgentPromptBuilder.BuildPrReviewLens"/> states: the two disagree whenever the
    /// reviewed pull request targets something other than the project's default branch. This
    /// persona never drives the product (<c>ReviewPersonaEntry.CanDriveTheProduct</c> is false on
    /// its entry), so <see cref="ReviewPersonaPromptRequest.Drive"/> and
    /// <see cref="ReviewPersonaPromptRequest.RunSkill"/> are never read here.
    /// </summary>
    public static string Build(ReviewPersonaPromptRequest request)
    {
        StringBuilder prompt = new();
        AppendOpening(prompt, request.BaseBranch);
        AppendLenses(prompt);
        AppendReportShape(prompt);

        // The two sections the platform itself parses, taken whole from the shared contract
        // rather than restated here: a Security finding is read by the identical parser and
        // screened by the identical verdict check (PrReviewEngine.HasUsableVerdict) every other
        // review's findings are.
        AgentPromptBuilder.ReviewMechanicsOverride foreignPullRequest =
            new(request.BaseBranch, DiffIsForeignPullRequest: true);
        AgentPromptBuilder.AppendFindingContract(
            prompt, request.Project, ReviewMode.Discovery, foreignPullRequest);
        AgentPromptBuilder.AppendVerdictContract(prompt, cycle: 1, ReviewMode.Discovery, foreignPullRequest);

        AppendRules(prompt, request.CommandTimeout);
        AppendClosing(prompt);
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

    private static void AppendLenses(StringBuilder prompt)
    {
        const string file = $"{TemplateDirectory}/lenses.md";
        prompt.AppendLine();
        prompt.AppendLine(Fragment(file, "heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "intro");
        prompt.AppendLine();
        AppendFragment(prompt, file, "injection");
        prompt.AppendLine();
        AppendFragment(prompt, file, "secrets");
        prompt.AppendLine();
        AppendFragment(prompt, file, "auth");
        prompt.AppendLine();
        AppendFragment(prompt, file, "unsafe-io");
        prompt.AppendLine();
        AppendFragment(prompt, file, "dependencies");
        prompt.AppendLine();
        AppendFragment(prompt, file, "ci");
    }

    private static void AppendReportShape(StringBuilder prompt)
    {
        const string file = $"{TemplateDirectory}/findings-report.md";
        prompt.AppendLine();
        prompt.AppendLine(Fragment(file, "heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "intro");
        prompt.AppendLine();
        AppendFragment(prompt, file, "what-is-a-finding");
    }

    // No AppendExternalInteractionLoggingRule call here, the same reason QaReviewPromptBuilder
    // carries none: every prompt this builder produces is for a pr-review lens (foreignPullRequest
    // above is unconditional), and that session's own real permission file refuses h9k task
    // log-interaction outright, so the instruction would be dead on arrival.
    private static void AppendRules(StringBuilder prompt, TimeSpan? commandTimeout)
    {
        const string file = $"{TemplateDirectory}/rules.md";
        prompt.AppendLine();
        prompt.AppendLine(Fragment(file, "heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "read-only-branch");
        AppendFragment(prompt, file, "never-post");
        AppendFragment(prompt, file, "no-fixing");
        AppendFragment(prompt, file, "clean-up");
        AppendFragment(prompt, file, "foreground-lead");
        // sessionRunsGates: false — unlike the QA review's own earned exception, this session
        // never runs this project's real gate commands; it reads the diff and the files it
        // touches.
        WorkPromptBuilder.AppendForegroundGatesRule(
            prompt, commandTimeout ?? ClaudeSettingsFile.DefaultCommandTimeout, sessionRunsGates: false);
    }

    /// <summary>This persona never drives the product, so its closing is always the static, no-offer shape.</summary>
    private static void AppendClosing(StringBuilder prompt)
    {
        const string file = $"{TemplateDirectory}/closing.md";
        prompt.AppendLine();
        prompt.AppendLine(Fragment(file, "heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "no-offer");
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

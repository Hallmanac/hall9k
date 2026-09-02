using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Writes the daemon's operating settings into the platform config file (backlog 59) — a durable
/// per-machine record that an autostart-launched daemon (no operator shell to export anything
/// into) reads on its own, and that outranks nothing but an environment variable set for one
/// invocation. Hand-editing <see cref="Hall9kDatabase.ConfigFile"/> works just as well; this
/// command is the guided path, not the only one.
/// </summary>
public sealed class ConfigSetCommand : Hall9kAsyncCommand<ConfigSetCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--max-concurrent-agent-sessions <N>")]
        [Description(
            "Retired as the node's own admission unit by Decisions Log #111 — use --max-concurrent-task-runs "
            + "instead. Still writable and still read as a fallback: when max-concurrent-task-runs is absent at a "
            + "given level (environment variable or config file), this converts (floor(n/2), minimum 1 run).")]
        public int? MaxConcurrentAgentSessions { get; init; }

        [CommandOption("--max-concurrent-task-runs <N>")]
        [Description(
            "How many task runs may be live on this node at once (DaemonOptions.MaxConcurrentTaskRuns, Decisions "
            + "Log #111) — every value is meaningful, unlike the retired --max-concurrent-agent-sessions, where a "
            + "run's two-lens review cycle meant 3 sessions and 2 sessions both admitted exactly one run. Durable "
            + "here, so an autostart-launched daemon (no shell to export it into) still runs at the ceiling this "
            + "machine can actually hold. An interactive claim (h9k task work) occupies zero runs and is never "
            + "counted against this ceiling.")]
        public int? MaxConcurrentTaskRuns { get; init; }

        [CommandOption("--session-cap-per-run <N>")]
        [Description(
            "The global default for how many agent sessions one run may hold simultaneously "
            + "(DaemonOptions.SessionCapPerRun, Decisions Log #111, default 3) — overridable per task at any time, "
            + "even mid-run, with h9k task set-session-cap, which is the one that takes effect at the run's next "
            + "session dispatch without a restart. This flag only changes the global default a running daemon "
            + "reads once at its own startup, so it takes effect on the daemon's next start like every other "
            + "setting here — see the note this command prints after writing. A cap of 1 serializes the run's two "
            + "review lenses instead of dispatching them together, for maximum throttle; today's routine peak is "
            + "2, so anything above 2 is inert headroom until a future coded activity actually overlaps a third "
            + "session.")]
        public int? SessionCapPerRun { get; init; }

        [CommandOption("--default-model <MODEL>")]
        [Description(
            "The platform default every agent session runs on unless a more specific level says otherwise "
            + "(DaemonOptions.DefaultModel, Decisions Log #33) — an exact model id (claude-opus-5, claude-sonnet-5, "
            + "or a context variant like claude-opus-5[[1m]]); anything 'claude -p --model' accepts, except the word "
            + "'default'. 'default' clears the override, so the built-in shipped default decides.")]
        public string? DefaultModel { get; init; }

        [CommandOption("--model-build <MODEL>")]
        [Description("This node's model for the Build role — the session that writes the feature. 'default' clears it.")]
        public string? ModelBuild { get; init; }

        [CommandOption("--model-review <MODEL>")]
        [Description("This node's model for the Review role — the independent reviewer over a run's diff. 'default' clears it.")]
        public string? ModelReview { get; init; }

        [CommandOption("--model-review-verify <MODEL>")]
        [Description(
            "This node's model for a Verify-shape review pass specifically (a middle cycle confirming a fix and "
            + "checking its blast radius, not the first pass or the mandatory final full pass) — a narrower knob "
            + "under --model-review, not a new role. 'default' clears it, falling through to whatever --model-review "
            + "itself resolves to.")]
        public string? ModelReviewVerify { get; init; }

        [CommandOption("--model-fix <MODEL>")]
        [Description("This node's model for the Fix role — the session that applies review findings. 'default' clears it.")]
        public string? ModelFix { get; init; }

        [CommandOption("--model-synthesis <MODEL>")]
        [Description("This node's model for the Synthesis role — condensing a fan-in of blocker handoffs. 'default' clears it.")]
        public string? ModelSynthesis { get; init; }

        [CommandOption("--model-refinement <MODEL>")]
        [Description("This node's model for the (future) Refinement role — draft refinement runs. 'default' clears it.")]
        public string? ModelRefinement { get; init; }

        [CommandOption("--model-publication <MODEL>")]
        [Description("This node's model for the Publication role — writing a task up as an external tracker card. 'default' clears it.")]
        public string? ModelPublication { get; init; }

        [CommandOption("--max-compliance-review-cycles <N>")]
        [Description(
            "This node's cycle cap for the conformance review track (DaemonOptions.MaxComplianceReviewCycles, "
            + "default 3, Decisions Log #63) — how many times the conformance reviewer may be told the same "
            + "thing before the run parks for a human. Resolves task override > project override (h9k project "
            + "set) > this node value > the compiled default (task: the review cycle caps become settable). "
            + "Unlike the project and task levels, there is no 'default' clearing word here — once set, the "
            + "only ways back are re-setting it to the compiled default's own number (3) or hand-editing the "
            + "config file (h9k config show prints its path), the same as --max-concurrent-agent-sessions.")]
        public int? MaxComplianceReviewCycles { get; init; }

        [CommandOption("--max-adversarial-review-cycles <N>")]
        [Description(
            "This node's cycle cap for the adversarial review track (DaemonOptions.MaxAdversarialReviewCycles, "
            + "default 10, Decisions Log #63) — deliberately far larger than the conformance cap, since the "
            + "severity gate, not this counter, is what ends the track in practice. Same resolution order, and "
            + "same lack of a 'default' clearing word, as --max-compliance-review-cycles.")]
        public int? MaxAdversarialReviewCycles { get; init; }

        [CommandOption("--max-final-full-pass-rounds <N>")]
        [Description(
            "This node's cap on consecutive mandatory final-full-pass rounds (DaemonOptions.MaxFinalFullPassRounds, "
            + "default 3, Decisions Log #93) — the independent bound for a track the final pass keeps "
            + "reawakening. Same resolution order, and same lack of a 'default' clearing word, as "
            + "--max-compliance-review-cycles.")]
        public int? MaxFinalFullPassRounds { get; init; }

        [CommandOption("--lifetime-review-cycle-budget <N>")]
        [Description(
            "This node's task-lifetime review-cycle budget (DaemonOptions.LifetimeReviewCycleBudget, default 25) "
            + "— cycles counted across every run and follow-up a task has had, immune to the per-run resets a "
            + "stranding, retry, or follow-up round otherwise gives the three caps above. Generous by design: it "
            + "only catches genuine pathology. Once exceeded, every subsequent settle point parks for a human "
            + "until a human resolution. Same resolution order, and same lack of a 'default' clearing word, as "
            + "--max-compliance-review-cycles.")]
        public int? LifetimeReviewCycleBudget { get; init; }

        [CommandOption("--spend-budget <TOKENS>")]
        [Description(
            "This node's periodic token-spend budget (DaemonOptions.SpendBudgetTokens, backlog: spend-governor "
            + "step three) — once the current period's recorded spend reaches this many input tokens (fresh, "
            + "cache-read and cache-creation combined, the same total TokensRecorded already prices, summed "
            + "across every model), the dispatcher declines to claim further queued work until the period rolls. "
            + "Denominated in tokens, never dollars (Decisions Log #30: the platform holds no price list) — "
            + "calibrate it from h9k config show's own current-period spend line, not from a subscription's "
            + "published hour limits, which shift over time and are not published as token counts. Known v1 "
            + "limitation: the budget gates on the single total across every model, so an Opus token and a Sonnet "
            + "token count identically even though the subscription meters them separately — per-model weighting "
            + "is deliberately not attempted, since it would smuggle in the price list #30 forbids; h9k config "
            + "show's per-model breakdown is what makes a later informed choice possible. Never kills or parks "
            + "running work, and never declines a review or fix session inside a run already claimed — this gates "
            + "claiming a new task only. Pair with --spend-period; omit both to leave dispatch unbudgeted.")]
        public long? SpendBudget { get; init; }

        [CommandOption("--spend-period <day|week>")]
        [Description(
            "The window --spend-budget resets on: day or week (UTC, week starting Monday; default week when a "
            + "budget is set but this is not). Has no observable effect on its own until --spend-budget is also set.")]
        public string? SpendPeriod { get; init; }

        [CommandOption("--interactive-claim-stale-after-days <DAYS>")]
        [Description(
            "How many days an interactive claim (h9k task work) can sit untouched before h9k status nudges "
            + "about it, asking whether it is still yours or ready for h9k task handback (OperatingSettings."
            + "DefaultInteractiveClaimStaleAfterDays, default 3). There is no reclaim to configure — the nudge "
            + "is the whole remedy, never a timeout.")]
        public int? InteractiveClaimStaleAfterDays { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        Validate(settings);

        List<string> changed = [];
        bool created = await PlatformConfigFile.WriteOperatingSettingsAsync(
            operating => Apply(settings, operating, changed), cancellationToken);

        if (created)
        {
            AnsiConsole.MarkupLineInterpolated($"[dim]{Hall9kDatabase.ConfigFile} did not exist — created it with these settings.[/]");
        }

        foreach (string line in changed)
        {
            AnsiConsole.MarkupLineInterpolated($"[green]{line}[/]");
        }

        bool onlyInteractiveClaimStaleAfterDaysChanged =
            settings.InteractiveClaimStaleAfterDays is not null
            && settings.MaxConcurrentAgentSessions is null && settings.MaxConcurrentTaskRuns is null
            && settings.SessionCapPerRun is null && settings.DefaultModel is null
            && settings.ModelBuild is null && settings.ModelReview is null && settings.ModelReviewVerify is null
            && settings.ModelFix is null && settings.ModelSynthesis is null && settings.ModelRefinement is null
            && settings.ModelPublication is null && settings.MaxComplianceReviewCycles is null
            && settings.MaxAdversarialReviewCycles is null && settings.MaxFinalFullPassRounds is null
            && settings.LifetimeReviewCycleBudget is null && settings.SpendBudget is null
            && settings.SpendPeriod is null;

        if (onlyInteractiveClaimStaleAfterDaysChanged)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Written to {Hall9kDatabase.ConfigFile} — h9k status reads this fresh on every render, so it is already in force; there is no daemon restart or environment variable involved.[/]");
        }
        else
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Written to {Hall9kDatabase.ConfigFile} — a running daemon picks this up on its next start (h9k daemon stop, then h9k daemon start); h9k config show prints the effective settings. (--interactive-claim-stale-after-days, if you set it, is already in force — h9k status reads it fresh from the file, with no daemon restart involved.)[/]");
        }

        return ExitCodes.Ok;
    }

    /// <summary>Refuses a no-op call, and a ceiling that would dispatch nothing.</summary>
    internal static void Validate(Settings settings)
    {
        if (settings.MaxConcurrentAgentSessions is null && settings.MaxConcurrentTaskRuns is null
            && settings.SessionCapPerRun is null && settings.DefaultModel is null
            && settings.ModelBuild is null && settings.ModelReview is null && settings.ModelReviewVerify is null
            && settings.ModelFix is null && settings.ModelSynthesis is null && settings.ModelRefinement is null
            && settings.ModelPublication is null && settings.InteractiveClaimStaleAfterDays is null
            && settings.MaxComplianceReviewCycles is null && settings.MaxAdversarialReviewCycles is null
            && settings.MaxFinalFullPassRounds is null && settings.LifetimeReviewCycleBudget is null
            && settings.SpendBudget is null && settings.SpendPeriod is null)
        {
            throw new DomainValidationException(
                "Nothing to change — pass at least one setting, e.g. --max-concurrent-task-runs 2. "
                + "h9k config show prints the current effective settings and where each came from.");
        }

        if (settings.MaxConcurrentAgentSessions is { } ceiling && ceiling < 1)
        {
            throw new DomainValidationException(
                "--max-concurrent-agent-sessions must be at least 1 — a ceiling of zero would dispatch nothing.");
        }

        if (settings.MaxConcurrentTaskRuns is { } maxConcurrentTaskRuns && maxConcurrentTaskRuns < 1)
        {
            throw new DomainValidationException(
                "--max-concurrent-task-runs must be at least 1 — a ceiling of zero would dispatch nothing.");
        }

        if (settings.SessionCapPerRun is { } sessionCapPerRun && sessionCapPerRun < 1)
        {
            throw new DomainValidationException(
                "--session-cap-per-run must be at least 1 — a cap of zero would dispatch nothing for a run's "
                + "next session.");
        }

        if (settings.MaxComplianceReviewCycles is { } complianceCap && complianceCap < 1)
        {
            throw new DomainValidationException("--max-compliance-review-cycles must be at least 1.");
        }

        if (settings.MaxAdversarialReviewCycles is { } adversarialCap && adversarialCap < 1)
        {
            throw new DomainValidationException("--max-adversarial-review-cycles must be at least 1.");
        }

        if (settings.MaxFinalFullPassRounds is { } finalFullPassCap && finalFullPassCap < 1)
        {
            throw new DomainValidationException("--max-final-full-pass-rounds must be at least 1.");
        }

        if (settings.LifetimeReviewCycleBudget is { } lifetimeBudget && lifetimeBudget < 1)
        {
            throw new DomainValidationException("--lifetime-review-cycle-budget must be at least 1.");
        }

        if (settings.SpendBudget is { } spendBudget && spendBudget < 0)
        {
            throw new DomainValidationException(
                "--spend-budget must be at least 0 tokens — a budget of 0 is a legitimate (if extreme) throttle "
                + "that pauses dispatch for the whole period, but a negative one is not a token count.");
        }

        if (settings.SpendPeriod is { } spendPeriod
            && !SpendPeriod.FromInput(spendPeriod).IsWellFormed)
        {
            throw new DomainValidationException("--spend-period must be \"day\" or \"week\".");
        }

        if (settings.InteractiveClaimStaleAfterDays is { } staleAfterDays && staleAfterDays < 1)
        {
            throw new DomainValidationException(
                "--interactive-claim-stale-after-days must be at least 1 — a claim less than a day old is never "
                + "stale.");
        }

        if (settings.InteractiveClaimStaleAfterDays > AttentionComposer.MaxInteractiveClaimStaleAfterDays)
        {
            throw new DomainValidationException(
                $"--interactive-claim-stale-after-days must be at most {AttentionComposer.MaxInteractiveClaimStaleAfterDays} "
                + $"— h9k status clamps any larger value down to {AttentionComposer.MaxInteractiveClaimStaleAfterDays} days "
                + "when it nudges a stale claim, so writing one here would confirm a setting the board would not "
                + "actually honour.");
        }
    }

    /// <summary>The mutation <see cref="PlatformConfigFile.WriteOperatingSettingsAsync"/> runs, isolated for direct testing.</summary>
    internal static void Apply(Settings settings, OperatingSettings operating, List<string> changed)
    {
        if (settings.MaxConcurrentAgentSessions is { } sessions)
        {
            operating.MaxConcurrentAgentSessions = sessions;
            changed.Add($"max-concurrent-agent-sessions = {sessions} (retired — max-concurrent-task-runs decides whenever it is set)");
        }

        if (settings.MaxConcurrentTaskRuns is { } maxConcurrentTaskRuns)
        {
            operating.MaxConcurrentTaskRuns = maxConcurrentTaskRuns;
            changed.Add($"max-concurrent-task-runs = {maxConcurrentTaskRuns}");
        }

        if (settings.SessionCapPerRun is { } sessionCapPerRun)
        {
            operating.SessionCapPerRun = sessionCapPerRun;
            changed.Add($"session-cap-per-run = {sessionCapPerRun}");
        }

        ApplyModel("default-model", settings.DefaultModel, value => operating.DefaultModel = value, changed);
        ApplyModel("model (build)", settings.ModelBuild, value => operating.ModelByRole.Build = value, changed);
        ApplyModel("model (review)", settings.ModelReview, value => operating.ModelByRole.Review = value, changed);
        ApplyModel(
            "model (review-verify)", settings.ModelReviewVerify, value => operating.ModelByRole.ReviewVerify = value, changed);
        ApplyModel("model (fix)", settings.ModelFix, value => operating.ModelByRole.Fix = value, changed);
        ApplyModel("model (synthesis)", settings.ModelSynthesis, value => operating.ModelByRole.Synthesis = value, changed);
        ApplyModel("model (refinement)", settings.ModelRefinement, value => operating.ModelByRole.Refinement = value, changed);
        ApplyModel("model (publication)", settings.ModelPublication, value => operating.ModelByRole.Publication = value, changed);

        if (settings.InteractiveClaimStaleAfterDays is { } staleAfterDays)
        {
            operating.InteractiveClaimStaleAfterDays = staleAfterDays;
            changed.Add($"interactive-claim-stale-after-days = {staleAfterDays}");
        }

        if (settings.MaxComplianceReviewCycles is { } complianceCap)
        {
            operating.MaxComplianceReviewCycles = complianceCap;
            changed.Add($"max-compliance-review-cycles = {complianceCap}");
        }

        if (settings.MaxAdversarialReviewCycles is { } adversarialCap)
        {
            operating.MaxAdversarialReviewCycles = adversarialCap;
            changed.Add($"max-adversarial-review-cycles = {adversarialCap}");
        }

        if (settings.MaxFinalFullPassRounds is { } finalFullPassCap)
        {
            operating.MaxFinalFullPassRounds = finalFullPassCap;
            changed.Add($"max-final-full-pass-rounds = {finalFullPassCap}");
        }

        if (settings.LifetimeReviewCycleBudget is { } lifetimeBudget)
        {
            operating.LifetimeReviewCycleBudget = lifetimeBudget;
            changed.Add($"lifetime-review-cycle-budget = {lifetimeBudget}");
        }

        if (settings.SpendBudget is { } spendBudget)
        {
            operating.SpendBudgetTokens = spendBudget;
            changed.Add($"spend-budget = {spendBudget} tokens");
        }

        if (settings.SpendPeriod is { } spendPeriod)
        {
            operating.SpendPeriod = SpendPeriod.FromInput(spendPeriod).Value;
            changed.Add($"spend-period = {operating.SpendPeriod}");
        }
    }

    /// <summary>
    /// 'default' is Unknown to <see cref="AgentModel.FromInput"/>, which is how the clearing idiom
    /// every other --model option documents (h9k project set --model) applies here with no special case.
    /// Anything else must be spawnable: the same <see cref="AgentModel.IsWellFormed"/> gate
    /// <c>ProjectDecider</c> and <c>TaskDecider</c> apply to a project- or task-level model, because
    /// this value reaches the executor's shell command line the same way theirs do — and this is
    /// the platform-wide bottom of the resolution chain, so an unusable value here breaks every
    /// dispatch on the node rather than one project or task.
    /// </summary>
    private static void ApplyModel(string label, string? input, Action<string?> assign, List<string> changed)
    {
        if (input is null)
        {
            return;
        }

        AgentModel resolved = AgentModel.FromInput(input);
        if (resolved != AgentModel.Unknown && !resolved.IsWellFormed)
        {
            throw new DomainValidationException(
                $"'{resolved.Value}' is not a usable model name. Use a tier alias "
                + $"({AgentModel.Fable}, {AgentModel.Opus}, {AgentModel.Sonnet}, {AgentModel.Haiku}) or an exact "
                + $"model id (for example {AgentModel.PlatformFallback}); letters, digits, and . _ - : / @ [ ] only.");
        }

        string? value = resolved == AgentModel.Unknown ? null : resolved.Value;
        assign(value);
        changed.Add($"{label} = {value ?? "(cleared)"}");
    }
}

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
            "How many agent sessions this node may have resident at once (DaemonOptions.MaxConcurrentAgentSessions, "
            + "Decisions Log #64) — sessions, not runs; a two-lens review cycle means a ceiling of 3 dispatches one "
            + "run at a time. Durable here, so an autostart-launched daemon (no shell to export it into) still runs "
            + "at the ceiling this machine can actually hold.")]
        public int? MaxConcurrentAgentSessions { get; init; }

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

        AnsiConsole.MarkupLineInterpolated(
            $"[dim]Written to {Hall9kDatabase.ConfigFile} — a running daemon picks this up on its next start (h9k daemon stop, then h9k daemon start); h9k config show prints the effective settings.[/]");
        return ExitCodes.Ok;
    }

    /// <summary>Refuses a no-op call, and a ceiling that would dispatch nothing.</summary>
    internal static void Validate(Settings settings)
    {
        if (settings.MaxConcurrentAgentSessions is null && settings.DefaultModel is null
            && settings.ModelBuild is null && settings.ModelReview is null && settings.ModelFix is null
            && settings.ModelSynthesis is null && settings.ModelRefinement is null && settings.ModelPublication is null
            && settings.InteractiveClaimStaleAfterDays is null)
        {
            throw new DomainValidationException(
                "Nothing to change — pass at least one setting, e.g. --max-concurrent-agent-sessions 4. "
                + "h9k config show prints the current effective settings and where each came from.");
        }

        if (settings.MaxConcurrentAgentSessions is { } ceiling && ceiling < 1)
        {
            throw new DomainValidationException(
                "--max-concurrent-agent-sessions must be at least 1 — a ceiling of zero would dispatch nothing.");
        }

        if (settings.InteractiveClaimStaleAfterDays is { } staleAfterDays && staleAfterDays < 1)
        {
            throw new DomainValidationException(
                "--interactive-claim-stale-after-days must be at least 1 — a claim less than a day old is never "
                + "stale.");
        }
    }

    /// <summary>The mutation <see cref="PlatformConfigFile.WriteOperatingSettingsAsync"/> runs, isolated for direct testing.</summary>
    internal static void Apply(Settings settings, OperatingSettings operating, List<string> changed)
    {
        if (settings.MaxConcurrentAgentSessions is { } sessions)
        {
            operating.MaxConcurrentAgentSessions = sessions;
            changed.Add($"max-concurrent-agent-sessions = {sessions}");
        }

        ApplyModel("default-model", settings.DefaultModel, value => operating.DefaultModel = value, changed);
        ApplyModel("model (build)", settings.ModelBuild, value => operating.ModelByRole.Build = value, changed);
        ApplyModel("model (review)", settings.ModelReview, value => operating.ModelByRole.Review = value, changed);
        ApplyModel("model (fix)", settings.ModelFix, value => operating.ModelByRole.Fix = value, changed);
        ApplyModel("model (synthesis)", settings.ModelSynthesis, value => operating.ModelByRole.Synthesis = value, changed);
        ApplyModel("model (refinement)", settings.ModelRefinement, value => operating.ModelByRole.Refinement = value, changed);
        ApplyModel("model (publication)", settings.ModelPublication, value => operating.ModelByRole.Publication = value, changed);

        if (settings.InteractiveClaimStaleAfterDays is { } staleAfterDays)
        {
            operating.InteractiveClaimStaleAfterDays = staleAfterDays;
            changed.Add($"interactive-claim-stale-after-days = {staleAfterDays}");
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

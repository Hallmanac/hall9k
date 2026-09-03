using System.Text.Json;
using System.Text.Json.Serialization;
using Hall9k.Domain.Features.Run;

namespace Hall9k.Domain.Infrastructure.Persistence;

/// <summary>
/// The daemon's durable operating settings that the CLI edits and reports on by name —
/// concurrency and the model-by-role policy (backlog 59, Decisions Log #33's missing bottom
/// layer). Lives in Domain rather than beside <c>Hall9k.Daemon</c>'s own <c>DaemonOptions</c>
/// because both <c>Hall9k.Cli</c> (<c>h9k config set/show</c>) and <c>Hall9k.Daemon</c> (options
/// binding) need the identical shape and the reference graph runs Daemon → Domain, never the
/// other way. <c>DaemonOptions</c> itself binds against the whole "hall9k" section of the
/// platform config file (<see cref="PlatformConfigFile"/>) through the ordinary .NET
/// configuration pipeline, so a sibling member this type does not know about is still
/// bindable by hand-editing the file — this type only names the subset the CLI edits directly.
/// <see cref="Extra"/> is what keeps a hand-edited key like that from being erased the next
/// time the CLI writes: read, mutate the known fields, write back, and everything else round-trips.
/// </summary>
public sealed class OperatingSettings
{
    /// <summary>
    /// Mirrors <c>DaemonOptions.MaxConcurrentAgentSessions</c>'s shipped default, so the two never
    /// drift apart. Retired as the node's own admission unit (Decisions Log #111) — the key is
    /// still read, converted, when <see cref="MaxConcurrentTaskRuns"/> is absent — but this
    /// default is no longer what a fresh install actually dispatches on:
    /// <see cref="DefaultMaxConcurrentTaskRuns"/> is <see cref="ConvertLegacyMaxConcurrentAgentSessions"/>
    /// of this same number, kept in sync by the conversion itself rather than by a second literal.
    /// </summary>
    public const int DefaultMaxConcurrentAgentSessions = 3;

    /// <summary>
    /// The node ceiling's own unit as of Decisions Log #111: how many task runs may be live on
    /// this node at once, replacing the session-denominated <see cref="MaxConcurrentAgentSessions"/>
    /// as the thing an operator actually configures. Every value is meaningful — 1, 2, 3 each
    /// admit one more run than the last — unlike the retired setting, where 3 sessions and 2
    /// sessions both admitted exactly one run once a run's peak session cost was 2. The shipped
    /// default is <see cref="ConvertLegacyMaxConcurrentAgentSessions"/> of
    /// <see cref="DefaultMaxConcurrentAgentSessions"/>, so a fresh install dispatches exactly as
    /// many runs at once as it always did.
    /// </summary>
    public static readonly int DefaultMaxConcurrentTaskRuns =
        ConvertLegacyMaxConcurrentAgentSessions(DefaultMaxConcurrentAgentSessions);

    /// <summary>
    /// The per-run session cap's shipped default (Decisions Log #111, Brian's ruling 2026-08-30):
    /// deliberate headroom above today's routine peak of 2 (the two review lenses) — nothing today
    /// spawns a third concurrent session within one run, so this default changes no dispatch
    /// behavior until a future coded activity actually overlaps a third session. A cap of 1
    /// serializes the two review lenses instead of running them together, for maximum throttle.
    /// </summary>
    public const int DefaultSessionCapPerRun = 3;

    /// <summary>
    /// The floor(n/2)-shaped conversion applied, at each precedence level independently, when only
    /// the retired <see cref="MaxConcurrentAgentSessions"/> key is present (Decisions Log #111): 2
    /// is <see cref="ReviewLens.CycleLenses"/>'s count, the peak sessions one run tree could hold
    /// under the old whole-life reservation — the same derivation
    /// <c>Hall9k.Daemon.Dispatch.NodeLoad</c> used before this decision, computed independently
    /// here because Domain cannot reference the Daemon type that owned that admission math. Never
    /// zero: a session budget smaller than one run's old peak still converts to exactly one run,
    /// the same floor the retired arithmetic already applied.
    /// </summary>
    public static int ConvertLegacyMaxConcurrentAgentSessions(int sessions) =>
        Math.Max(1, sessions / Math.Max(1, ReviewLens.CycleLenses.Count));

    /// <summary>
    /// How many agent sessions one run may hold simultaneously (Decisions Log #111, Brian's ruling
    /// 2026-08-30): a global default, overridable per task from the CLI at any time — including
    /// while the task's run is live (<c>h9k task set-session-cap</c>) — because a change only ever
    /// takes effect at the run's next session dispatch: raising it lets the next phase fan out
    /// wider, and lowering it never terminates a session already running. Effective concurrency
    /// within a run is bounded by coded capability, not by this number alone — today the daemon
    /// knows exactly one overlappable activity, the two review lenses, so a cap above 2 is inert
    /// until a future coded activity actually overlaps a third session.
    /// </summary>
    public int? SessionCapPerRun { get; set; }

    /// <summary>See <see cref="DefaultMaxConcurrentTaskRuns"/>'s own doc for what this replaces.</summary>
    public int? MaxConcurrentTaskRuns { get; set; }

    /// <summary>
    /// How many days an interactive claim (h9k task work) can sit untouched before h9k status
    /// nudges about it (Decisions Log #103's own follow-up, idea 3ba186b6: "a staleness nudge,
    /// not a timeout"). Read directly by the CLI's attention composer rather than through
    /// <see cref="OperatingSettingsResolver"/> — nothing binds it through <c>DaemonOptions</c>,
    /// since no daemon process acts on it (there is deliberately no reclaim, ever), so it carries
    /// no environment-variable tier and no daemon-startup consequence the way the resolved
    /// settings above do.
    /// </summary>
    public const int DefaultInteractiveClaimStaleAfterDays = 3;

    /// <summary>Mirrors <c>DaemonOptions.MaxComplianceReviewCycles</c>'s shipped default (Decisions Log #63).</summary>
    public const int DefaultMaxComplianceReviewCycles = 3;

    /// <summary>Mirrors <c>DaemonOptions.MaxAdversarialReviewCycles</c>'s shipped default (Decisions Log #63).</summary>
    public const int DefaultMaxAdversarialReviewCycles = 10;

    /// <summary>Mirrors <c>DaemonOptions.MaxFinalFullPassRounds</c>'s shipped default (Decisions Log #93).</summary>
    public const int DefaultMaxFinalFullPassRounds = 3;

    /// <summary>
    /// Mirrors <c>DaemonOptions.LifetimeReviewCycleBudget</c>'s shipped default: generous enough
    /// that only genuine pathology — a task ground across strandings, retries, and follow-up
    /// rounds so many times that every per-run cap kept getting a fresh start — ever reaches it
    /// (origin: task b6dfcbe5 reached 52 review cycles across nine generations, 2026-08-30).
    /// </summary>
    public const int DefaultLifetimeReviewCycleBudget = 25;

    public int? MaxConcurrentAgentSessions { get; set; }

    [JsonConverter(typeof(LenientModelStringJsonConverter))]
    public string? DefaultModel { get; set; }

    public RoleModelSettings ModelByRole { get; set; } = new();

    public int? InteractiveClaimStaleAfterDays { get; set; }

    /// <summary>
    /// This node's override of the conformance review track's cycle cap (Decisions Log #63);
    /// null defers to <see cref="DefaultMaxComplianceReviewCycles"/>. Task &gt; project &gt; node &gt;
    /// compiled default is the resolution order every one of these four caps shares.
    /// </summary>
    public int? MaxComplianceReviewCycles { get; set; }

    /// <summary>This node's override of the adversarial review track's cycle cap (Decisions Log #63); null defers to the compiled default.</summary>
    public int? MaxAdversarialReviewCycles { get; set; }

    /// <summary>This node's override of the mandatory final-full-pass round cap (Decisions Log #93); null defers to the compiled default.</summary>
    public int? MaxFinalFullPassRounds { get; set; }

    /// <summary>
    /// This node's override of the task-lifetime review-cycle budget: cycles summed across every
    /// run and follow-up a task has had, immune to the per-run resets a stranding, retry, or
    /// follow-up round would otherwise give it. Null defers to <see cref="DefaultLifetimeReviewCycleBudget"/>.
    /// </summary>
    public int? LifetimeReviewCycleBudget { get; set; }

    /// <summary>
    /// This node's review stage composition (task: the review pipeline's stage composition
    /// becomes configuration recorded per run) — the config-file record <c>h9k config set
    /// --review-stage-composition</c> writes and reads back for <c>h9k config show</c>, and what
    /// an interactive dispatch (<c>Hall9k.Cli.Commands.TaskWorkCommand</c>, no live
    /// <c>DaemonOptions</c> to read) resolves the node level from directly, the same file
    /// <c>DaemonOptions.ReviewStageComposition</c> eventually binds from for a headless dispatch.
    /// Null means unset — <see cref="Hall9k.Domain.Features.Run.ReviewStageCompositionResolver"/>
    /// falls through to the compiled default.
    /// </summary>
    public string? ReviewStageComposition { get; set; }

    /// <summary>
    /// This node's periodic token-spend budget (backlog: spend-governor step three, the
    /// 2026-09-01 architecture review's token-economics findings): once the current period's
    /// recorded spend — every input token <c>TokensRecorded</c> already prices, fresh, cache-read
    /// and cache-creation combined, summed across every model — meets or exceeds this many
    /// tokens, <c>DispatchEngine</c> declines to claim any further queued task until the period
    /// rolls. Null means no budget: dispatch is unbudgeted and behavior is byte-for-byte
    /// unchanged. Denominated in tokens, never dollars — Decisions Log #30 rules the platform
    /// holds no price list, and on a subscription a cost figure is a shadow price, not a bill —
    /// and calibrated from observation (h9k config show's own current-period spend line), not
    /// derived from a subscription's published hour limits, which Anthropic shifts over time and
    /// does not publish as token counts.
    /// <para>
    /// Never kills or parks running work (Decisions Log #11), and never declines a review or fix
    /// session inside a run this node already claimed. It does not distinguish a first claim from
    /// a re-claim, though: a closeout follow-up or <c>h9k task retry</c> reopens its task straight
    /// back to <see cref="Hall9k.Domain.Features.Tasks.TaskState.Queued"/> — or, when the task's
    /// dependency snapshot still names an open blocker (only reachable via a deliberate
    /// start-it-mine claim's own override), to <see cref="Hall9k.Domain.Features.Tasks.TaskState.Blocked"/>
    /// instead (<c>TaskAggregate.Apply(TaskReopened)</c>) — without clearing the assignment, so a
    /// task that does land Queued re-enters <c>DispatchEngine.ClaimEligibleAsync</c>'s identical
    /// claim query and is declined by a spent budget exactly as a brand-new task would be
    /// (independent pre-PR review, cycle 1, adversarial lens — this doc used to promise the
    /// opposite unconditionally).
    /// </para>
    /// <para>
    /// Known v1 limitation: the budget gates on the single total across every model, so an Opus
    /// token and a Sonnet token are counted identically even though the subscription meters them
    /// separately. Per-model weighting is deliberately not attempted — it would smuggle in the
    /// price list Decisions Log #30 forbids the platform from holding — and the per-model spend
    /// breakdown (<c>h9k config show</c>, <c>h9k status</c>) is what makes a later, informed
    /// choice about that trade-off possible, if one is ever made.
    /// </para>
    /// </summary>
    public long? SpendBudgetTokens { get; set; }

    /// <summary>
    /// The window <see cref="SpendBudgetTokens"/> resets on: "day" or "week" (UTC, week starting
    /// Monday). Null defers to <see cref="DefaultSpendPeriod"/>. Meaningless on its own until a
    /// budget is actually set.
    /// </summary>
    public string? SpendPeriod { get; set; }

    /// <summary>
    /// The shipped default for <see cref="SpendPeriod"/>: a week, matching "the weekly allotment"
    /// this setting exists to pace (task objective). Applied whenever a budget is set but no
    /// period is, so <c>h9k config set --spend-budget &lt;n&gt;</c> alone is enough to turn the
    /// throttle on.
    /// </summary>
    public const string DefaultSpendPeriod = "week";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// Per-role model overrides, named rather than held in a dictionary for the same reason
/// <c>DaemonOptions.RoleModelDefaults</c> is: <c>h9k config set --help</c>-shaped discovery
/// states exactly which sessions are configurable, rather than accepting an arbitrary key.
/// </summary>
public sealed class RoleModelSettings
{
    [JsonConverter(typeof(LenientModelStringJsonConverter))]
    public string? Build { get; set; }

    [JsonConverter(typeof(LenientModelStringJsonConverter))]
    public string? Review { get; set; }

    [JsonConverter(typeof(LenientModelStringJsonConverter))]
    public string? Fix { get; set; }

    [JsonConverter(typeof(LenientModelStringJsonConverter))]
    public string? Synthesis { get; set; }

    [JsonConverter(typeof(LenientModelStringJsonConverter))]
    public string? Refinement { get; set; }

    [JsonConverter(typeof(LenientModelStringJsonConverter))]
    public string? Publication { get; set; }

    /// <summary>
    /// The Review role's model for a Verify-shape pass specifically (Brian's ruling, 2026-08-29):
    /// blank falls through to whatever <see cref="Review"/> itself resolves to, so this is not a
    /// seventh role — Verify is still Review-role work — it is a narrower override <see
    /// cref="Hall9k.Daemon.Review.ReviewEngine"/>'s Verify dispatch reads on its own.
    /// </summary>
    [JsonConverter(typeof(LenientModelStringJsonConverter))]
    public string? ReviewVerify { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Every named role and its configured model, in the order <c>h9k config show</c> renders them.</summary>
    public IEnumerable<(string Role, string? Model)> AsPairs()
    {
        yield return (nameof(Build), Build);
        yield return (nameof(Review), Review);
        yield return (nameof(ReviewVerify), ReviewVerify);
        yield return (nameof(Fix), Fix);
        yield return (nameof(Synthesis), Synthesis);
        yield return (nameof(Refinement), Refinement);
        yield return (nameof(Publication), Publication);
    }
}

/// <summary>
/// Reads a model-name leaf exactly as <c>JsonConfigurationFileParser</c> stringifies it before
/// <c>ConfigurationBinder</c> ever sees it: a JSON string as itself, and a JSON number or boolean
/// the same text <see cref="JsonElement.ToString()"/> renders for it ("3", "True"). Without this,
/// a hand-quoted number or boolean here — the same mistake <see cref="OperatingSettings.MaxConcurrentAgentSessions"/>
/// already tolerates in the other direction — is a value the daemon binds and runs on happily, but
/// this type refused as the wrong shape, so <c>h9k config show</c> would report the setting as
/// ignored (falling back to a healthy default) while every session using it actually fails to
/// spawn. An object or array still throws: <c>JsonConfigurationFileParser</c> routes those into
/// nested keys rather than a leaf value, which <see cref="PlatformConfigFile"/>'s existing
/// shape-mismatch recovery already handles correctly for a leaf that stays this type's
/// responsibility. Origin: the cycle-4 pre-PR review found a hand-quoted number for
/// <c>defaultModel</c> or a role under <c>modelByRole</c> reported as merely ignored when the
/// daemon in fact binds and spawns on the coerced value.
/// </summary>
internal sealed class LenientModelStringJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Null => null,
            JsonTokenType.Number => JsonElement.ParseValue(ref reader).ToString(),
            JsonTokenType.True => bool.TrueString,
            JsonTokenType.False => bool.FalseString,
            _ => throw new JsonException("The JSON value could not be converted to System.String."),
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

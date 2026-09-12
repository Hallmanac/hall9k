namespace Hall9k.Daemon.Review;

/// <summary>
/// The mechanical inventory this task's own rule defines a contract token by: any literal string
/// code outside a prompt builder parses, matches, or greps out of an agent session's own output or
/// commits. A template file must never contain one of these literally — see
/// <c>PromptTemplateContractTests</c> — because a template is an operator's to freely edit, and an
/// edited-away marker would silently break the daemon's own parsing rather than merely reading
/// differently to the agent.
/// <para>
/// Aggregated here from each token's own authoritative definition where one is already public or
/// internal (<see cref="ReviewResultParser"/>, <see cref="Hall9k.Connectors.Prompts.HandoffParser"/>,
/// <see cref="Hall9k.Connectors.Prompts.PrSummaryParser"/>); a token still only a private literal on
/// its own parser, or duplicated across two parsers because the two assemblies cannot see each
/// other's internals (<c>ReviewResultParser.ParseFixOutcome</c>'s own inline <c>"RESOLUTION:"</c>
/// versus <see cref="Hall9k.Connectors.Prompts.PrSummaryParser.ResolutionMarker"/>), is restated
/// once more here rather than left absent from the guard this class exists for. Consolidating every
/// parser onto these shared constants directly — so a token is defined exactly once in the whole
/// codebase rather than merely inventoried once here — is left for the run that moves the
/// finding/verdict contract prose itself (<see cref="Hall9k.Daemon.Execution.AgentPromptBuilder"/>'s
/// <c>AppendFindingContract</c>/<c>AppendVerdictContract</c>/<c>AppendReviewMechanics</c>), since
/// that is the first point any of these markers would ever sit beside template prose.
/// </para>
/// </summary>
internal static class PromptContractTokens
{
    /// <summary>Every literal a template file must never contain.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        ReviewResultParser.FindingMarker,
        ReviewResultParser.VerdictMarker,
        ReviewResultParser.ExampleLocationPlaceholder,
        ReviewResultParser.DisagreementMarker,
        ReviewResultParser.ReviewerAskedMarker,
        ReviewResultParser.DisagreementReasoningMarker,
        ReviewResultParser.ProposedReplyMarker,
        ReviewResultParser.ThreadDispositionMarker,
        ReviewResultParser.ThreadIdPlaceholder,
        ReviewResultParser.ThreadDispositionSummaryMarker,
        "RESOLUTION:",
        "merge-ready",
        "needs-fixes",
        // The finding, disagreement, and thread-triage headers' own `key=value` tag grammar
        // (independent pre-PR review, cycle 1, conformance finding): each of these is a bare
        // key ReviewResultParser.Tag reads literally off a header line, so an operator who
        // edits a worked example must not be able to silently retype one. The trailing "=" is
        // appended here rather than folded into the parser's own key constants, which stay
        // bare (what Tag actually compares against) — every template occurrence of the full
        // "key=" spelling goes through the matching {{...TagKey}} placeholder, never a literal.
        ReviewResultParser.SeverityTagKey + "=",
        ReviewResultParser.ScopeTagKey + "=",
        ReviewResultParser.AtTagKey + "=",
        ReviewResultParser.TrackTagKey + "=",
        ReviewResultParser.ThreadTagKey + "=",
        ReviewResultParser.DispositionTagKey + "=",
        ReviewResultParser.KindTagKey + "=",
        ReviewResultParser.AuthorTagKey + "=",
        ReviewResultParser.ReviewTagKey + "=",
        // The finding contract's own structural labels (same review, same finding): distinct from
        // ReviewVerdictValidation's private FindingContractExampleBody, kept in sync by hand across
        // the Execution/Review boundary per that constant's own doc comment, rather than shared.
        "Defect:",
        "Scenario:",
        // "fixed" and "disputed" (the RESOLUTION: value vocabulary) are deliberately not listed:
        // both are ordinary English words a guidance sentence uses constantly with no relation to
        // this grammar, and a substring guard over them would reject nearly every template that
        // ever discusses a fix. The colon-qualified marker itself is the actual two-way contract;
        // its value words are not distinctive enough to police the same way.
        Hall9k.Connectors.Prompts.HandoffParser.Marker,
        Hall9k.Connectors.Prompts.PrSummaryParser.Marker,
        Hall9k.Connectors.Prompts.PrSummaryParser.TitlePrefix,
        // The CLI's own command-name registrations (Hall9k.Cli.Infrastructure.CliCommandTree),
        // matched by Spectre.Console.Cli's router when the agent runs them — not restated from a
        // shared constant because Hall9k.Daemon does not reference Hall9k.Cli (see AGENTS.md's own
        // reference graph). A template teaching a session to run either command still needs the
        // exact command word, so it is injected as a parameter computed in the builder rather than
        // typed into the template file itself.
        "register-session",
        "deliver",
    ];
}

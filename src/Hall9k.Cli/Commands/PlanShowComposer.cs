using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Plan;
using Hall9k.Domain.Features.Plan.Projections;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Storage;
using Spectre.Console;

namespace Hall9k.Cli.Commands;

/// <summary>
/// What <c>h9k plan show</c> read about a plan started from an idea that the plan's record cannot hold: the idea's note, the
/// report file in the workspace and what it says (read now, so a person's edit shows), and the line that resumes the discovery
/// session. The report text is read through <see cref="DiscoveryReport"/> only; nothing of the file's prose is carried here.
/// </summary>
/// <param name="IdeaNote">The idea's note, or null when the idea is not on this node.</param>
/// <param name="ReportPath">The report's path in the idea's workspace now, or null when the workspace path is not this host's.</param>
/// <param name="Reading">What the file reads as, or null when it was not looked at.</param>
/// <param name="ResumeLine">The line to paste to question the discovery session, or null when there is no session to resume.</param>
internal sealed record DiscoveryShowFacts(string? IdeaNote, string? ReportPath, DiscoveryReading? Reading, string? ResumeLine);

/// <summary>
/// What <c>h9k plan show</c> knows beyond the plan's own record: whether the daemon is up, why a live plan is not starting sessions,
/// and, for each draft parked on a proposed split, what its session's report reads as now.
/// </summary>
internal sealed record PlanShowFacts(
    bool DaemonRunning, string? LaunchGate, DiscoveryShowFacts? Discovery = null, IReadOnlyDictionary<Guid, RefinementSplitReading>? Splits = null);

/// <summary>
/// The text of <c>h9k plan show</c>, as Spectre markup lines, composed from the plan's record and the drafts it names. Pure
/// over its inputs so the surface is testable with no database. It prints the plan's state and, for each draft, its phase,
/// round, latest outcome, and where its latest report is (derived from the session's id, which is all the record holds), and
/// for a park it prints the reason, the way forward, and where to read the rest, because the record itself holds nothing a
/// session wrote.
/// </summary>
internal static class PlanShowComposer
{
    public static IReadOnlyList<string> Compose(
        PlanDetails plan, ProjectDetails? project, IReadOnlyDictionary<Guid, TaskDetails> tasks, PlanShowFacts facts, int roundCap)
    {
        string shortId = TaskListCommand.ShortId(plan.Id);
        List<string> lines =
        [
            $"[bold]Plan {shortId}[/] {StateWord(plan)}",
            $"  project: {ExternalText.OneLineMarkup(project?.Name ?? plan.ProjectId.ToString())}",
            $"  started: {plan.StartedAt:u}, at {plan.StartPhase.Word}",
            $"  tracker attestation: {Attestation(plan)}",
        ];
        if (plan.FeedbackStepTaskId is { } feedbackTaskId)
        {
            lines.Add(
                $"  [blue]the feedback step of task {TaskListCommand.ShortId(feedbackTaskId)}[/]: [dim]one challenge round, and the plan ends when "
                + $"the step does (h9k task show {TaskListCommand.ShortId(feedbackTaskId)} says where it stands)[/]");
        }

        if (plan.IdeaId is { } ideaId)
        {
            string note = facts.Discovery?.IdeaNote is { } text
                ? $" {ExternalText.OneLineMarkup(RelayedText.Truncate(text, IdeaNoteWidth))}"
                : string.Empty;
            lines.Insert(2, $"  idea: {TaskListCommand.ShortId(ideaId)}{note}");
            lines.Insert(3, $"  phase: {plan.Phase.Word}");
        }

        if (plan.EndedAt is { } ended)
        {
            string given = plan.CancelReason is { } why
                ? $", reason given: {ExternalText.OneLineMarkup(why)}"
                : plan.StopReason is { } stopWhy
                    ? $", reason given: {ExternalText.OneLineMarkup(stopWhy)}"
                    : string.Empty;
            lines.Add($"  ended: {ended:u}{given}");
        }

        if (plan.IsLive)
        {
            if (!facts.DaemonRunning)
            {
                lines.Add(
                    "  [yellow]waiting for the daemon[/] [dim]— the daemon on this node drives a plan, and it is not running. "
                    + "A plan started while it is stopped waits for it: h9k daemon start[/]");
            }
            else if (facts.LaunchGate is { } gate)
            {
                lines.Add($"  [yellow]no session or challenge starts:[/] {ExternalText.OneLineMarkup(gate)}");
            }
        }

        if (plan.Discovery is not null)
        {
            lines.AddRange(DiscoveryLines(plan, plan.Discovery, facts.Discovery));
        }

        lines.Add(plan.Discovery is not null && plan.Drafts.Count == 0
            ? plan.IsLive
                ? "  drafts: none yet (they are cut when the plan is pursued)"
                : "  drafts: none"
            : "  drafts:");
        foreach (PlanDraftState draft in plan.Drafts)
        {
            lines.AddRange(DraftLines(plan, draft, tasks.GetValueOrDefault(draft.DraftId), roundCap, facts.Splits?.GetValueOrDefault(draft.DraftId)));
        }

        if (plan.IsLive && plan.Drafts.Any(draft => draft.Round > 0))
        {
            lines.Add(
                $"  [dim]The cap in \"round N of M\" is the default of {roundCap} plus any rounds a resume granted; the daemon's "
                + "Hall9k__PlanRoundCap, if it was set, overrides it, and this command cannot read the daemon's options.[/]");
        }

        lines.AddRange(TrackerGapLines(plan, project, tasks));
        if (plan.AtGate)
        {
            lines.Add(string.Empty);
            lines.Add(
                $"[dim]Next:[/] h9k plan pursue {shortId} [dim]cuts the report's proposed tasks as drafts; or[/] h9k plan stop {shortId} --reason \"<why>\" "
                + "[dim]ends it, a normal ending that leaves the idea Captured. h9k plan resume is refused here.[/]");
        }
        else if (plan.IsLive && plan.IsParked && plan.Drafts.Count == 0)
        {
            lines.Add(string.Empty);
            lines.Add(
                $"[dim]Next:[/] h9k plan resume {shortId} [dim]runs discovery again; h9k plan cancel {shortId} --reason \"<why>\" ends the plan[/]");
        }
        else if (plan.IsLive && plan.Drafts.Any(draft => draft.ParkReason == PlanParkReason.SplitProposed))
        {
            lines.Add(string.Empty);
            lines.Add($"[dim]Next:[/] h9k plan split accept {shortId} [dim]cuts the proposed increments as stacked drafts; or record in the draft's "
                + $"journal.md that it stays one task, then[/] h9k plan resume {shortId}[dim]; h9k plan cancel {shortId} --reason \"<why>\" ends the plan[/]");
        }
        else if (plan.IsLive && plan.IsParked)
        {
            lines.Add(string.Empty);
            lines.Add($"[dim]Next:[/] h9k plan resume {shortId} [dim]— or publish or abandon the parked draft by hand, which answers its park; "
                + $"h9k plan cancel {shortId} --reason \"<why>\" ends the plan[/]");
        }

        return lines;
    }

    /// <summary>The width the idea's note is cut to on the idea line, so a long note does not bury the rest.</summary>
    private const int IdeaNoteWidth = 100;

    /// <summary>
    /// Discovery's part of a plan started from an idea: whether a session is running (only for a plan still live; an ended
    /// plan's session was stopped) and where its report will be, the park (a failed session with the reason it failed for, or the gate), and at the gate what the report in the workspace says: pursue or stop, the objectives it
    /// proposes, its path, and the line that resumes the session. A report that is missing or unreadable is said to be, and its
    /// path and the resume line are still printed.
    /// </summary>
    private static IEnumerable<string> DiscoveryLines(PlanDetails plan, PlanDiscoveryState discovery, DiscoveryShowFacts? facts)
    {
        if (plan.IsLive && discovery.RunningSessionId is { } running)
        {
            yield return $"  a discovery session is running; its report will be {RunPaths.DiscoveryReportFile(running).EscapeMarkup()}";
        }

        if (plan.IsLive && discovery.ParkReason.IsParked && plan.Drafts.Count == 0)
        {
            yield return $"  [red]parked:[/] {discovery.ParkReason.Description}";
            yield return $"  [dim]{discovery.ParkReason.WayForward.EscapeMarkup()}[/]";
            if (discovery.ParkReason == PlanParkReason.SessionFailed && discovery.LatestFailure.IsFailure)
            {
                yield return $"  [red]why:[/] {discovery.LatestFailure.Description.EscapeMarkup()}";
            }

            if (discovery.ParkReason == PlanParkReason.SessionFailed && discovery.LatestSessionId is { } failed)
            {
                yield return $"  [dim]read: {RunPaths.DiscoveryReportFile(failed).EscapeMarkup()} (if the session wrote one), and the daemon log[/]";
            }
        }

        if (facts?.Reading is not { } reading)
        {
            yield break;
        }

        yield return "  discovery report:";
        yield return $"    path: {(facts.ReportPath ?? "(the idea's workspace is not on this node)").EscapeMarkup()}";
        if (reading.Missing)
        {
            yield return "    [yellow]the report is not there[/] [dim](the daemon copies it to the idea's workspace when the session ends; if the copy failed, the report is still in the session directory above)[/]";
        }
        else if (reading.Readout is not { } readout)
        {
            yield return "    [yellow]the report is there but not readable[/] [dim](its closing block must hold exactly one DISCOVERY: line naming pursue or stop, "
                + "and for a pursue one or more TASK: lines (a stop has none). Fix the end of the file; h9k plan pursue reads it as it is then)[/]";
        }
        else
        {
            yield return readout.Decision == DiscoveryDecision.Pursue
                ? $"    the report says [green]pursue[/], proposing {readout.Objectives.Count} {(readout.Objectives.Count == 1 ? "task" : "tasks")}:"
                : "    the report says [yellow]stop[/]: it proposes no task";
            foreach (string objective in readout.Objectives)
            {
                yield return $"      - {ExternalText.OneLineMarkup(objective)}";
            }
        }

        if (facts.ResumeLine is { } resume)
        {
            yield return "  to question the discovery session, paste:";
            yield return $"    {resume.EscapeMarkup()}";
        }
    }

    private static IEnumerable<string> DraftLines(
        PlanDetails plan, PlanDraftState draft, TaskDetails? task, int roundCap, RefinementSplitReading? split)
    {
        string id = TaskListCommand.ShortId(draft.DraftId);
        string objective = task is null
            ? "(not on this node)"
            : ExternalText.OneLineMarkup(task.Objective);
        yield return $"    [bold]{id}[/] {objective}";

        string phase = draft switch
        {
            { Published: true } => "published",
            { Abandoned: true } => "abandoned",
            _ => draft.Phase.Word + ReadySuffix(draft),
        };
        string round = draft.Round == 0
            ? "round 0 (no challenge yet)"
            : $"round {draft.Round} of {roundCap + draft.ExtraRounds}";
        yield return $"      phase: {phase}; {round}; latest outcome: {draft.Outcome.Description}";

        if (draft.RunningSessionId is { } running)
        {
            yield return $"      a refinement session is running; its report will be {RunPaths.RefinementReportFile(running).EscapeMarkup()}";
        }
        else if (draft.ChallengeRequestedAt is not null)
        {
            yield return "      a challenge is requested or running; h9k task show says when its verdict lands";
        }

        if (draft.LatestSessionId is { } latest)
        {
            yield return $"      latest report: {ReportFile(draft, latest).EscapeMarkup()}";
        }

        if (draft.IsParked)
        {
            yield return $"      [red]parked:[/] {draft.ParkReason.Description}";
            yield return $"      [dim]{draft.ParkReason.WayForward}[/]";
            foreach (string where in WhereToRead(plan, draft))
            {
                yield return $"      [dim]read: {where.EscapeMarkup()}[/]";
            }

            foreach (string line in SplitLines(draft, split))
            {
                yield return line;
            }
        }
    }

    /// <summary>The increments a draft parked on a proposed split names, in order, as its report reads now; or why it names none.</summary>
    private static IEnumerable<string> SplitLines(PlanDraftState draft, RefinementSplitReading? split)
    {
        if (draft.ParkReason != PlanParkReason.SplitProposed || split is null)
        {
            yield break;
        }

        if (split.Missing)
        {
            yield return "      [yellow]the report is not there[/] [dim](h9k plan split accept reads the increments from it, so restore it or keep the draft as one task)[/]";
            yield break;
        }

        if (split.Increments is not { } increments)
        {
            yield return "      [yellow]the report no longer ends with a split[/] [dim](its closing block must be one split outcome line followed "
                + "directly by two or more increment lines, with no blank line between them; h9k plan split accept reads it as it is then)[/]";
            yield break;
        }

        yield return $"      proposed increments, in order ({increments.Count}):";
        for (int index = 0; index < increments.Count; index++)
        {
            yield return $"        {index + 1}. {ExternalText.OneLineMarkup(increments[index])}";
        }
    }

    /// <summary>The report of the draft's latest session: a challenge's or a refinement session's, as the record says which it was.</summary>
    private static string ReportFile(PlanDraftState draft, Guid session) =>
        draft.LatestReportIsChallenge
            ? RunPaths.ChallengeReportFile(session)
            : RunPaths.RefinementReportFile(session);

    /// <summary>Where the prose behind a park is, since the record holds none of it: the report, the child command's output, or the journal.</summary>
    private static IEnumerable<string> WhereToRead(PlanDetails plan, PlanDraftState draft)
    {
        if (draft.ParkReason == PlanParkReason.PublishRefused)
        {
            yield return RunPaths.PlanPublishOutputFile(plan.Id, draft.DraftId);
            yield break;
        }

        if (draft.LatestSessionId is not { } session)
        {
            yield break;
        }

        if (draft.ParkReason == PlanParkReason.RevisionRefused)
        {
            yield return RunPaths.PlanReviseOutputFile(plan.Id, session);
        }

        yield return ReportFile(draft, session);
        if (draft.ParkReason == PlanParkReason.Question)
        {
            yield return "the draft's journal.md, where the session wrote the question (a project with no home has no journal, and the question is in the report above)";
        }
    }

    private static IEnumerable<string> TrackerGapLines(PlanDetails plan, ProjectDetails? project, IReadOnlyDictionary<Guid, TaskDetails> tasks)
    {
        if (project is null)
        {
            yield break;
        }

        string[] gaps = [.. plan.Drafts
            .Where(draft => draft.Published && tasks.TryGetValue(draft.DraftId, out TaskDetails? task) && TrackerGap.Exists(project.BacklogPolicy, task))
            .Select(draft => TaskListCommand.ShortId(draft.DraftId))];
        if (gaps.Length > 0)
        {
            yield return string.Empty;
            yield return
                $"[yellow]No tracker item[/] exists or is on its way for {string.Join(", ", gaps)}: {project.Name.EscapeMarkup()} tracks its backlog and "
                + "the draft was not published untracked. Link one (h9k task link-jira, link-issue, or link-linear) or ask for one (h9k task push-to-jira).";
        }
    }

    private static string ReadySuffix(PlanDraftState draft) =>
        draft.Ready
            ? " (ready to publish)"
            : string.Empty;

    private static string StateWord(PlanDetails plan) => plan.State.Value switch
    {
        "Live" when plan.AtGate => "[red]live, parked at discovery's gate[/]",
        "Live" when plan.IsParked => "[red]live, parked[/]",
        "Live" => "[green]live[/]",
        "Published" => "[green]published[/]",
        "Cancelled" => "[dim]cancelled[/]",
        "Stopped" => "[green]stopped[/] [dim](a normal ending, not a failure or a cancellation)[/]",
        _ => "[dim]unknown[/]",
    };

    private static string Attestation(PlanDetails plan) =>
        plan.NoExistingItemAttested
            ? "--no-existing-item (given by the person who started the plan)"
            : plan.UntrackedAttested
                ? "--untracked (given by the person who started the plan)"
                : "none given";
}

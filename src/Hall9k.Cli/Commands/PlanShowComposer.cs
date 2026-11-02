using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Plan;
using Hall9k.Domain.Features.Plan.Projections;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Storage;
using Spectre.Console;

namespace Hall9k.Cli.Commands;

/// <summary>What <c>h9k plan show</c> knows beyond the plan's own record: whether the daemon is up, and why a live plan is not starting sessions.</summary>
internal sealed record PlanShowFacts(bool DaemonRunning, string? LaunchGate);

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
        if (plan.EndedAt is { } ended)
        {
            string given = plan.CancelReason is { } why
                ? $", reason given: {ExternalText.OneLineMarkup(why)}"
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

        lines.Add("  drafts:");
        foreach (PlanDraftState draft in plan.Drafts)
        {
            lines.AddRange(DraftLines(plan, draft, tasks.GetValueOrDefault(draft.DraftId), roundCap));
        }

        if (plan.IsLive && plan.Drafts.Any(draft => draft.Round > 0))
        {
            lines.Add(
                $"  [dim]The cap in \"round N of M\" is the default of {roundCap} plus any rounds a resume granted; the daemon's "
                + "Hall9k__PlanRoundCap, if it was set, overrides it, and this command cannot read the daemon's options.[/]");
        }

        lines.AddRange(TrackerGapLines(plan, project, tasks));
        if (plan.IsLive && plan.IsParked)
        {
            lines.Add(string.Empty);
            lines.Add($"[dim]Next:[/] h9k plan resume {shortId} [dim]— or publish or abandon the parked draft by hand, which answers its park; "
                + $"h9k plan cancel {shortId} --reason \"<why>\" ends the plan[/]");
        }

        return lines;
    }

    private static IEnumerable<string> DraftLines(PlanDetails plan, PlanDraftState draft, TaskDetails? task, int roundCap)
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
        "Live" when plan.IsParked => "[red]live, parked[/]",
        "Live" => "[green]live[/]",
        "Published" => "[green]published[/]",
        "Cancelled" => "[dim]cancelled[/]",
        _ => "[dim]unknown[/]",
    };

    private static string Attestation(PlanDetails plan) =>
        plan.NoExistingItemAttested
            ? "--no-existing-item (given by the person who started the plan)"
            : plan.UntrackedAttested
                ? "--untracked (given by the person who started the plan)"
                : "none given";
}

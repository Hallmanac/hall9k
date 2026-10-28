using System.ComponentModel;
using System.Globalization;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Shared.Exceptions;
using JasperFx.Events;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Where a task stands, who moves next, and the command that moves it, from the platform's own
/// records and with no agent call. Read-only on the <see cref="PullRequestThreadsCommand"/> pattern:
/// it takes an <see cref="IQuerySession"/>, so there is no session here that could append, and it
/// writes plain text to a <see cref="TextWriter"/> because an agent's Bash call is not a TTY and
/// Spectre would rewrap and re-parse what it printed.
/// <para>
/// It owns no rules about what to do next. The standing is the row <see cref="TaskStatusComposer"/>
/// composes (the phase from <see cref="TaskPhaseComposer"/>, the lever from
/// <see cref="AttentionComposer"/>), the pre-dispatch step and the Failed exits are
/// <see cref="TaskNextStep"/> (the same function <c>h9k task show</c> prints), and the park choices
/// are <see cref="InteractiveBoundaryChoices"/>. What this command adds is the fold of the run
/// stream those three cannot see: the review phase, the park boundary, and what closeout last
/// recorded.
/// </para>
/// </summary>
public sealed class TaskNextCommand : Hall9kAsyncCommand<TaskNextCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description(
            "Task id (full, or an unambiguous prefix). The same row h9k status shows for it is read "
            + "back as a plain-text answer to what happens next and which command does it")]
        public string Id { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();
        return await RunAsync(session, settings, Console.Out, DateTimeOffset.UtcNow, cancellationToken);
    }

    /// <summary>The whole command body, with the writer handed in so a test reads what was printed.</summary>
    internal static async Task<int> RunAsync(
        IQuerySession session,
        Settings settings,
        TextWriter output,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        TaskDetails details = await session.LoadAsync<TaskDetails>(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        TaskNextFacts facts = await ReadFactsAsync(session, details, now, cancellationToken);

        foreach (string line in Lines(facts))
        {
            await output.WriteLineAsync(line.AsMemory(), cancellationToken);
        }

        return ExitCodes.Ok;
    }

    /// <summary>
    /// Everything the text is composed from, read once: the composed row, whether a dependency is
    /// still open, and, only where the row has a run that can say more, the run folded from its
    /// stream. A teammate's task reads no run at all, because nothing about it is addressed to
    /// this viewer.
    /// </summary>
    private static async Task<TaskNextFacts> ReadFactsAsync(
        IQuerySession session, TaskDetails details, DateTimeOffset now, CancellationToken cancellationToken)
    {
        TaskStatusRow? row = await TaskStatusComposer.ComposeOneAsync(session, details, now, cancellationToken);

        bool hasOpenDependency = false;
        if (details.BlockedBy.Count > 0)
        {
            IReadOnlyList<TaskDependency> dependencies = await TaskDependencyQuery.LoadAsync(
                session, details.BlockedBy, cancellationToken);
            hasOpenDependency = TaskNextStep.HasOpenDependency(details, dependencies);
        }

        TaskListItem? listItem = await session.LoadAsync<TaskListItem>(details.Id, cancellationToken);
        TaskNextFacts facts = new(row, details, hasOpenDependency, listItem?.IsInteractiveClaim ?? false, now);
        if (row is not { IsTeammates: false } || listItem?.CurrentRunId is not { } runId)
        {
            return facts;
        }

        RunAggregate? run = await session.Events.AggregateStreamAsync<RunAggregate>(runId, token: cancellationToken);
        if (run is null)
        {
            return facts;
        }

        RunDetails? recorded = await session.LoadAsync<RunDetails>(runId, cancellationToken);
        TaskNextRun folded = new(
            run.State, run.ReviewPhase, run.ReviewCycle, run.ParkedFromReviewPhase, run.ParkedIsInteractiveGate,
            recorded is not null && OwnersSessionIsActive(recorded));
        CloseoutObservation? closeout = null;
        if (row.State == LifecycleState.Delivered && CloseoutObservations.IsCloseoutState(run.State))
        {
            IReadOnlyList<IEvent> stream = await session.Events.FetchStreamAsync(runId, token: cancellationToken);
            closeout = CloseoutObservations.Last(stream, run.State);
        }

        return facts with { Run = folded, Closeout = closeout };
    }

    /// <summary>
    /// Whether a session the run records as active is the owner's own registration: interactive in
    /// role and named as the run's registered interactive session. A delegated contractor records
    /// under the build session's name, which a registration never takes, so it does not match.
    /// </summary>
    internal static bool OwnersSessionIsActive(RunDetails run) =>
        run.RegisteredInteractiveSessionName is { Length: > 0 } registered
        && run.ActiveSessions.Exists(active =>
            active.Role == AgentRole.Interactive && string.Equals(active.Name, registered, StringComparison.Ordinal));

    /// <summary>
    /// The printed answer, line by line, with no database in it. Phase and attention go through the
    /// same sanitising the board applies (<see cref="ExternalText"/>): the phase detail can quote a
    /// check's name and the cause can quote a reviewer, and neither is Hall9k's own prose.
    /// </summary>
    internal static IReadOnlyList<string> Lines(TaskNextFacts facts)
    {
        TaskDetails details = facts.Details;
        string shortId = TaskListCommand.ShortId(details.Id);
        List<string> lines = [];

        if (facts.Row is not { } row)
        {
            // A task whose lifecycle row could not be composed still has a persisted state worth
            // printing; it says what it read rather than nothing at all.
            lines.Add($"Task {shortId}: {ExternalText.OneLine(details.Objective)}");
            lines.Add($"State: {details.State.Value} (no lifecycle row could be composed)");
            AppendNextStep(lines, details, facts.HasOpenDependency);
            return lines;
        }

        if (row.IsTeammates)
        {
            // Another owner's task: its cause, phase and lever were written in that owner's voice,
            // and every command that would move it guards on them. So no lever is named here.
            lines.Add($"Task {shortId}: {ExternalText.OneLine(row.Objective)}");
            lines.Add(row.TeammateOwner.IsBlank()
                ? "This is a teammate's task, and this node cannot resolve which teammate."
                : $"This is {ExternalText.OneLine(row.TeammateOwner)}'s task (a teammate).");
            lines.Add("Its next move is theirs, so there is no command for you here.");
            return lines;
        }

        lines.Add($"Task {shortId}: {ExternalText.OneLine(row.Objective)}");
        lines.Add($"State: {row.State.Word} {Markup.Remove(TaskShowCommand.StateGloss(row))}");
        if (row.Phase.HasPhase)
        {
            lines.Add($"Now: {Markup.Remove(row.Phase.Markup)}");
        }

        if (row.PullRequestUrl.IsNotBlank())
        {
            lines.Add($"Pull request: {ExternalText.OneLine(row.PullRequestUrl)}");
        }

        (string who, string because) = Mover(facts, row);
        lines.Add($"Moves next: {who}. {because}");

        if (row.Attention.HasCause)
        {
            lines.Add($"{(row.Attention.NeedsYou ? "Needs you" : "Waiting")}: {ExternalText.OneLine(row.Attention.Cause)}");
        }

        if (row.Attention.Lever.IsNotBlank())
        {
            lines.Add($"Lever: {ExternalText.OneLine(row.Attention.Lever)}");
        }

        if (facts.Closeout is { } closeout)
        {
            AppendCloseout(lines, closeout, facts.Now);
        }

        if (facts.Run is { } run)
        {
            AppendReviewStep(lines, run);
            AppendParkChoices(lines, run, details.Id);
        }

        AppendNextStep(lines, details, facts.HasOpenDependency);
        return lines;
    }

    /// <summary>
    /// Who has the next move, read off the composed row: a row asking for the reader is theirs, a
    /// live session on the phase line is a running agent (a delegated contractor is one, since it
    /// is the session the run records), and a pull request waiting on its reviews and checks is
    /// GitHub's. A task still short of a run that only the owner can advance (a draft, a published
    /// task, a failed one) is the owner's, and a claim the owner holds with nothing running is
    /// theirs too; anything else is the platform's own pipeline.
    /// </summary>
    private static (string Who, string Because) Mover(TaskNextFacts facts, TaskStatusRow row)
    {
        if (row.State == LifecycleState.Done || row.State == LifecycleState.Archived)
        {
            return ("nobody", "The task is finished.");
        }

        if (row.State == LifecycleState.HeldElsewhere)
        {
            return ("the platform", "Another node holds this task's claim and carries it on; this node owes it nothing.");
        }

        if (row.Attention.NeedsYou)
        {
            return ("you", "The board has this row asking for you.");
        }

        // The owner's claim while the build is still the run's work: past it, the pipeline owns the
        // worktree (gates, review, closeout), and a session or a gap with no session there is not
        // the owner's to act on. The composers draw the same line (TaskPhaseComposer.Working).
        bool ownersBuild = facts.HoldsInteractiveClaim
            && facts.Run is { } run
            && (run.State == RunState.Dispatched || run.State == RunState.Running);

        if (ownersBuild && facts.Run is { ActiveSessionIsOwners: true })
        {
            return ("you", "Your own session is the one building here; carry on in this worktree.");
        }

        if (row.Phase is { HasPhase: true, Liveness: SessionLiveness.Alive })
        {
            return ("a running agent", "A session is alive on this task; leave its worktree alone until it reports.");
        }

        if (facts.Details.State == TaskState.Draft || facts.Details.State == TaskState.Published
            || facts.Details.State == TaskState.Failed)
        {
            return ("you", "Nothing runs until you take the step below.");
        }

        if (ownersBuild && row.State == LifecycleState.Working)
        {
            return ("you", "You hold the claim and no session is running on it.");
        }

        return row.State == LifecycleState.Delivered && row.RunState == RunState.AwaitingReview
            ? ("GitHub", "The pull request is waiting on its reviews and checks.")
            : ("the platform", "Nothing is asked of you; the pipeline carries it on.");
    }

    private static void AppendNextStep(List<string> lines, TaskDetails details, bool hasOpenDependency)
    {
        foreach (string line in TaskNextStep.Lines(details, hasOpenDependency))
        {
            lines.Add(Markup.Remove(line));
        }
    }

    /// <summary>
    /// What closeout last recorded for an open pull request, and when. What it does about that state
    /// is the phase and attention text above, which the composers already give it; this does not
    /// restate the engine's rules.
    /// </summary>
    private static void AppendCloseout(List<string> lines, CloseoutObservation closeout, DateTimeOffset now)
    {
        string detail = closeout.Detail.IsBlank() ? string.Empty : $": {ExternalText.OneLine(closeout.Detail)}";
        string recorded = closeout.RecordedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        lines.Add(string.Empty);
        lines.Add(
            $"Closeout last recorded {closeout.State.Value} at {recorded} "
            + $"({DurationFormat.Short(now - closeout.RecordedAt)} ago){detail}.");
        lines.Add(
            "Closeout records a change in that state, not every poll, so this is when it last changed, "
            + "not when the pull request was last checked.");
    }

    private static void AppendReviewStep(List<string> lines, TaskNextRun run)
    {
        if (run.State != RunState.UnderReview)
        {
            return;
        }

        lines.Add(string.Empty);
        lines.Add($"Review cycle {run.ReviewCycle.ToString(CultureInfo.InvariantCulture)}: {ReviewStep(run.ReviewPhase)}");
    }

    /// <summary>
    /// The next review step for each place the loop can be, in the words of
    /// <see cref="ReviewPhase"/>'s own documentation. A description of what the pipeline does, never
    /// a command: the commands at a park come from <see cref="InteractiveBoundaryChoices"/>.
    /// </summary>
    internal static string ReviewStep(ReviewPhase phase) => phase switch
    {
        ReviewPhase.None => "no review pass has dispatched yet; the first one starts next.",
        ReviewPhase.AwaitingVerdict => "a review pass is out; its verdict decides whether the work is merge-ready or needs a fix.",
        ReviewPhase.FixNeeded => "the last verdict asked for fixes; a fix session dispatches next.",
        ReviewPhase.VerdictMissing => "a review pass ended without a verdict; it is prompted once more, then the run parks.",
        ReviewPhase.AwaitingFix => "a fix session is working the findings; the gates re-run when it finishes.",
        ReviewPhase.Reverify => "the fix is in; the gates re-run, then a fresh review reads it.",
        ReviewPhase.Settling => "every review track has concluded with nothing left to fix; the loop records how it ended next.",
        ReviewPhase.MergeReady => "the review judged the work merge-ready; the pull request opens next.",
        ReviewPhase.Disputed => "a fix session disputed a finding; the run parks for a human.",
        ReviewPhase.Parked => "the run is parked for a human; the loop is over until someone intervenes.",
        ReviewPhase.AwaitingRebaseRecovery => "a session is resolving a rebase conflict with the base branch before the final pass.",
        ReviewPhase.RebaseRecoveryDisputed => "the rebase conflict could not be decided; the run parks for a human.",
        ReviewPhase.RebaseRecoveryNeeded => "guidance on the rebase conflict was recorded; a fresh recovery session runs next.",
        ReviewPhase.AwaitingSettlingGateRepair => "a repair session is fixing the gate failure the rebase caused.",
        ReviewPhase.SettlingGateRepairCapReached => "the repair rounds are spent; the run parks for a human.",
        ReviewPhase.SettlingGateRepairNeeded => "guidance was recorded; one more repair session runs next.",
        _ => $"the loop is at '{phase}'.",
    };

    /// <summary>
    /// The choices at an interactive park, for the boundary the run parked from. Offered only where
    /// the run is parked at one of interactive mode's own routine gates: any other park (a dispute,
    /// a cap, a budget) is answered by the lever above, and these choices would not apply to it.
    /// </summary>
    private static void AppendParkChoices(List<string> lines, TaskNextRun run, Guid taskId)
    {
        if (run.State != RunState.ReviewParked
            || !run.ParkedIsInteractiveGate
            || InteractiveBoundaryChoices.LeversFor(run.ParkedFromReviewPhase) is not { } levers)
        {
            return;
        }

        lines.Add(string.Empty);
        lines.Add("Your choices at this park:");
        foreach (string choice in InteractiveBoundaryChoices.Lines(levers, taskId))
        {
            lines.Add($"  - {choice}");
        }
    }
}

/// <summary>What <see cref="TaskNextCommand"/> composes its answer from.</summary>
/// <param name="Row">The composed row, null when the task has no lifecycle row.</param>
/// <param name="Details">The task as <c>h9k task show</c> reads it.</param>
/// <param name="HasOpenDependency">Whether a dependency still blocks the claim.</param>
/// <param name="HoldsInteractiveClaim">Whether the task's claim is the owner's own (<c>h9k task drive</c>).</param>
/// <param name="Now">The one clock every elapsed figure measures against.</param>
/// <param name="Run">The current run folded from its stream, null when there is none to read.</param>
/// <param name="Closeout">What closeout last recorded, only for an open pull request.</param>
internal sealed record TaskNextFacts(
    TaskStatusRow? Row,
    TaskDetails Details,
    bool HasOpenDependency,
    bool HoldsInteractiveClaim,
    DateTimeOffset Now,
    TaskNextRun? Run = null,
    CloseoutObservation? Closeout = null);

/// <summary>The parts of a folded run stream the answer reads.</summary>
/// <param name="ActiveSessionIsOwners">
/// Whether the session the run records as active is the owner's own registered one (an interactive
/// session named as <c>RunDetails.RegisteredInteractiveSessionName</c>), as opposed to a delegated
/// contractor, which records under the build session's name.
/// </param>
internal sealed record TaskNextRun(
    RunState State,
    ReviewPhase ReviewPhase,
    int ReviewCycle,
    ReviewPhase ParkedFromReviewPhase,
    bool ParkedIsInteractiveGate,
    bool ActiveSessionIsOwners = false);

/// <summary>The run state closeout last recorded, with what that state carries and when it was recorded.</summary>
internal sealed record CloseoutObservation(RunState State, string Detail, DateTimeOffset RecordedAt);

/// <summary>
/// Reads which run stream event set the state closeout is holding a pull request in. Closeout
/// appends a state-setting event only when the state changes, so the newest such event is the
/// moment the current state began.
/// </summary>
internal static class CloseoutObservations
{
    public static bool IsCloseoutState(RunState state) =>
        state == RunState.AwaitingReview
        || state == RunState.ChecksFailing
        || state == RunState.ReviewPending
        || state == RunState.Conflicting
        || state == RunState.CloseoutParked;

    /// <summary>
    /// The newest event in <paramref name="stream"/> that sets <paramref name="state"/>, or null when
    /// the stream holds none (an older stream, or a state something other than closeout set). The
    /// time is the one the event carries, never <see cref="IEvent.Timestamp"/>: that is when this
    /// node stored the event, which for a replicated or pulled stream is when it arrived here.
    /// </summary>
    public static CloseoutObservation? Last(IReadOnlyList<IEvent> stream, RunState state)
    {
        for (int index = stream.Count - 1; index >= 0; index--)
        {
            if (Carried(stream[index].Data, state) is var (detail, recordedAt))
            {
                return new CloseoutObservation(state, detail, recordedAt);
            }
        }

        return null;
    }

    /// <summary>
    /// What the event carries and when it says it happened, or null when it is not an event that
    /// sets <paramref name="state"/>. An empty detail means it is one and carries nothing to say.
    /// </summary>
    private static (string Detail, DateTimeOffset RecordedAt)? Carried(object data, RunState state) => data switch
    {
        PullRequestOpened opened when state == RunState.AwaitingReview => (string.Empty, opened.OpenedAt),
        PullRequestUpdated updated when state == RunState.AwaitingReview => (string.Empty, updated.UpdatedAt),
        PullRequestChecksFailed failed when state == RunState.ChecksFailing =>
            ($"failing checks {string.Join(", ", failed.FailedChecks)}", failed.ObservedAt),
        ReviewFeedbackReceived feedback when state == RunState.ReviewPending =>
            ($"{feedback.UnresolvedThreadCount.ToString(CultureInfo.InvariantCulture)} unresolved review threads", feedback.ObservedAt),
        ReviewErrored errored when state == RunState.ReviewPending =>
            ($"a review errored ({errored.ReviewUrl})", errored.ObservedAt),
        PullRequestConflictObserved conflict when state == RunState.Conflicting =>
            ("a conflict with the base branch", conflict.ObservedAt),
        CloseoutParked parked when state == RunState.CloseoutParked => (parked.Reason, parked.ParkedAt),
        _ => null,
    };
}

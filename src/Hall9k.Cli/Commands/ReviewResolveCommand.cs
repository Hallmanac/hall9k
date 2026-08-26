using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The human's unpark lever for the pre-PR review loop (Decisions Log #24 deferred it;
/// its absence left a parked run with no path forward except abandonment): record a
/// human verdict on a review-parked run. --merge-ready sends the run on to its pull
/// request; --needs-fixes dispatches a fix session with the stated reason as its
/// findings and, like h9k pr resolve, re-bases the review's per-track cycle caps on the
/// cycle it resolved (log #63's ReviewBudgetBaseCycle) — the human asking is a fresh
/// grant, not one cycle before an immediate re-park. It does not re-open the severity
/// gate, which is a statement about how converged the diff is rather than a budget.
/// <para>
/// One park takes merge-ready differently. The thread-dispute park (Decisions Log #62) is
/// raised before the gates run, so there is no reviewed diff to sign off: the verdict
/// settles the disputed thread, and the run re-enters the pipeline at the gates and the
/// review loop rather than proceeding straight to the pull request. The message says so.
/// </para>
/// <para>
/// A rebase-conflict dispute (backlog 44) refuses merge-ready outright rather than taking it
/// the same way: nothing has been rebased, so there is no sense in which the branch is
/// "ready" — every path forward needs the human's actual resolution, which only
/// --needs-fixes carries. The refusal is scoped to that specific park (the task's follow-up
/// is a rebase AND no review pass has ever run, <see cref="RunAggregate.ReviewCycle"/> still 0)
/// rather than to the task's FollowUpKind alone, which stays Rebase for the rest of the run: an
/// ordinary review park later in that same rebase follow-up — the branch already rebased, the
/// gates already green, at least one review cycle behind it — is exactly the park --merge-ready
/// exists for. ReviewCycle, not <see cref="RunAggregate.ParkedFromState"/>, is what the refusal
/// keys on: ParkedFromState is captured from the run's State at park time, and a resumed dispute
/// that disputes again parks from UnderReview rather than Verifying (the fix session that resumed
/// it already moved State on), so it stops reading as "before the gates" the moment the dispute
/// resumes even though nothing has actually been rebased yet. ReviewCycle carries no such state
/// to go stale — it is untouched by the whole dispute-and-resolve round trip. The outcome message
/// printed below the append, by contrast, still reads <see cref="RunAggregate.ParkedFromState"/>
/// rather than ReviewCycle: it describes what <see cref="RunAggregate.Apply(Hall9k.Domain.Features.Run.Events.ReviewParkResolved)"/>
/// itself will do, and that method's own branch is still keyed on ParkedFromState, stale reads and
/// all. Keying the message on ReviewCycle would make it lie about a second pre-gate dispute's
/// outcome instead of the refusal preventing one; that mismatch inside RunAggregate is a real,
/// recorded gap (backlog 64), not this command's to fix.
/// </para>
/// </summary>
public sealed class ReviewResolveCommand : Hall9kAsyncCommand<ReviewResolveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<TASK>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Task { get; init; } = string.Empty;

        [CommandOption("--merge-ready")]
        [Description(
            "Your verdict: the diff is sound — the run proceeds to open its pull request "
            + "(on a thread-dispute park, which happens before the gates, it re-enters at the "
            + "gates and the review loop instead: your call settled the thread, not the diff. "
            + "Refused on a disputed rebase conflict — nothing has been rebased yet, so use "
            + "--needs-fixes with your resolution instead)")]
        public bool MergeReady { get; init; }

        [CommandOption("--needs-fixes <REASON>")]
        [Description("Your verdict: the stated defects are real — a fix session is dispatched with this reason as its findings")]
        public string? NeedsFixes { get; init; }

        [CommandOption("--reason <TEXT>")]
        [Description(
            "Why the diff is sound despite the finding — e.g. the evidence that dismissed it. Only valid "
            + "with --merge-ready (--needs-fixes already takes its reason as its own argument). Recorded on "
            + "the task so a later fresh-context review pass is told this was already settled, rather than "
            + "re-raising the same question — except on a thread-dispute park, which settles a disputed "
            + "thread rather than a review finding and is not carried forward this way "
            + "(PLAN.md log #24, task: review prompts carry prior rulings).")]
        public string? Reason { get; init; }

        public override ValidationResult Validate()
        {
            if (MergeReady == NeedsFixes.IsNotBlank())
            {
                return ValidationResult.Error(
                    "Pass exactly one verdict: --merge-ready, or --needs-fixes <reason>. " +
                    "The park exists because the platform refused to guess; this command records " +
                    "YOUR judgment (PLAN.md log #24).");
            }

            return Reason.IsNotBlank() && NeedsFixes.IsNotBlank()
                ? ValidationResult.Error(
                    "Pass --reason only with --merge-ready; --needs-fixes already takes its reason as its " +
                    "own argument.")
                : ValidationResult.Success();
        }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Task, cancellationToken);
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        Guid runId = task.CurrentRunId
            ?? throw new DomainConflictException(
                $"Task {taskId} has no current run — nothing is review-parked here.");

        // Fence before aggregating (the h9k pr resolve manner): the append below carries
        // expectedVersion so a resolve racing the daemon (or a duplicate invocation)
        // loses loudly instead of stacking a second verdict.
        StreamState? fence = await session.Events.FetchStreamStateAsync(runId, cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId}'s run {runId} has no run stream.");
        RunAggregate run = await session.Events.AggregateStreamAsync<RunAggregate>(
                runId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId}'s run {runId} has no run stream.");

        if (run.State != RunState.ReviewParked)
        {
            throw new DomainConflictException(
                $"Task {taskId}'s current run is {run.State.Value}, not ReviewParked — only a " +
                "review-parked run takes a human verdict. (A parked pull request is resolved " +
                "with h9k pr resolve instead.)");
        }

        if (settings.MergeReady && task.FollowUpKind == FollowUpKind.Rebase
            && run.ReviewCycle == 0)
        {
            throw new DomainConflictException(
                $"Task {taskId} is parked on a disputed rebase conflict — merge-ready has no meaning " +
                "here, because nothing has been rebased yet and the branch still conflicts with its " +
                "base. Resolve with --needs-fixes \"<how to resolve the conflict>\" instead; the " +
                "follow-up applies your decision and retries the rebase.");
        }

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        ReviewVerdict verdict = settings.MergeReady ? ReviewVerdict.MergeReady : ReviewVerdict.NeedsFixes;
        // Normalized so the stored event agrees with how the rest of the pipeline reads it:
        // prompt rendering and echo-stripping both treat a blank reason as "none recorded".
        string? reason = settings.MergeReady ? settings.Reason : settings.NeedsFixes;
        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        session.Events.Append(runId, expectedVersion: fence.Version + 1, new ReviewParkResolved(
            runId, verdict, reason, DateTimeOffset.UtcNow, context.OwnerId));

        // The run is no longer parked, so the sweep's parked-run shield no longer covers
        // this lease; a fresh heartbeat holds the task while the daemon wakes.
        TaskLease? lease = await session.LoadAsync<TaskLease>(taskId, cancellationToken);
        if (lease is not null)
        {
            lease.HeartbeatAt = DateTimeOffset.UtcNow;
            session.Store(lease);
        }

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Run {runId} changed while resolving — the daemon (or another resolve) got there " +
                "first. Check h9k status; re-run this command only if the run is still ReviewParked.");
        }
        await Doorbell.RingAsync($"review-resolve:{taskId}", cancellationToken);

        // What happens next differs by where the park caught the run, so say which — and this
        // has to read RunAggregate.ParkedFromState, not ReviewCycle, even though the refusal
        // guard above reads ReviewCycle: Apply(ReviewParkResolved) itself still branches on
        // ParkedFromState == RunState.Verifying (RunAggregate.cs), so a message keyed on
        // ReviewCycle would describe a re-enter-at-the-gates outcome on a second pre-gate
        // dispute that the aggregate actually settles straight to the pull request. That
        // mismatch between the aggregate's two conditions is a real, recorded gap (backlog 64);
        // until it's fixed, the honest message is the one that matches what
        // Apply(ReviewParkResolved) will actually do.
        FormattableString outcome = (settings.MergeReady, run.ParkedFromState == RunState.Verifying) switch
        {
            (true, true) =>
                $"[dim]Run {runId} resolved merge-ready — the daemon re-enters the pipeline at the gates, then review; the pull request opens if both pass.[/]",
            (true, false) =>
                $"[dim]Run {runId} resolved merge-ready — the daemon resumes it and opens the pull request.[/]",
            _ =>
                $"[dim]Run {runId} resolved needs-fixes — the daemon dispatches a fix session with your reason as its findings.[/]",
        };
        AnsiConsole.MarkupLineInterpolated(outcome);
        return ExitCodes.Ok;
    }
}

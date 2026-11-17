using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon.ClaudeAccounts;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Documents;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// Spawns the "--resume" leg of a primary-session recovery: the exact process-spawn, session-
/// naming, and isolation mechanics token-budget recovery already uses
/// (<see cref="TokenBudgetRetryEngine"/>), factored out here so a second caller — the short-
/// backoff error-result retry (task: a session that reports an error result is retried once in
/// place, <see cref="RunSupervisor"/>) — spawns an identical resumed session rather than a
/// second, slightly different copy of the same rules. Neither caller may depend on the other
/// directly (<see cref="TokenBudgetRetryEngine"/> already depends on <see cref="RunSupervisor"/>
/// to re-enter the review loop), so this sits underneath both.
/// </summary>
public sealed class PrimarySessionResumer(IExecutor executor, IOptions<DaemonOptions> options)
{
    /// <summary>
    /// Spawns the resumed session and appends its <see cref="RunResumed"/> milestone to
    /// <paramref name="session"/> — the caller's own transaction, so it lands atomically beside
    /// whatever else that caller is recording (a budget park's retry, or a session-error retry's
    /// own <see cref="RunSessionErrorRetried"/>) rather than in a second round trip. The caller
    /// still owns <c>SaveChangesAsync</c>, the <see cref="RunActivity"/> stream-cursor reset that
    /// a stdout-redirect truncation demands, and <c>RunSupervisor.StartMonitoring</c>.
    /// </summary>
    public async Task<SpawnedAgent> ResumeAsync(
        IDocumentSession session, RunDetails run, TaskDetails task, ProjectDetails project, string prompt,
        CancellationToken cancellationToken)
    {
        // A pr-review task's primary session reads another contributor's pull-request head
        // (RunLauncher's UntrustedWorkingDirectory), so a
        // resume of that same session carries the same distrust forward — otherwise the
        // resumed --resume spawn would load the foreign checkout's own .claude/ config
        // and CLAUDE.md/AGENTS.md under the owner's credentials (adversarial review, cycle 2).
        // Reuses the primary session's own recorded name (RunDispatched.SessionName) rather
        // than re-deriving it: a resume re-enters the same session, so it keeps the same
        // name it was dispatched under. A stream written before that field existed falls
        // back to the identical three-way split RunLauncher used to pick the name in the
        // first place. Which persona's session that is has been the plan's own first session
        // since the registry landed (idea b9b09779) — QA's, for an assignee who declared only
        // qa — so the adversarial arm below is the fallback's answer for a legacy stream, not
        // a claim about today's runs: every stream carrying personas at all also carries the
        // recorded name, and only a pre-persona stream ever reaches this branch.
        string sessionRole = task.Type == TaskType.PrReview
            ? SessionRoleName.ReviewAdversarial(1)
            : run.IsFollowUp
                ? task.FollowUpKind == FollowUpKind.FailingChecks
                    ? SessionRoleName.Checks
                    : task.FollowUpKind == FollowUpKind.Rebase
                        ? SessionRoleName.Rebase
                        : SessionRoleName.Build
                : SessionRoleName.Build;
        string sessionName = run.SessionName.IsNotBlank()
            ? run.SessionName
            : SessionRoleName.For(DomainId.Short(run.TaskId), sessionRole);

        // Re-resolved rather than carried from the run: the model rides on the run because it is a
        // recorded fact, but effort is only ever a settings-file line, and a resume rewrites that
        // file, so the retry runs at whatever the chain says now, the way the original spawn did.
        AgentEffort effort = options.Value.ResolveEffort(
            task.Type == TaskType.PrReview ? AgentRole.Review : AgentRole.Build, task.Effort, project.Effort);

        // The spawn's own stdout redirect (ShellRedirection.Wrap's "> stream.jsonl") truncates
        // this same path from inside the freshly spawned shell, asynchronously, well after
        // Process.Start already returns — a monitor that reseeks to byte zero the moment this
        // method returns can still read the errored session's own final result line back
        // before that truncation actually lands (adversarial pre-PR review, cycle 1:
        // RunSupervisor.MonitorAsync's in-place continue hit exactly this window and read a
        // second, phantom error off pre-truncation content, failing a run whose retry had, in
        // fact, already spawned successfully). Moving the old transcript aside first — rather
        // than racing a fixed delay against the child shell's own exec+open — makes the
        // handoff atomic from a reader's perspective: the path is either genuinely missing,
        // which StreamTailReader already reads as "nothing new yet", or genuinely the new
        // session's own fresh content, never the stale one.
        string streamFile = RunPaths.StreamFile(run.RunDirectory);
        if (File.Exists(streamFile))
        {
            File.Move(streamFile, $"{streamFile}.pre-resume-{DateTimeOffset.UtcNow.Ticks}");
        }

        bool isPrReview = task.Type == TaskType.PrReview;

        SessionStart start = await StartForAsync(run, task, prompt, options.Value, cancellationToken);

        ClaudeCodeAccount account = await ClaudeAccountResolution.ForRunAsync(session, run, cancellationToken);
        SpawnedAgent agent = await executor.SpawnAsync(new AgentSpawnRequest(
            run.Id, start.SessionId, run.WorktreePath, run.RunDirectory, start.Prompt,
            run.ExecutorMode, run.Model, effort,
            // Hardcoded false for a pr-review resume, never project.SkipPermissions (Brian's
            // ruling 2026-09-27: no pr-review session ever runs with permissions skipped, member
            // or not) — mirrors RunLauncher's own pr-review branch, which a resume must not
            // silently regress from by falling back to the project's own setting (independent
            // pre-PR review, cycle 1, both lenses: this was the fourth real spawn site the
            // security review's own IExecutor doc named only three of).
            SkipPermissions: isPrReview ? false : project.SkipPermissions,
            ResumeSessionId: start.ResumeSessionId, UntrustedWorkingDirectory: isPrReview,
            // Carried forward from the task's own declared budget, exactly as the original spawn
            // (RunLauncher.LaunchAsync) already passes it: without this, a resumed primary session
            // — a token-budget park's own retry, or the short-backoff error-result retry — ran
            // with no turn cap at all despite the task's own stated one, since AgentSpawnRequest's
            // own default is null/unbounded (independent pre-PR review, cycle 1, adversarial lens).
            MaxTurns: start.MaxTurns)
        {
            TaskId = task.Id,
            SessionName = sessionName,
            // The same real permission file the original pr-review dispatch spawned under
            // (RunLauncher's own isPrReview branch): without this, a resumed pr-review primary
            // session or mention follow-up fell back to ClaudeSettingsFile.Build's ordinary shape
            // and SkipPermissions above, ran with no allow/deny list at all (independent pre-PR
            // review, cycle 1, both lenses). QaGateCommands mirrors the resumed session's own
            // recorded persona — the first of run.PrReviewPersonasRan is the plan's primary
            // session (ReviewPersonaPlan.Sessions' own doc), so this is QA's own earned exception
            // exactly when the session being resumed is QA's.
            UsesReviewPermissions = isPrReview,
            GitHubRepository = isPrReview ? GitHubPullRequestReference.RepositoryOfTask(task.ExternalReference) : null,
            QaGateCommands = isPrReview && run.PrReviewPersonasRan.FirstOrDefault() == ReviewPersona.Qa
                ? QaGateCommandsResolver.Resolve(project)
                : null,
            Account = account,
        }, cancellationToken);

        // The retry's stdout redirect truncates the run's stream file fresh (log #2), so the
        // tail cursor has to restart at zero with it — otherwise the monitor seeks to an offset
        // the new file has not grown to yet.
        session.Store(new RunActivity
        {
            Id = run.Id,
            LastActivityAt = DateTimeOffset.UtcNow,
            StreamBytesRead = 0,
        });
        session.Events.Append(
            run.Id, new RunResumed(run.Id, agent.ProcessId, agent.StartedAt, DateTimeOffset.UtcNow, sessionName));

        return agent;
    }

    /// <summary>How the primary session starts again: the session id it runs under, the one it resumes, its prompt, and its turn cap.</summary>
    internal sealed record SessionStart(Guid SessionId, Guid? ResumeSessionId, string Prompt, int? MaxTurns);

    /// <summary>
    /// A session that ran is resumed with <c>--resume</c> under a fresh process session id and given
    /// <paramref name="retryPrompt"/>. A launch held because its account was refused never started a session, so
    /// there is nothing to resume: it starts fresh, under the session id it was dispatched with, from the prompt its
    /// refused spawn already wrote to the run directory, and a mention follow-up gets the same hard turn cap its
    /// original launch would have (RunLauncher's mention branch) rather than the task's.
    /// </summary>
    internal static async ValueTask<SessionStart> StartForAsync(
        RunDetails run, TaskDetails task, string retryPrompt, DaemonOptions options, CancellationToken cancellationToken)
    {
        bool neverStarted = run.ProcessStartedAt is null && run.AccountHeldConnectionId is not null;
        if (!neverStarted)
        {
            return new SessionStart(DomainId.New(), run.SessionId, retryPrompt, task.Constraints?.MaxTurns);
        }

        string promptFile = RunPaths.PromptFile(RunPaths.ResolveCurrentDirectory(run.RunDirectory));
        string prompt = File.Exists(promptFile)
            ? await File.ReadAllTextAsync(promptFile, cancellationToken)
            : retryPrompt;
        int? maxTurns = run.PrReviewMentionCommentId is not null
            ? options.PrReviewMentionFollowUpMaxTurns
            : task.Constraints?.MaxTurns;
        return new SessionStart(run.SessionId, null, prompt, maxTurns);
    }
}

using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Shared.Exceptions;
using Npgsql;
using Spectre.Console.Cli;

CommandApp app = new();
app.Configure(config =>
{
    config.SetApplicationName("h9k");
    config.SetApplicationVersion(CliVersion.Current);
    config.PropagateExceptions();

    config.AddBranch("project", project =>
    {
        project.SetDescription("Manage projects: register them, browse them, inspect one");
        project.AddCommand<ProjectAddCommand>("add")
            .WithDescription("Register a project (repo path, base branch, connection binding)")
            .WithExample("project", "add", "--name", "hall9k", "--repo", "~/code/hall9k", "--base-branch", "main");
        project.AddCommand<ProjectListCommand>("list")
            .WithDescription(
                "Every registered project, one row each, with its tasks counted by attention bucket "
                + "(needs you, stalled, active, in review, queued, done, closed). The counts are "
                + "single-assignment, so a row sums to the project's task count — this is where you look "
                + "to see which project is asking for something.")
            .WithExample("project", "list");
        project.AddCommand<ProjectShowCommand>("show")
            .WithDescription(
                "One project in one pane: how it is registered (repository, base branch, connection binding, "
                + "owner) and every setting the daemon runs it by (skip-permissions, verify gates, parallelism, "
                + "commit style, context links), plus its task rollup and newest tasks. Takes the project name, "
                + "an unambiguous fragment of it, or its id.")
            .WithExample("project", "show", "hall9k")
            .WithExample("project", "show", "hall");
        project.AddCommand<ProjectSetCommand>("set")
            .WithDescription(
                "Change project settings: verify gates, skip-permissions, links, parallelism, "
                + "commit style, agent model")
            .WithExample("project", "set", "hall9k", "--commit-style", "narrative")
            .WithExample("project", "set", "hall9k", "--model", "claude-opus-5");
    });

    config.AddBranch("pr", pullRequest =>
    {
        pullRequest.SetDescription("Work with a task's pull request");
        pullRequest.AddCommand<PullRequestResolveCommand>("resolve")
            .WithDescription(
                "Dispatch a follow-up run onto a done task's existing PR branch to resolve review feedback "
                + "(or fix failing CI with --checks). Also resets the closeout monitor's automatic retry budget.")
            .WithExample("pr", "resolve", "28b19893")
            .WithExample("pr", "resolve", "28b19893", "--checks");
    });

    config.AddBranch("review", review =>
    {
        review.SetDescription("Work with the pre-PR review loop (PLAN.md log #24)");
        review.AddCommand<ReviewResolveCommand>("resolve")
            .WithDescription(
                "Record your verdict on a review-parked run: --merge-ready proceeds to the pull request, "
                + "--needs-fixes <reason> dispatches a fix session (and, like pr resolve, restores the "
                + "automatic fix budget). The park reason and findings files name what needs judging.")
            .WithExample("review", "resolve", "28b19893", "--merge-ready")
            .WithExample("review", "resolve", "28b19893", "--needs-fixes", "The limiter reset finding is real; fix it as the reviewer described");
    });

    config.AddCommand<StatusCommand>("status")
        .WithDescription(
            "The attention pane: what needs you, what has gone quiet, what is running — bounded and "
            + "glanceable, with everything else counted in the header. Browsing lives under the nouns "
            + "(h9k task list, h9k project list); this answers \"what should I look at right now\".")
        .WithExample("status");
    config.AddCommand<LogsCommand>("logs")
        .WithDescription("A run's transcript, rendered (or --raw for stream-json)");

    config.AddCommand<InstallCommand>("install")
        .WithDescription(
            "Publish h9k + h9kd release binaries to ~/.hall9k/bin and link h9k onto the PATH. Registers no "
            + "background service and no login item — the daemon is started on demand (h9k daemon start). "
            + "Re-run after a merge to refresh the binaries; a running daemon is offered a restart (Decisions Log #31).")
        .WithExample("install")
        .WithExample("install", "--restart");

    config.AddBranch("daemon", daemon =>
    {
        daemon.SetDescription(
            "The daemon's CLI-owned lifecycle (Decisions Log #31): start and stop on demand; a stopped daemon "
            + "costs latency, never correctness — startup adopts, sweeps, and closes out whatever happened while down");
        daemon.AddCommand<DaemonStartCommand>("start")
            .WithDescription(
                "Launch h9kd detached from this terminal (it survives shell exit), logging to ~/.hall9k/h9kd.log. "
                + "Refuses politely if one is already running, then reports what startup caught up on "
                + "(runs adopted, leases swept, merges observed).")
            .WithExample("daemon", "start");
        daemon.AddCommand<DaemonStopCommand>("stop")
            .WithDescription(
                "Stop h9kd gracefully: in-flight event appends finish, detached agents keep running and are "
                + "adopted on the next start. Goes through launchctl when autostart owns the job, so stopped means stopped.")
            .WithExample("daemon", "stop");
        daemon.AddCommand<DaemonStatusCommand>("status")
            .WithDescription("Running or not, pid, uptime, autostart posture, and the last few log lines")
            .WithExample("daemon", "status");
        daemon.AddBranch("autostart", autostart =>
        {
            autostart.SetDescription(
                "Start-at-login, strictly opt-in — never implied by install or start (macOS launchd LaunchAgent; "
                + "Windows arrives with S1-14)");
            autostart.AddCommand<DaemonAutostartEnableCommand>("enable")
                .WithDescription(
                    "Register the launchd LaunchAgent: h9kd starts at login and restarts after a crash — "
                    + "never after a clean stop, and h9k daemon stop always still means stopped")
                .WithExample("daemon", "autostart", "enable");
            autostart.AddCommand<DaemonAutostartDisableCommand>("disable")
                .WithDescription("Fully unregister start-at-login (stops a launchd-owned daemon and says so)")
                .WithExample("daemon", "autostart", "disable");
        });
    });

    config.AddBranch("task", task =>
    {
        task.SetDescription(
            "Manage tasks. Development and dispatch are separate lifecycles (Decisions Log #34): "
            + "add drafts, revise develops, publish is the readiness gate, and assign is the go signal. "
            + "A task runs only once a human assigns it and every dependency has closed out.");
        task.AddCommand<TaskAddCommand>("add")
            .WithDescription(
                "Create a draft (flags or --file task.md). Creation is identity, not readiness: a project "
                + "and an objective are all it takes, and the draft is invisible to the dispatcher until "
                + "you publish and assign it. Acceptance criteria are what h9k task publish demands.")
            .WithExample("task", "add", "--project", "hall9k", "--objective", "Add the project browse surface",
                "--criteria", "h9k project list shows one row per project")
            .WithExample("task", "add", "--file", "backlog/19-model-policy.md", "--model", "claude-opus-5")
            .WithExample("task", "add", "--project", "hall9k", "--objective", "Wire the new pane in",
                "--blocked-by", "28b19893");
        task.AddCommand<TaskReviseCommand>("revise")
            .WithDescription(
                "Revise a draft: objective, acceptance criteria, agent context, type, model, dependencies. "
                + "Draft-only by design — a published task promises it may be assigned at any moment and a "
                + "assigned one promises a node may read it at any moment, and editing would break both. "
                + "Each option passed replaces that part; each one left off is left alone.")
            .WithExample("task", "revise", "28b19893", "--criteria", "h9k status shows the blocked reason")
            .WithExample("task", "revise", "28b19893", "--blocked-by", "3f2a91b2", "--blocked-by", "91bd44c0")
            .WithExample("task", "revise", "28b19893", "--clear-dependencies");
        task.AddCommand<TaskPublishCommand>("publish")
            .WithDescription(
                "Publish a draft: the readiness gate. Enforces the full contract (an outcome-phrased "
                + "objective and at least one checkable acceptance criterion, PLAN.md §4) and refuses a "
                + "dependency cycle, naming it. A published task is immutable and assignable but still "
                + "will not run — assigning it is a separate, explicit act (--assign does both at once).")
            .WithExample("task", "publish", "28b19893")
            .WithExample("task", "publish", "28b19893", "--assign")
            .WithExample("task", "publish", "28b19893", "--no-assign");
        task.AddCommand<TaskAssignCommand>("assign")
            .WithDescription(
                "Assign a published task to an owner: the dispatch trigger, and the only way a task "
                + "becomes claimable. It queues when every dependency has reached true closeout (the "
                + "pull request merged), and blocks otherwise — unblocking itself when the last one lands. "
                + "Only that owner's nodes may claim it.")
            .WithExample("task", "assign", "28b19893")
            .WithExample("task", "assign", "28b19893", "brian");
        task.AddCommand<TaskUnassignCommand>("unassign")
            .WithDescription(
                "Take a queued or blocked task back to Published, so no node claims it. Refused while a "
                + "node holds the lease — that is a running agent. This is the first step of the "
                + "edit-after-the-fact path: unassign → draft → revise → publish → assign.")
            .WithExample("task", "unassign", "28b19893", "--reason", "The criteria missed the migration case");
        task.AddCommand<TaskDraftCommand>("draft")
            .WithDescription(
                "Return a published task to Draft so it can be revised. Refused from Queued and Blocked "
                + "onward: unassign it first, so a task the dispatcher can see never becomes editable by "
                + "one keystroke.")
            .WithExample("task", "draft", "28b19893");
        task.AddCommand<TaskListCommand>("list")
            .WithDescription(
                "Browse tasks newest-first, across projects or filtered to one (--project) and to a state "
                + "(--state, matched against the Status column: an attention group like needs-you or active, "
                + "or an exact state like Running). Bounded to the newest 20 by default — the footer says how "
                + "many were held back and how to see them (--all, --limit <n>).")
            .WithExample("task", "list")
            .WithExample("task", "list", "--project", "hall9k", "--state", "needs-you")
            .WithExample("task", "list", "--state", "draft")
            .WithExample("task", "list", "--state", "in-review", "--all")
            .WithExample("task", "list", "--state", "AwaitingReview");
        task.AddCommand<TaskShowCommand>("show")
            .WithDescription("Task detail: contract, conversation, runs")
            .WithExample("task", "show", "28b19893");
        task.AddCommand<TaskAbandonCommand>("abandon")
            .WithDescription(
                "Abandon a task (terminal; releases any lease). Reaches every non-terminal state, drafts "
                + "and published tasks included — walking away from an idea you have stopped believing in "
                + "is the same act as walking away from a run that failed.")
            .WithExample("task", "abandon", "28b19893", "--reason", "Superseded by the noun-first CLI work");
        task.AddCommand<TaskRetryCommand>("retry")
            .WithDescription(
                "Requeue a failed task for another run (human-only; Failed tasks only — Abandoned stays terminal). "
                + "The failure stays on the stream; the new run resumes the failed run's branch when it survives, "
                + "or starts clean from the base branch when the artifacts are gone. "
                + "Failed's other exits: h9k task resolve (objective already met), h9k task abandon (walk away).")
            .WithExample("task", "retry", "28b19893")
            .WithExample("task", "retry", "28b19893", "--reason", "Daemon push bug fixed; the completed work is intact in the worktree");
        task.AddCommand<TaskResolveCommand>("resolve")
            .WithDescription(
                "Resolve a failed task to Done: your attestation that the objective was met even though the run "
                + "failed (human-only; Failed tasks only). --reason is required — an attestation without a why is "
                + "a guess. The failure stays on the stream; --pr records where the work landed. "
                + "Failed's other exits: h9k task retry (run again), h9k task abandon (walk away).")
            .WithExample("task", "resolve", "28b19893", "--reason", "Work merged as PR #7; only the daemon's push step failed")
            .WithExample("task", "resolve", "28b19893", "--reason", "Objective met by hand in the worktree", "--pr", "https://github.com/x/y/pull/7");
    });
});

try
{
    return await app.RunAsync(args);
}
catch (DomainValidationException exception)
{
    await Console.Error.WriteLineAsync(exception.Message);
    return ExitCodes.Validation;
}
catch (DomainNotFoundException exception)
{
    await Console.Error.WriteLineAsync(exception.Message);
    return ExitCodes.NotFound;
}
catch (DomainConflictException exception)
{
    await Console.Error.WriteLineAsync(exception.Message);
    return ExitCodes.Conflict;
}
catch (DomainBusinessRuleException exception)
{
    await Console.Error.WriteLineAsync(exception.Message);
    return ExitCodes.BusinessRule;
}
catch (NpgsqlException exception)
{
    await Console.Error.WriteLineAsync(
        $"Cannot reach Postgres: {exception.Message}\n" +
        "Is it running? Start it with: docker compose up -d  (or the Aspire AppHost)");
    return ExitCodes.Error;
}

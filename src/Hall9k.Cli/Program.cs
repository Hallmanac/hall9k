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
        project.SetDescription("Manage projects");
        project.AddCommand<ProjectAddCommand>("add")
            .WithDescription("Register a project (repo path, base branch, connection binding)");
        project.AddCommand<ProjectSetCommand>("set")
            .WithDescription("Change project settings: verify gates, skip-permissions, links, parallelism, commit style")
            .WithExample("project", "set", "hall9k", "--commit-style", "narrative");
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
        .WithDescription("The one-pane view: what needs you, what's running, what's done");
    config.AddCommand<LogsCommand>("logs")
        .WithDescription("A run's transcript, rendered (or --raw for stream-json)");

    config.AddBranch("task", task =>
    {
        task.SetDescription("Manage tasks");
        task.AddCommand<TaskAddCommand>("add")
            .WithDescription("Queue a task (flags or --file task.md); enforces the readiness contract");
        task.AddCommand<TaskListCommand>("list")
            .WithDescription("List tasks across projects");
        task.AddCommand<TaskShowCommand>("show")
            .WithDescription("Task detail: contract, conversation, runs");
        task.AddCommand<TaskAbandonCommand>("abandon")
            .WithDescription("Abandon a task (terminal; releases any lease)");
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

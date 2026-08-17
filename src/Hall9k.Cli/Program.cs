using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Shared.Exceptions;
using Npgsql;
using Spectre.Console.Cli;

CommandApp app = new();
app.Configure(config =>
{
    config.SetApplicationName("h9k");
    config.PropagateExceptions();

    config.AddBranch("project", project =>
    {
        project.SetDescription("Manage projects");
        project.AddCommand<ProjectAddCommand>("add")
            .WithDescription("Register a project (repo path, base branch, connection binding)");
        project.AddCommand<ProjectSetCommand>("set")
            .WithDescription("Change project settings: verify gates, skip-permissions, links, parallelism");
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

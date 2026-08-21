using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

public sealed class OwnerShowCommand : Hall9kAsyncCommand<OwnerShowCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[OWNER]")]
        [Description(
            "Owner name, an unambiguous fragment of it or their email, or the full id. Omit it "
            + "when this platform has exactly one owner.")]
        public string? Owner { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        OwnerDetails owner = await OwnerResolver.ResolveOrSoleAsync(session, settings.Owner, cancellationToken);
        IReadOnlyList<ProjectDetails> projects = await session.Query<ProjectDetails>()
            .Where(project => project.OwnerId == owner.Id)
            .ToListAsync(cancellationToken);

        Table table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumns("k", "v");
        table.AddRow("[bold]Owner[/]", $"[bold]{owner.Name.EscapeMarkup()}[/]");
        table.AddRow("Id", $"[dim]{owner.Id}[/]");
        table.AddRow("Email", owner.Email.IsNotBlank() ? owner.Email!.EscapeMarkup() : "[dim]none recorded[/]");
        table.AddRow("Projects", projects.Count == 0
            ? "[dim]none registered to this owner yet[/]"
            : string.Join(", ", projects.Select(project => project.Name.EscapeMarkup()).Order(StringComparer.OrdinalIgnoreCase)));
        table.AddRow("Re-request review", DescribePolicy(owner.ReviewRerequest));
        table.AddRow("Registered", $"[dim]{owner.RegisteredAt.ToLocalTime():g}[/]");
        table.AddRow("Settings changed", owner.SettingsChangedAt is { } changedAt
            ? $"[dim]{changedAt.ToLocalTime():g}[/]"
            : "[dim]never — still the registration defaults[/]");

        AnsiConsole.Write(table);
        // A name with a space is two arguments unless it is quoted, and a hint that does not
        // run as printed is worse than no hint.
        string named = owner.Name.Any(char.IsWhiteSpace) ? $"\"{owner.Name}\"" : owner.Name;
        AnsiConsole.MarkupLine(
            $"\n[dim]Change a preference:[/] h9k owner set {named.EscapeMarkup()} --rerequest-review on");
        return ExitCodes.Ok;
    }

    private static string DescribePolicy(ReviewRerequestPolicy policy) => ReviewRerequestOption.Describe(
        policy,
        "after a fix follow-up pushes, closeout asks the pull request's reviewers for another pass "
        + "(log #62); a project setting can still override this",
        "the project setting decides, else the node default (DaemonOptions.DefaultReviewRerequest, off)");
}

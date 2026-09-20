using System.ComponentModel;
using System.Globalization;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Prints this project's run skill exactly as this node's own event stream last recorded it
/// (idea b9b09779, piece 4) — this node's own audit trail, never a live ledger read, the same
/// reason <c>ProjectPromptAddendumShowCommand</c> reads the projection: the ledger is the
/// daemon's to write and to fetch, and a CLI process that read it directly would need the
/// project's remote reachable to answer a question the local store already holds.
/// </summary>
public sealed class ProjectRunSkillShowCommand : Hall9kAsyncCommand<ProjectRunSkillShowCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PROJECT>")]
        [Description("Project name, an unambiguous fragment of it, or its id.")]
        public string Project { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        return await RunAsync(session, settings, cancellationToken);
    }

    internal static async Task<int> RunAsync(
        IDocumentSession session, Settings settings, CancellationToken cancellationToken)
    {
        ProjectDetails project = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);

        if (project.RunSkill is not { } skill)
        {
            AnsiConsole.MarkupLine(
                $"[dim]'{project.Name.EscapeMarkup()}' has no run skill yet.[/] {PendingState(project)}");
            return ExitCodes.Ok;
        }

        AnsiConsole.MarkupLine($"Run skill for '{project.Name.EscapeMarkup()}' [dim]({skill.Shape.Value})[/]:");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine(skill.Content);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[dim]Composed by {skill.Author.Value} "
            + $"{skill.RecordedAt.ToString("u", CultureInfo.InvariantCulture)}, against "
            + (skill.ComposedAgainstCommit.IsNotBlank()
                ? $"commit {skill.ComposedAgainstCommit.EscapeMarkup()}"
                : "no recorded commit")
            + ".[/]");

        if (project.RunSkillDiscoveryOutstanding)
        {
            AnsiConsole.MarkupLine(
                "[dim]A fresh discovery is outstanding; the daemon replaces the above on its next "
                + "run-skill sweep.[/]");
        }
        else if (project.RunSkillDiscoveryFailure is { } failure)
        {
            // Printed beside the skill, not only in place of a missing one: a re-discovery that
            // failed leaves the OLD document standing above, and a reader given it with no
            // warning reads a skill the repository may have outgrown as current, and waits for a
            // replacement that is not coming (independent pre-PR review, cycle 1, adversarial
            // lens). The reason FailRunSkillDiscovery insists on exists to be read somewhere.
            AnsiConsole.MarkupLine(
                $"[yellow]Warning:[/] the last discovery failed and did not replace the above: "
                + $"{failure.EscapeMarkup()} Ask again with h9k project set "
                + $"{project.Name.EscapeMarkup()} --discover-run-skill.");
        }

        // Mirrors the prompt-addenda panes' own warning: the audit trail above is accurate, but a
        // standing ledger-push failure means the daemon has not actually got this content onto
        // the ledger, so no other member's machine can read it yet.
        RunSkillSyncPosition? position = await session.LoadAsync<RunSkillSyncPosition>(project.Id, cancellationToken);
        if (position is { LastPushError: { } error })
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Warning:[/] the daemon has not been able to push this project's run skill to the "
                + $"ledger since {position.LastPushErrorAt:u} — no other member's machine can read it yet. "
                + $"Last error: {error.EscapeMarkup()}");
        }

        return ExitCodes.Ok;
    }

    /// <summary>
    /// What a project with no recorded skill is actually waiting on — told apart honestly rather
    /// than collapsed into one "nothing here" line: a discovery the daemon will answer, one
    /// already dispatched with nothing recorded since, one that failed and why, or nobody having
    /// asked at all. The second of those carries two states the timestamps genuinely cannot tell
    /// apart — a session composing right now, since the sweep spawns and waits inline, and one
    /// lost mid-wait to a daemon restart — so both are named rather than one asserted. Only the
    /// lost one is worth asking again for, and it is never redispatched on its own.
    /// </summary>
    private static string PendingState(ProjectDetails project) => project switch
    {
        { RunSkillDiscoveryOutstanding: true } =>
            "A discovery is outstanding; the daemon answers it on its next run-skill sweep.",
        { RunSkillDiscoveryRequestedAt: not null } =>
            $"A discovery session was dispatched {project.RunSkillDiscoveryDispatchedAt:u} and has recorded "
            + "nothing since: it is either still composing or was lost mid-wait, and a lost one is never "
            + $"redispatched on its own, so ask again if it stays this way with h9k project set "
            + $"{project.Name.EscapeMarkup()} --discover-run-skill.",
        { RunSkillDiscoveryFailure: { } failure } =>
            $"The last discovery failed: {failure.EscapeMarkup()} Ask again with "
            + $"h9k project set {project.Name.EscapeMarkup()} --discover-run-skill.",
        _ =>
            $"Ask for one with h9k project set {project.Name.EscapeMarkup()} --discover-run-skill, "
            + $"or write it yourself with h9k project run-skill set {project.Name.EscapeMarkup()} "
            + "--file <path> --shape full-text.",
    };
}

using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Decision;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Record a binding decision (idea d805fd8b, piece 1, Brian's ruling 2026-09-16). The bare
/// positional form always writes and never reads: <c>h9k decide "…"</c> is a write, and every
/// read lives behind its own subcommand, so there is no shape of this command that looks like a
/// query and turns out to have appended an event.
/// <para>
/// The id it prints is the citation key. That is the whole point of the aggregate: a decision
/// gets a stable identity the moment it is recorded, rather than a sequential number that has to
/// be assigned at merge time and that forced a renumberer, a placeholder value object, a guard
/// test, and a tail conflict on every stacked replay.
/// </para>
/// </summary>
public sealed class DecideCommand : Hall9kAsyncCommand<DecideCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<STATEMENT>")]
        [Description(
            "The decision itself, in one claim, stated as a rule rather than a story and "
            + "self-contained enough to read on its own six months from now. \"Agents never push; the "
            + "daemon pushes every branch with --force-with-lease\" is a decision. \"We talked about "
            + "pushing\" is not")]
        public string Statement { get; init; } = string.Empty;

        [CommandOption("--project <PROJECT>")]
        [Description(
            "The project this decision binds: its name, an unambiguous fragment of it, or its id. "
            + "Defaults to the project of the run you are recording from, then to the sole registered "
            + "project. A project-scoped decision travels to every member of that project")]
        public string? Project { get; init; }

        [CommandOption("--owner")]
        [Description(
            "Scope this to you rather than to a project: a cross-project habit that holds wherever you "
            + "work. Widening is the deliberate act because the failure is asymmetric — too narrow "
            + "means one project misses it, too wide means it rides in every prompt everywhere")]
        public bool Owner { get; init; }

        [CommandOption("--origin <TEXT>")]
        [Description(
            "The concrete failure that produced this rule, with its date where you have one. AGENTS.md's "
            + "own standing rule recorded as a field: a rulebook is an accumulation of documented scars, "
            + "and a rule stripped of its scar looks arbitrary to the next reader. Leave it off when "
            + "there genuinely was no incident — never invent one")]
        public string? Origin { get; init; }

        [CommandOption("--supersedes <DECISION>")]
        [Description(
            "A decision this one replaces — its id or an unambiguous fragment, repeatable. Each named "
            + "decision is marked superseded by this one in the same transaction, so both directions of "
            + "the replacement are on the streams. Nothing is deleted")]
        public string[] Supersedes { get; init; } = [];

        [CommandOption("--task <TASK>")]
        [Description(
            "The task whose live run you are recording this from, so the decision carries that run and "
            + "task as provenance. Leave it off from a plain shell and both are recorded as explicit "
            + "nulls rather than guessed at. A run that is not human-attended is refused here and told "
            + "to use h9k learn instead")]
        public string? Task { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        (DecisionRecorded recorded, ResolvedKnowledgeScope scope) =
            await RunAsync(session, settings, context.OwnerId, DateTimeOffset.UtcNow, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        Print(recorded, scope);
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Everything but the store round trip and the printing, so the whole write is testable
    /// against a real document session — the shape <see cref="ProjectMembersCommand"/> and
    /// <see cref="TaskLogInteractionCommand"/> already use for the same reason. Appends and
    /// returns; the caller saves.
    /// </summary>
    internal static async Task<(DecisionRecorded Recorded, ResolvedKnowledgeScope Scope)> RunAsync(
        IDocumentSession session, Settings settings, Guid ownerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ObservedRecordingContext observed = await RecordingProvenanceReader.ObserveAsync(
            session, settings.Task, ownerId, cancellationToken);

        ResolvedKnowledgeScope scope = await KnowledgeScopeResolver.ResolveAsync(
            session, settings.Project, settings.Owner, observed.ProjectId, ownerId, "decision", cancellationToken);

        // Resolved before anything is appended: a misspelled --supersedes must refuse the whole
        // call rather than record a decision that half-claims a replacement it never made.
        List<DecisionAggregate> superseded = [];
        foreach (string reference in settings.Supersedes)
        {
            superseded.Add(await DecisionIdResolver.LoadAsync(session, reference, cancellationToken));
        }

        Guid decisionId = DomainId.New();
        DecisionRecorded recorded = DecisionDecider.Record(
            decisionId, scope.Scope, scope.ScopeId, settings.Statement, settings.Origin,
            [.. superseded.Select(decision => decision.Id)], observed.Provenance, now);
        session.Events.StartStream<DecisionAggregate>(decisionId, recorded);

        // The other half of the same fact, on each replaced decision's own stream, in this same
        // transaction. The reason is composed rather than typed because the act itself is the
        // reason here: what replaced it is named, and its statement is one h9k decide show away.
        // The standalone h9k decide supersede is where a human's own prose reason belongs.
        foreach (DecisionAggregate replaced in superseded)
        {
            session.Events.Append(replaced.Id, DecisionDecider.Supersede(
                replaced, decisionId, $"Replaced by decision {DomainId.Short(decisionId)} when it was recorded.",
                ownerId, now));
        }

        return (recorded, scope);
    }

    private static void Print(DecisionRecorded recorded, ResolvedKnowledgeScope scope)
    {
        string shortId = DomainId.Short(recorded.Id);
        AnsiConsole.MarkupLine(
            $"[blue]Decision recorded[/] [dim]({shortId})[/] {recorded.Statement.EscapeMarkup()}");
        AnsiConsole.MarkupLine(
            $"[dim]  scope:[/] {scope.Scope.Value.ToLowerInvariant()} — {scope.Label.EscapeMarkup()}");
        AnsiConsole.MarkupLine(recorded.OriginIncident is { } incident
            ? $"[dim]  origin:[/] {incident.EscapeMarkup()}"
            : "[dim]  origin: none recorded[/]");
        AnsiConsole.MarkupLine(recorded.Provenance.RunId is { } runId
            ? $"[dim]  from run:[/] {DomainId.Short(runId)} [dim]({recorded.Provenance.Attendance.Value.ToLowerInvariant()})[/]"
            : "[dim]  from run: none — recorded outside any run[/]");
        foreach (Guid replaced in recorded.Supersedes)
        {
            AnsiConsole.MarkupLine($"[dim]  supersedes:[/] {DomainId.Short(replaced)}");
        }

        AnsiConsole.MarkupLine(
            $"[dim]Cite it by id. Read it back:[/] h9k decide show {shortId} [dim]· browse:[/] h9k decide list");
    }
}

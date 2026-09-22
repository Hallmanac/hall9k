using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using JasperFx.Events;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Record a run-earned lesson (idea d805fd8b, piece 1; backlog 55). One path, two callers: an
/// agent mid-run records a lesson with the same command a human types at a shell, exactly as
/// with every other verb here. The bare positional form always writes and never reads.
/// <para>
/// A recorded lesson is live immediately, with no gate and no approval step. The cost model is
/// the argument: a task dispatched before it was ready costs a run, a worktree and a branch; a
/// lesson believed before it was corroborated costs one line of a prompt. Noise protection is
/// <c>h9k learn retire</c> being cheap, not good lessons being held back.
/// </para>
/// </summary>
public sealed class LearnCommand : Hall9kAsyncCommand<LearnCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<STATEMENT>")]
        [Description(
            "The lesson itself, in one claim, phrased as an instruction to the next agent and "
            + "self-contained: no run ids, no paths out of this worktree, nothing that only makes sense "
            + "inside this session. \"Integration tests need Docker running before dotnet test\" is a "
            + "lesson. \"The test failed and then it passed\" is not")]
        public string Statement { get; init; } = string.Empty;

        [CommandOption("--project <PROJECT>")]
        [Description(
            "The project this lesson applies to: its name, an unambiguous fragment of it, or its id. "
            + "Defaults to the project of the run you are recording from, then to the sole registered "
            + "project")]
        public string? Project { get; init; }

        [CommandOption("--owner")]
        [Description(
            "Scope this to you rather than to a project: a cross-project habit, a working preference, "
            + "something true of how you like things done wherever you work. Project is the default "
            + "because too wide is the worse mistake — a wrong statement then rides in every prompt on "
            + "every project")]
        public bool Owner { get; init; }

        [CommandOption("--task <TASK>")]
        [Description(
            "The task whose live run you are recording this from, so the lesson carries that run and "
            + "task as provenance. Leave it off from a plain shell and both are recorded as explicit "
            + "nulls rather than guessed at")]
        public string? Task { get; init; }

        [CommandOption("--distilled-from <LESSON>")]
        [Description(
            "A lesson this one was merged out of, repeatable, each an id or an unambiguous fragment of "
            + "one (h9k learn list shows them). This is the verb a distillation task uses (h9k learn "
            + "distill authors one): pass it and the lesson is recorded as a merge that cites its "
            + "sources, and the decider refuses it outright if the citations resolve to nothing, "
            + "because a merge nobody can check against what it merged is a new claim wearing a merge's "
            + "clothes. Merging does not retire the sources; that stays the explicit act, h9k learn "
            + "retire <id> --reason \"Absorbed into <this id>\"")]
        public string[] DistilledFrom { get; init; } = [];
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        (LearningRecorded recorded, ResolvedKnowledgeScope scope) = await RunAsync(
            session, settings, context.OwnerId, context.NodeId, DateTimeOffset.UtcNow, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        Print(recorded, scope);
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Everything but the store round trip and the printing, so the write is testable through the
    /// same seam <see cref="DecideCommand.RunAsync"/> uses.
    /// </summary>
    /// <param name="thisNodeId">
    /// This install's own node, which the append stamps onto the event so the inline
    /// <see cref="LearningDetailsProjection"/> can read it (see
    /// <see cref="EventRecordingNode.StampAtAppend"/>). <see cref="Guid.Empty"/> from a caller
    /// that has no node of its own, which records the lesson with no node observed rather than
    /// with a guessed one.
    /// </param>
    internal static async Task<(LearningRecorded Recorded, ResolvedKnowledgeScope Scope)> RunAsync(
        IDocumentSession session,
        Settings settings,
        Guid ownerId,
        Guid thisNodeId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ObservedRecordingContext observed = await RecordingProvenanceReader.ObserveAsync(
            session, settings.Task, ownerId, cancellationToken);

        ResolvedKnowledgeScope scope = await KnowledgeScopeResolver.ResolveAsync(
            session, settings.Project, settings.Owner, observed.ProjectId, ownerId, "lesson", cancellationToken);

        Guid learningId = DomainId.New();
        // Each source is resolved against the store before the decider ever sees it, so a typo is
        // refused by name here ("No lesson matches 'abc'") rather than recorded as a citation
        // pointing at nothing — a full, well-formed id included, which is why this goes through
        // ResolveRecordedAsync rather than ResolveAsync. The decider's own guard is the narrower
        // one it can actually hold without a store: at least one source, no repeats, and never
        // this lesson itself.
        List<Guid> sources = [];
        foreach (string reference in settings.DistilledFrom)
        {
            sources.Add(await LearningIdResolver.ResolveRecordedAsync(session, reference, cancellationToken));
        }

        LearningRecorded recorded = sources.Count > 0
            ? LearningDecider.RecordDistilled(
                learningId, scope.Scope, scope.ScopeId, settings.Statement, sources, observed.Provenance, now)
            : LearningDecider.Record(
                learningId, scope.Scope, scope.ScopeId, settings.Statement, observed.Provenance, now);
        StreamAction stream = session.Events.StartStream<LearningAggregate>(learningId, recorded);
        // Stamped here, at the append, rather than left to EventOriginStampingListener: the
        // LearningDetailsProjection that reads this node off the event is Inline, and Marten
        // applies inline projections before it runs any session listener, so the listener's own
        // stamp lands too late for the row this save writes.
        EventRecordingNode.StampAtAppend(stream, thisNodeId);

        return (recorded, scope);
    }

    private static void Print(LearningRecorded recorded, ResolvedKnowledgeScope scope)
    {
        string shortId = DomainId.Short(recorded.Id);
        AnsiConsole.MarkupLine($"[blue]Lesson recorded[/] [dim]({shortId})[/] {recorded.Statement.EscapeMarkup()}");
        AnsiConsole.MarkupLine(
            $"[dim]  scope:[/] {scope.Scope.Value.ToLowerInvariant()} — {scope.Label.EscapeMarkup()}");
        if (recorded.DistilledFrom is { Count: > 0 } sources)
        {
            AnsiConsole.MarkupLine(
                $"[dim]  merged from:[/] {string.Join(", ", sources.Select(DomainId.Short))} "
                + "[dim]· still live until each retires:[/] h9k learn retire <id> --reason "
                + $"\"Absorbed into {shortId}\"");
        }

        AnsiConsole.MarkupLine(recorded.Provenance.RunId is { } runId
            ? $"[dim]  from run:[/] {DomainId.Short(runId)} [dim]({recorded.Provenance.Attendance.Value.ToLowerInvariant()})[/]"
            : "[dim]  from run: none — recorded outside any run[/]");
        AnsiConsole.MarkupLine(
            $"[dim]Live now. Read it back:[/] h9k learn show {shortId} [dim]· wrong later:[/] "
            + $"h9k learn retire {shortId} --reason \"…\"");
    }
}

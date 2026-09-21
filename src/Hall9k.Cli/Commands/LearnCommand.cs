using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
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
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        (LearningRecorded recorded, ResolvedKnowledgeScope scope) =
            await RunAsync(session, settings, context.OwnerId, DateTimeOffset.UtcNow, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        Print(recorded, scope);
        return ExitCodes.Ok;
    }

    /// <summary>Everything but the store round trip and the printing, so the write is testable through the same seam <see cref="DecideCommand.RunAsync"/> uses.</summary>
    internal static async Task<(LearningRecorded Recorded, ResolvedKnowledgeScope Scope)> RunAsync(
        IDocumentSession session, Settings settings, Guid ownerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ObservedRecordingContext observed = await RecordingProvenanceReader.ObserveAsync(
            session, settings.Task, ownerId, cancellationToken);

        ResolvedKnowledgeScope scope = await KnowledgeScopeResolver.ResolveAsync(
            session, settings.Project, settings.Owner, observed.ProjectId, ownerId, "lesson", cancellationToken);

        Guid learningId = DomainId.New();
        LearningRecorded recorded = LearningDecider.Record(
            learningId, scope.Scope, scope.ScopeId, settings.Statement, observed.Provenance, now);
        session.Events.StartStream<LearningAggregate>(learningId, recorded);

        return (recorded, scope);
    }

    private static void Print(LearningRecorded recorded, ResolvedKnowledgeScope scope)
    {
        string shortId = DomainId.Short(recorded.Id);
        AnsiConsole.MarkupLine($"[blue]Lesson recorded[/] [dim]({shortId})[/] {recorded.Statement.EscapeMarkup()}");
        AnsiConsole.MarkupLine(
            $"[dim]  scope:[/] {scope.Scope.Value.ToLowerInvariant()} — {scope.Label.EscapeMarkup()}");
        AnsiConsole.MarkupLine(recorded.Provenance.RunId is { } runId
            ? $"[dim]  from run:[/] {DomainId.Short(runId)} [dim]({recorded.Provenance.Attendance.Value.ToLowerInvariant()})[/]"
            : "[dim]  from run: none — recorded outside any run[/]");
        AnsiConsole.MarkupLine(
            $"[dim]Live now. Read it back:[/] h9k learn show {shortId} [dim]· wrong later:[/] "
            + $"h9k learn retire {shortId} --reason \"…\"");
    }
}

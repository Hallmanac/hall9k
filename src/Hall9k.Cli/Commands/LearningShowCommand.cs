using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// One lesson in full (idea d805fd8b, piece 1): the statement, where it applies, its whole
/// provenance, and its retirement when it has one. Provenance is the half a reader actually
/// weighs a lesson by — a claim from an unattended run reads differently from one a human typed
/// — so it is rendered rather than summarised away.
/// </summary>
public sealed class LearningShowCommand : Hall9kAsyncCommand<LearningShowCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<LESSON>")]
        [Description("The lesson: its id, or an unambiguous fragment of one")]
        public string Learning { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        Guid id = await LearningIdResolver.ResolveAsync(session, settings.Learning, cancellationToken);
        LearningDetails learning = await session.LoadAsync<LearningDetails>(id, cancellationToken)
            ?? throw new DomainNotFoundException($"No lesson {id}.");

        await WriteAsync(session, learning, cancellationToken);
        return ExitCodes.Ok;
    }

    /// <summary>The rendering itself, so an integration test can drive it against a real store without a command app.</summary>
    internal static async Task WriteAsync(
        IQuerySession session, LearningDetails learning, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine($"[bold]{learning.Statement.EscapeMarkup()}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[dim]Id[/]           {learning.Id} [dim](cite it as {DomainId.Short(learning.Id)})[/]");
        AnsiConsole.MarkupLine($"[dim]Scope[/]        {await ScopeLineAsync(session, learning, cancellationToken)}");
        AnsiConsole.MarkupLine($"[dim]Status[/]       {StatusLine(learning)}");
        AnsiConsole.MarkupLine($"[dim]Recorded[/]     {learning.RecordedAt.ToLocalTime():g}");

        string ownerLabel = learning.Provenance is { } provenance
            ? await KnowledgeRecordRendering.OwnerLabelAsync(session, provenance.RecordedByOwnerId, cancellationToken)
            : string.Empty;
        KnowledgeRecordRendering.Write(learning.Provenance, ownerLabel);
        await WriteInjectionLineAsync(session, learning, cancellationToken);

        if (learning.DistilledFrom is { Count: > 0 } sources)
        {
            AnsiConsole.MarkupLine(
                $"[dim]Merged from[/]   {string.Join(", ", sources.Select(DomainId.Short))} "
                + "[dim](h9k learn show <id> on any of them)[/]");
        }

        if (learning.RetireReason is { } reason)
        {
            AnsiConsole.MarkupLine($"[dim]Why it ended[/] {reason.EscapeMarkup()}");
        }
    }

    /// <summary>
    /// Whether this lesson is one a dispatched session's prompt actually carries, and why (idea
    /// d805fd8b, piece 5). Rendered here rather than in <c>lessons.md</c> on purpose: the answer
    /// depends on which node is asking, and that file holds a byte-for-byte determinism contract
    /// across every node that has the same records (its own header says so, and points here). This
    /// is the surface where a node-local judgment belongs.
    /// <para>
    /// Status is read before provenance, because the composer filters on it first: a retired
    /// lesson reaches no prompt whatever its mark says, and reporting one as carried told an
    /// operator checking that a retirement had actually taken effect the opposite of the truth
    /// (independent pre-PR review, cycle 3, adversarial lens).
    /// </para>
    /// <para>
    /// Only the provenance rule is answered beyond that, never the caps. Whether a given lesson
    /// fitted inside the count and character caps depends on how many others were live at the
    /// moment a particular prompt was composed, so an answer here would be a guess about a past
    /// composition; the section in the prompt announces its own truncation instead.
    /// </para>
    /// </summary>
    private static async Task WriteInjectionLineAsync(
        IQuerySession session, LearningDetails learning, CancellationToken cancellationToken)
    {
        string machineName = Environment.MachineName;
        NodeDetails? node = (await session.Query<NodeDetails>()
            .Where(candidate => candidate.MachineName == machineName)
            .Take(1)
            .ToListAsync(cancellationToken)).FirstOrDefault();
        LessonProvenanceMark mark = LessonProvenanceMark.Of(
            learning.Provenance, learning.RecordedOnNodeId, node?.Id ?? Guid.Empty);
        string nodeLabel = learning.RecordedOnNodeId is { } recordingNode
            ? $"node {DomainId.Short(recordingNode)}"
            : "no node recorded";
        AnsiConsole.MarkupLine($"[dim]Recorded on[/]  {nodeLabel} [dim]({mark.Label})[/]");
        AnsiConsole.MarkupLine(InjectionLine(learning, mark));
    }

    /// <summary>
    /// The three answers, in the order the composer actually decides them
    /// (<see cref="LessonInjection.Compose"/>): a lesson that is not active is filtered out before
    /// a mark is computed at all, then the provenance rule runs. Pure, so both the status gate and
    /// the mark gate are provable without a store.
    /// </summary>
    internal static string InjectionLine(LearningDetails learning, LessonProvenanceMark mark) =>
        learning.Status != LearningStatus.Active
            ? "[yellow]In prompts[/]    no [dim]· only active lessons are composed into a section, so this one "
              + "stopped reaching prompts when it stopped being active[/]"
            : mark.ReachesAPrompt
                ? "[dim]In prompts[/]    yes, inside this node's lesson caps (h9k config show)"
                : "[yellow]In prompts[/]    no [dim]· rendered in lessons.md and listed here, but held out of "
                  + "dispatched prompts until idea 7e403b80's security review rules on it[/]";

    private static async Task<string> ScopeLineAsync(
        IQuerySession session, LearningDetails learning, CancellationToken cancellationToken) =>
        learning.Scope == KnowledgeScope.Owner
            ? $"owner — {(await KnowledgeRecordRendering.OwnerLabelAsync(session, learning.ScopeId, cancellationToken)).EscapeMarkup()}"
            : learning.Scope == KnowledgeScope.Project
                ? $"project — {(await KnowledgeRecordRendering.ProjectLabelAsync(session, learning.ScopeId, cancellationToken)).EscapeMarkup()}"
                : "[dim]unrecorded[/]";

    private static string StatusLine(LearningDetails learning) =>
        learning.Status == LearningStatus.Retired
            ? $"[yellow]retired[/]{(learning.RetiredAt is { } when ? $" on {when.ToLocalTime():g}" : string.Empty)}"
            : learning.Status == LearningStatus.Active
                ? "[green]active[/]"
                : "[dim]unrecorded[/]";
}

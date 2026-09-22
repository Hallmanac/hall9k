using System.ComponentModel;
using System.Globalization;
using System.Text;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Marten.Linq.MatchesSql;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Author the task that merges a scope's lessons into fewer, better ones (idea d805fd8b, piece 5;
/// backlog 55). What this command produces is an ordinary Research task draft: the same
/// <see cref="TaskDecider.Add"/> every <c>h9k task add</c> goes through, the same draft state, the
/// same publish-and-assign a human performs before anything dispatches. There is no distillation
/// engine, no sweep, and nothing in the daemon that decides on its own that a project's lessons
/// need merging.
/// <para>
/// That is the design rather than an omission. Merging two claims into one is a judgment about
/// meaning, and a wrong merge is worse than two lessons that overlap: the overlap costs a prompt
/// line, while a bad merge silently replaces two things somebody observed with one thing nobody
/// did. <c>h9k status</c> names this lever when a project's active lessons pass the prompt count
/// cap, which is as far as the platform goes; it says the inventory has grown and leaves the call
/// to a person.
/// </para>
/// <para>
/// The task's own instructions are merge-and-cite only, and they are prose in
/// <c>.claude/templates/learn-distill/task.md</c> rather than string literals here, so an operator
/// can reword them for their own project the way they can any other shipped prompt prose
/// (AGENTS.md, the judgment layer). The one half that is not prose is the citation requirement:
/// <see cref="LearningDecider.RecordDistilled"/> refuses a distilled lesson that cites nothing, so
/// that contract holds whatever the template ends up saying.
/// </para>
/// </summary>
public sealed class LearningDistillCommand : Hall9kAsyncCommand<LearningDistillCommand.Settings>
{
    /// <summary>
    /// The template package this command's prose ships in, named on its own so
    /// <see cref="InstallCommand"/>'s release-payload completeness check can require it by the
    /// same constant the loader below builds its path from.
    /// </summary>
    internal const string TemplatePackage = "learn-distill";

    /// <summary>Where this command's own prose lives, published as its own template package by <c>h9k install</c>.</summary>
    internal const string TemplateFile = $"{TemplatePackage}/task.md";

    /// <summary>
    /// Nothing to merge below two lessons, so the command refuses rather than authoring a task
    /// whose inventory cannot produce a single citation pair. Not a setting: this is arithmetic
    /// about what a merge is, not a preference.
    /// </summary>
    internal const int MinimumLessonsToDistil = 2;

    /// <summary>
    /// How many lessons the authored task's context carries as an orientation snapshot. Bounded
    /// for the same reason a prompt's own lesson section is: a task card whose context is two
    /// hundred lessons is a card nobody reads. The task is told to work from
    /// <c>h9k learn list</c>, which is the live set anyway.
    /// </summary>
    internal const int SnapshotLessons = 40;

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--project <PROJECT>")]
        [Description(
            "The project whose lessons this merges: its name, an unambiguous fragment of it, or its id. "
            + "Defaults to the sole registered project")]
        public string? Project { get; init; }

        [CommandOption("--owner")]
        [Description(
            "Merge your own cross-project lessons instead of one project's. The task still belongs to a "
            + "project, since every task does, so --project still names where the work is tracked")]
        public bool Owner { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        // Resolved through the same helper h9k learn and h9k decide use, so --project accepts the
        // same forms and a single-project install needs no flag at all. Always resolved as a
        // project even under --owner: every task belongs to one, so --owner says whose lessons are
        // being merged while --project still says where the work is tracked.
        ResolvedKnowledgeScope tracking = await KnowledgeScopeResolver.ResolveAsync(
            session, settings.Project, ownerScoped: false, projectFromRun: null, context.OwnerId,
            "distillation", cancellationToken);
        ResolvedKnowledgeScope scope = settings.Owner
            ? new ResolvedKnowledgeScope(KnowledgeScope.Owner, context.OwnerId, "you, across every project")
            : tracking;

        IReadOnlyList<LearningDetails> active = await ActiveAsync(session, scope, cancellationToken);
        if (active.Count < MinimumLessonsToDistil)
        {
            throw new DomainValidationException(
                $"{scope.Label} has {active.Count} active "
                + (active.Count == 1 ? "lesson" : "lessons")
                + ", so there is nothing to merge yet: a distillation needs at least "
                + $"{MinimumLessonsToDistil} lessons to cite. See what is on record: h9k learn list"
                + (settings.Owner ? " --owner" : $" --project {tracking.Label}"));
        }

        Guid taskId = DomainId.New();
        TaskAdded added = TaskDecider.Add(
            taskId,
            tracking.ScopeId,
            Objective(scope, active.Count),
            Criteria(),
            TaskType.Research,
            Context(scope, active, settings.Owner, tracking.Label, DateTimeOffset.UtcNow),
            constraints: null,
            externalReference: null,
            DateTimeOffset.UtcNow,
            context.OwnerId);
        session.Events.StartStream<TaskAggregate>(taskId, added);
        await session.SaveChangesAsync(cancellationToken);

        string shortId = DomainId.Short(taskId);
        AnsiConsole.MarkupLine(
            $"[blue]Distillation draft created[/] in '{tracking.Label.EscapeMarkup()}': "
            + $"{added.Objective.EscapeMarkup()} [dim]({shortId})[/]");
        AnsiConsole.MarkupLine(
            "[dim]It is a draft and nothing dispatches it: the daemon never distils on its own "
            + "judgment. Read what it asks for, then publish and assign it yourself:[/] "
            + $"h9k task show {shortId} [dim]then[/] h9k task publish {shortId}");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// The scope's live lessons, newest first: the same indexed filter
    /// <see cref="LearningListCommand.QueryAsync"/> and
    /// <see cref="Domain.Features.Learning.Queries.LessonPromptFeed"/> run, read here for two
    /// separate reasons: the refusal above needs the count, and the authored task's context
    /// carries a bounded snapshot of them.
    /// </summary>
    private static async Task<IReadOnlyList<LearningDetails>> ActiveAsync(
        IQuerySession session, ResolvedKnowledgeScope scope, CancellationToken cancellationToken)
    {
        string scopeValue = scope.Scope;
        string active = LearningStatus.Active;
        return await session.Query<LearningDetails>()
            .Where(lesson => lesson.MatchesSql("d.data ->> 'scope' = ?", scopeValue))
            .Where(lesson => lesson.ScopeId == scope.ScopeId)
            .Where(lesson => lesson.MatchesSql("d.data ->> 'status' = ?", active))
            .OrderByDescending(lesson => lesson.RecordedAt)
            .ToListAsync(cancellationToken);
    }

    internal static string Objective(ResolvedKnowledgeScope scope, int lessonCount) =>
        PromptTemplates.Load(TemplateFile, "objective", new Dictionary<string, string>
        {
            ["Scope"] = scope.Label,
            ["LessonCount"] = lessonCount.ToString(CultureInfo.InvariantCulture),
        });

    /// <summary>
    /// The acceptance contract, which is where merge-and-cite is stated as something a review pass
    /// grades against rather than as advice buried in the context.
    /// </summary>
    internal static IReadOnlyList<string> Criteria() =>
    [
        PromptTemplates.Load(TemplateFile, "criterion-merge-only"),
        PromptTemplates.Load(TemplateFile, "criterion-no-new-claims"),
        PromptTemplates.Load(TemplateFile, "criterion-retire-sources"),
        PromptTemplates.Load(TemplateFile, "criterion-leave-alone"),
        PromptTemplates.Load(TemplateFile, "criterion-account"),
    ];

    /// <summary>
    /// The task's own context: why distillation is a human-authored act, what merge-and-cite rules
    /// out, and a bounded snapshot of the inventory, labelled as a snapshot. Labelled rather than
    /// presented as the set because it will be stale by the time anything dispatches, and a
    /// session that mistook it for the live inventory would cite ids that have since retired.
    /// </summary>
    internal static string Context(
        ResolvedKnowledgeScope scope,
        IReadOnlyList<LearningDetails> active,
        bool ownerScoped,
        string projectName,
        DateTimeOffset takenAt)
    {
        string listArguments = ownerScoped ? "--owner" : $"--project {projectName}";
        StringBuilder context = new();
        context.AppendLine(PromptTemplates.Load(TemplateFile, "context", new Dictionary<string, string>
        {
            ["ListArguments"] = listArguments,
        }));
        context.AppendLine();
        context.AppendLine(
            $"Snapshot taken {takenAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture)}: "
            + $"{active.Count} active in {scope.Label}, newest first"
            + (active.Count > SnapshotLessons ? $", the first {SnapshotLessons} shown." : "."));
        context.AppendLine();
        foreach (LearningDetails lesson in active.Take(SnapshotLessons))
        {
            context.AppendLine($"- [{DomainId.Short(lesson.Id)}] {lesson.Statement.ReplaceLineEndings(" ").Trim()}");
        }

        return context.ToString().TrimEnd();
    }
}

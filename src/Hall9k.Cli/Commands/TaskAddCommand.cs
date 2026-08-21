using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Text;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Marten.Linq.MatchesSql;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

public sealed class TaskAddCommand : Hall9kAsyncCommand<TaskAddCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--project <PROJECT>")]
        [Description(
            "Project the task belongs to: its name, an unambiguous fragment of it, or its full id "
            + "(h9k project list shows them all). A fragment matching more than one project is "
            + "rejected as ambiguous rather than guessed at.")]
        public string? Project { get; init; }

        [CommandOption("--objective <OBJECTIVE>")]
        [Description(
            "One sentence, outcome-phrased — what the draft is about. Together with --project it is "
            + "everything creation requires: creation is identity, not readiness (Decisions Log #34). "
            + "The readiness contract is enforced later, once, by h9k task publish")]
        public string? Objective { get; init; }

        [CommandOption("--criteria <CRITERION>")]
        [Description(
            "Checkable acceptance criterion; repeat the option for more. Optional here and required "
            + "by h9k task publish — a draft exists in order to gather them")]
        public string[] Criteria { get; init; } = [];

        [CommandOption("--blocked-by <TASK>")]
        [Description(
            "A task this one waits on: its id or an unambiguous fragment; repeat the option for more. "
            + "A dependency counts as met only at true closeout (the pull request merged and the "
            + "closeout monitor observed it), so a Done-but-unmerged dependency still blocks. "
            + "Revise the set later with h9k task revise --blocked-by")]
        public string[] BlockedBy { get; init; } = [];

        [CommandOption("--type <TYPE>")]
        [Description("feature | bugfix | refactor | chore | research")]
        public string? Type { get; init; }

        [CommandOption("--context <CONTEXT>")]
        [Description("Agent-facing context (pointers, constraints, boundaries)")]
        public string? AgentContext { get; init; }

        [CommandOption("--file <PATH>")]
        [Description(
            "Task file: frontmatter (project/type/objective/criteria/model/blocked-by) + markdown body "
            + "as agent context")]
        public string? File { get; init; }

        [CommandOption("--from-issue <NUMBER-OR-URL>")]
        [Description(
            "Adopt an existing GitHub issue (PLAN.md §3.1a): the number (42 or #42), the owner/repo#42 "
            + "shorthand, or the issue URL on github.com. Read through the gh CLI from the project's "
            + "repository, so it uses your GitHub login and no token of Hall9k's. The title seeds the "
            + "objective and the body becomes agent context; the issue is recorded as the task's external "
            + "reference and rendered as a link by h9k task show. Acceptance criteria are NEVER "
            + "read out of an issue body — they are the readiness contract, so you supply them with "
            + "--criteria or at the prompt. Only an issue the source reports as open is adopted, so a "
            + "closed or missing one is refused; the state read at import is recorded as an "
            + "observation of that moment, never re-checked afterwards")]
        public string? FromIssue { get; init; }

        [CommandOption("--model <MODEL>")]
        [Description(
            "Model this task's sessions run on, overriding every other level of the chain "
            + "(Decisions Log #33): a tier alias (fable, opus, sonnet, haiku) or an exact model id "
            + "(claude-opus-5, claude-sonnet-5, or a context variant like claude-opus-5[[1m]]); anything "
            + "'claude -p --model' accepts, except the word 'default'. "
            + "Omit it — or pass 'default', which states no override rather than naming a model — and "
            + "the node's per-role default, then the project default, then the platform default decide. "
            + "Reach for it when THIS task is unusual, not to express a standing preference")]
        public string? Model { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        string? project = settings.Project;
        string? objective = settings.Objective;
        string? type = settings.Type;
        string? agentContext = settings.AgentContext;
        string? model = settings.Model;
        IReadOnlyList<string> criteria = settings.Criteria;
        IReadOnlyList<string> blockedBy = settings.BlockedBy;

        if (settings.File.IsNotBlank() && settings.FromIssue.IsNotBlank())
        {
            throw new DomainValidationException(
                "--file and --from-issue both seed a draft, from different places; pass one. "
                + "To adopt an issue and add your own material, use --from-issue with --context.");
        }

        if (settings.File.IsNotBlank())
        {
            if (!System.IO.File.Exists(settings.File))
            {
                throw new DomainNotFoundException($"Task file not found: {settings.File}");
            }

            TaskFileContent file = TaskFileParser.Parse(
                await System.IO.File.ReadAllTextAsync(settings.File, cancellationToken));
            project ??= file.Project;
            objective ??= file.Objective;
            type ??= file.Type;
            agentContext ??= file.AgentContext;
            model ??= file.Model;
            criteria = criteria.Count > 0 ? criteria : file.Criteria;
            blockedBy = blockedBy.Count > 0 ? blockedBy : file.BlockedBy;
        }

        if (project.IsBlank())
        {
            throw new DomainValidationException(settings.FromIssue.IsNotBlank()
                ? "--from-issue reads the issue through the project's own repository, so it needs "
                    + "--project <name>."
                : "A task needs a project (--project or 'project:' in the file).");
        }

        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        ProjectDetails projectDetails = await ProjectResolver.ResolveAsync(session, project, cancellationToken);
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        // Everything that can be refused is refused before anything is asked of the human: a
        // mistyped dependency, an unknown --type, a --model that could not be spawned, or an
        // issue that cannot be adopted should not cost someone the acceptance criteria they had
        // just finished typing. The decider checks the type and the model again when it builds
        // the event, which is where the rule belongs; this asks it early, on the near side of
        // the prompts, because that is where the human's typing sits.
        Guid[] dependencies = await ResolveDependenciesAsync(session, blockedBy, cancellationToken);
        TaskType taskType = TaskType.Parse(type);
        AgentModel taskModel = TaskDecider.VetModel(AgentModel.FromInput(model));

        ImportedWorkItem? imported = settings.FromIssue.IsBlank()
            ? null
            : await AdoptAsync(session, projectDetails, settings.FromIssue, cancellationToken);
        if (imported is not null)
        {
            objective = ChooseObjective(objective, imported);
            agentContext = WorkItemContext.Compose(imported, agentContext);
            criteria = criteria.Count > 0 ? criteria : AskForCriteria(imported);
        }

        Guid taskId = DomainId.New();
        TaskAdded added = TaskDecider.Add(
            taskId,
            projectDetails.Id,
            objective ?? string.Empty,
            criteria,
            taskType,
            agentContext,
            constraints: null,
            imported?.Reference,
            DateTimeOffset.UtcNow,
            context.OwnerId,
            taskModel,
            dependencies);
        session.Events.StartStream<TaskAggregate>(taskId, added);

        await session.SaveChangesAsync(cancellationToken);

        // No doorbell: a draft is invisible to the dispatcher by design, so there is nothing
        // for a daemon to wake up for until a human publishes and assigns it (log #34).
        string modelNote = added.Model is { } chosen && chosen != AgentModel.Unknown
            ? $" [dim]on {chosen.Value.EscapeMarkup()}[/]"
            : string.Empty;
        AnsiConsole.MarkupLine(
            $"[blue]Draft created[/] in '{projectDetails.Name.EscapeMarkup()}': " +
            $"{ExternalText.OneLineMarkup(added.Objective)}{modelNote} [dim]({taskId})[/]");
        if (imported is not null)
        {
            AnsiConsole.MarkupLine(
                $"[dim]  adopted {imported.Reference.ToString().EscapeMarkup()}, "
                + $"{imported.Status.ToString().EscapeMarkup()} when read at "
                + $"{imported.ObservedStamp}[/]");
        }

        if (dependencies.Length > 0)
        {
            AnsiConsole.MarkupLine(
                $"[dim]  blocked by {dependencies.Length} task(s): " +
                $"{string.Join(", ", dependencies.Select(TaskListCommand.ShortId))}[/]");
        }

        string shortId = TaskListCommand.ShortId(taskId);
        if (imported is not null && added.AcceptanceCriteria.Count == 0)
        {
            AnsiConsole.MarkupLine(
                "[yellow]No acceptance criteria.[/] [dim]The issue body became agent context; criteria "
                + "are the readiness contract (PLAN.md §4) and Hall9k will not invent them from it.[/]");
        }

        AnsiConsole.MarkupLine(added.AcceptanceCriteria.Count == 0
            ? $"[dim]Next:[/] h9k task revise {shortId} --criteria \"…\" [dim]then[/] h9k task publish {shortId}"
            : $"[dim]Next:[/] h9k task publish {shortId} [dim](a draft never dispatches; publishing then assigning is what starts it)[/]");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Adopt an existing external item (PLAN.md §3.1a): read it through the resolver seam, then
    /// refuse a second adoption of the same item.
    /// </summary>
    private static async Task<ImportedWorkItem> AdoptAsync(
        IQuerySession session, ProjectDetails project, string reference, CancellationToken cancellationToken)
    {
        ImportedWorkItem imported = await WorkItemImporter.Default.ImportAsync(
            new WorkItemImportRequest(WorkItemProvider.GitHub, reference, project.RepositoryPath),
            cancellationToken);

        await RefuseSecondAdoptionAsync(session, imported.Reference, cancellationToken);
        return imported;
    }

    /// <summary>
    /// One live task per item. Adoption is selective rather than mirroring (PLAN.md §3.1a), so a
    /// second task against the same issue is two records of one piece of work with two sets of
    /// runs, and the second one to close out would quietly contradict the first.
    /// <para>
    /// The check runs on the canonical reference the fetch returned rather than on what the
    /// human typed, because "42", "owner/repo#42" and the browser URL all name the same issue
    /// and only the canonical form makes that visible.
    /// </para>
    /// <para>
    /// An abandoned task does not count. The reason to refuse is the contradiction two closeouts
    /// would make, and a task a human walked away from will never close out or run again, so
    /// holding the issue hostage to it would leave the work permanently unadoptable with nothing
    /// gained. Failed is refused like any other live task: it is a waypoint rather than an ending
    /// (Decisions Log #27), with retry, resolve and abandon still open on the task that has the
    /// issue, and abandoning it is exactly how a human says they are done with it.
    /// </para>
    /// <para>
    /// Done still holds the reference, and the refusal says so without offering a way out that
    /// does not exist. Abandon is refused on a terminal task (<c>TaskDecider.Abandon</c>), so
    /// telling the human to abandon a Done holder — the case a reopened GitHub issue lands on —
    /// would send them to a second refusal. Whether closing out should release the item is a
    /// policy question this command does not get to answer; what it can do is name the one route
    /// that works.
    /// </para>
    /// </summary>
    internal static async Task RefuseSecondAdoptionAsync(
        IQuerySession session, ExternalReference reference, CancellationToken cancellationToken)
    {
        string canonical = reference.ToString();
        // The state is matched as SQL rather than compared in LINQ, which is how every state filter
        // in this repo is written (DispatchEngine, TaskDependencyResolver): TaskState is a value
        // object, and Marten refuses to translate a comparison against one.
        TaskListItem? existing = await session.Query<TaskListItem>()
            .Where(task => task.ExternalReference == canonical)
            .Where(task => task.MatchesSql("d.data ->> 'state' <> ?", TaskState.Abandoned.Value))
            .OrderBy(task => task.AddedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is null)
        {
            return;
        }

        string shortId = TaskListCommand.ShortId(existing.Id);
        throw new DomainConflictException(
            $"{canonical} is already adopted by task {shortId} ({ExternalText.OneLine(existing.Objective)}), "
            + $"which is {existing.State.Value}. Adoption is selective, never mirroring (PLAN.md §3.1a), so one "
            + $"issue is one live task: see it with h9k task show {shortId}, then {Remedy(existing.State, shortId)}");
    }

    /// <summary>
    /// The title is a seed, not the objective: an issue title is written for humans browsing a
    /// board, and Hall9k's objective is one outcome-phrased sentence. An explicit --objective
    /// wins outright; a terminal gets the seed as a prefilled default to accept or rewrite;
    /// a script gets the seed, which is the honest thing to do with an unattended one.
    /// <para>
    /// A seed can be empty — an issue with no title, or one whose title was nothing but
    /// characters <see cref="ObjectiveSeed"/> drops — and an empty objective is refused by the
    /// decider. That refusal has to happen here, before the criteria prompt: reached later it
    /// would land after the human has typed out a whole acceptance contract, and take it with it
    /// when the command exits. So an empty seed offers no default to press enter on, and an
    /// unattended run is refused outright rather than left to fail two steps further on.
    /// </para>
    /// </summary>
    private static string ChooseObjective(string? objective, ImportedWorkItem imported)
    {
        if (objective.IsNotBlank())
        {
            return objective;
        }

        string seed = ObjectiveSeed(imported.Title);
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            return seed.IsNotBlank()
                ? seed
                : throw new DomainValidationException(
                    $"{ExternalText.OneLine(imported.Reference.ToString())} has no title Hall9k "
                    + "can seed an objective from"
                    + $" (the source reported '{ExternalText.OneLine(imported.Title)}'), and nothing "
                    + "here can ask you for one. Pass the objective yourself: "
                    + "h9k task add --project <name> --from-issue <ref> --objective \"…\"");
        }

        TextPrompt<string> prompt = new(ObjectivePrompt(imported.Title));
        return AnsiConsole.Prompt(seed.IsBlank()
            ? prompt
            : prompt.DefaultValue(seed).HideDefaultValue());
    }

    /// <summary>
    /// What the human can actually do about it, which depends on where the holding task ended up.
    /// A live task has an exit: abandoning it is how a human says they are done with the work, and
    /// it releases the item. A terminal one does not — <c>TaskDecider.Abandon</c> refuses an
    /// already-Done or already-Abandoned task — so naming abandon there would be a suggestion that
    /// fails on the next command, which is the one error shape an agent cannot self-correct from
    /// (AGENTS.md, failures print why). The separate task is the route that works, so it leads.
    /// <para>
    /// Abandoned is here for completeness rather than because it is reachable: the query above
    /// filters those out, so the only terminal holder that gets this far is a Done one — a
    /// reopened GitHub issue whose first adoption already closed out.
    /// </para>
    /// </summary>
    private static string Remedy(TaskState state, string shortId) => state.IsTerminal
        ? $"write a separate task for the new work with h9k task add --objective \"…\". Task {shortId} "
            + $"is {state.Value} and cannot be abandoned to release the item: a task that has already "
            + "ended stays the record of what was done against it."
        : $"abandon it with h9k task abandon {shortId} if it is finished with, or write a separate "
            + "task with h9k task add --objective \"…\".";

    /// <summary>
    /// The title folded to one line of printable text, with its closing keywords defused. This is
    /// the one place adopted text is sanitised on its way into storage rather than on its way to a
    /// terminal, and it is not the exception it looks like: what is stored verbatim is the item,
    /// and the item is kept whole in the agent context (WorkItemContext.Compose) and in the
    /// reference. The objective is Hall9k's own field, seeded from the title and edited by hand,
    /// and it is not read only by terminals — it becomes the pull request's title, the branch's
    /// slug, and a line in every agent prompt, none of which sanitise anything. A seed that cannot
    /// be a sentence should not become an objective in the first place.
    /// <para>
    /// The keywords are defused here rather than only where the objective is rendered, because
    /// rendering is not the only way out. The daemon defuses the pull request's title and body,
    /// but this repository merges fast-forward, so the agent's <em>own</em> commit subjects land
    /// on the default branch — and an agent naturally opens a commit with its task's headline. An
    /// objective reading "Fix login timeout, resolves #500" would close issue 500 at merge without
    /// the platform having written a word of it. Defused at the seed, the keyword is already dead
    /// in the only copy an agent ever reads, and the daemon's pass over it becomes a second,
    /// idempotent one (<see cref="RelayedText.WithoutClosingKeywords"/> leaves a reference already
    /// inside a code span alone).
    /// </para>
    /// <para>
    /// The prompt's default is this same seed, so what the human accepts by pressing enter is
    /// exactly what they were shown — backticks included, and editable like the rest of it.
    /// </para>
    /// </summary>
    internal static string ObjectiveSeed(string title) =>
        RelayedText.WithoutClosingKeywords(ExternalText.OneLine(title).Trim());

    /// <summary>
    /// The prompt line, with the title escaped into it by hand rather than left to Spectre's own
    /// default-value rendering: Spectre composes that suffix as markup and hands the whole line to
    /// <c>Markup(...)</c>, so a title in the very common <c>[[BUG]] …</c> shape is read as a style
    /// name and throws before the human ever sees the prompt. What it shows is
    /// <see cref="ObjectiveSeed"/>, the same string Spectre returns when the human presses enter,
    /// so the line reads as exactly what the draft will record. When there is no seed there is no
    /// default either, so the line says so rather than offering empty parentheses to accept.
    /// <para>
    /// Escaping the markup is only half of it. <c>EscapeMarkup()</c> neutralises Spectre's
    /// syntax; the title is a value GitHub reported, so it can carry the terminal's own — an
    /// escape sequence or a newline that repaints or writes under the very question the human
    /// is being asked. <see cref="ExternalText.OneLine"/> runs first for that.
    /// </para>
    /// </summary>
    internal static string ObjectivePrompt(string title)
    {
        string seed = ObjectiveSeed(title);
        return seed.IsBlank()
            ? "[bold]Objective[/] [dim](the issue has no title to seed one from, so there is "
                + "nothing to accept; type it)[/]:"
            : "[bold]Objective[/] [dim](from the issue title; edit it or press enter)[/] "
                + $"[green]({seed.EscapeMarkup()})[/]:";
    }

    /// <summary>
    /// The heading the criteria prompt is asked under: the item's title, made safe to print the
    /// same way <see cref="ObjectivePrompt"/> makes it safe.
    /// </summary>
    internal static string CriteriaHeading(string title) =>
        $"[bold]{ExternalText.OneLineMarkup(title)}[/]";

    /// <summary>
    /// The interactive gap this command exists to open. An issue body describes what someone
    /// wants; acceptance criteria state what would make it done, and the difference is the
    /// readiness contract (PLAN.md §4). Deriving one from the other is exactly the kind of
    /// plausible reconstruction the never-guess rule forbids, so the human types them or the
    /// draft goes out without them — and a draft without criteria simply cannot be published,
    /// which is the gate doing its job rather than a silent pass.
    /// </summary>
    private static IReadOnlyList<string> AskForCriteria(ImportedWorkItem imported)
    {
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            return [];
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(CriteriaHeading(imported.Title));
        AnsiConsole.MarkupLine(
            "[dim]The issue body is now this task's agent context. Acceptance criteria are not in it: "
            + "they are the readiness contract, and Hall9k does not invent them from a description.[/]");
        AnsiConsole.MarkupLine("[dim]Type one criterion per line; an empty line ends the list.[/]");

        List<string> criteria = [];
        while (true)
        {
            string entry = AnsiConsole.Prompt(
                new TextPrompt<string>($"  [green]{criteria.Count + 1}.[/]").AllowEmpty());
            if (entry.IsBlank())
            {
                return criteria;
            }

            criteria.Add(entry.Trim());
        }
    }

    /// <summary>
    /// Dependency ids as typed: full ids or unambiguous fragments, resolved now so a typo is
    /// refused at creation rather than becoming an edge that names nothing.
    /// </summary>
    private static async Task<Guid[]> ResolveDependenciesAsync(
        IQuerySession session, IReadOnlyList<string> blockedBy, CancellationToken cancellationToken)
    {
        List<Guid> dependencies = [];
        foreach (string reference in blockedBy.Where(value => value.IsNotBlank()))
        {
            dependencies.Add(await TaskIdResolver.ResolveAsync(session, reference, cancellationToken));
        }

        return [.. dependencies];
    }
}

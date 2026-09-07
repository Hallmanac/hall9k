using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Text;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
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

        [CommandOption("--stacked-on <TASK>")]
        [Description(
            "Declare this task STACKED ON that blocker rather than merely blocked by it: its id or an "
            + "unambiguous fragment. A stacked task dispatches as soon as its parent reaches Delivered "
            + "(pull request open, internal review done) instead of waiting for the merge, cuts its "
            + "branch from the parent's branch head, opens its pull request against that branch — "
            + "forming a GitHub stack — and is retargeted onto the base branch automatically when the "
            + "parent merges. Reserve it for slices of one feature that are genuinely cohesive; the "
            + "tool never infers a stack from an ordinary --blocked-by, and a plain --blocked-by task "
            + "behaves exactly as before. Implies the dependency edge, so it needs no separate "
            + "--blocked-by for the same task")]
        public string? StackedOn { get; init; }

        [CommandOption("--stacked-on-pull-request <NUMBER>")]
        [Description(
            "Declare this task STACKED ON a pull request on this project's repository that another "
            + "install owns — a teammate's, on their own node — rather than on a task in this "
            + "install's records: the number as GitHub shows it (264 or #264). Everything --stacked-on "
            + "does, driven by the pull request instead of a local task: the pull request being OPEN is "
            + "the parent's Delivered, so the child dispatches then, cuts its branch from origin's copy "
            + "of the pull request's head branch, opens its own pull request against that branch, and is "
            + "retargeted onto the base branch automatically when the parent merges. The state is read "
            + "on the closeout watcher's cadence, so a hold can lag a few minutes behind GitHub. No "
            + "--blocked-by goes with it — there is no local task to name — and no task needs to exist "
            + "for that pull request. Mutually exclusive with --stacked-on: a child stands on one parent")]
        public string? StackedOnPullRequest { get; init; }

        [CommandOption("--type <TYPE>")]
        [Description(
            "feature | bugfix | refactor | chore | research | pr-review. pr-review is set for you by "
            + "--from-pr and needs no explicit --type of its own")]
        public string? Type { get; init; }

        [CommandOption("--context <CONTEXT>")]
        [Description("Agent-facing context (pointers, constraints, boundaries)")]
        public string? AgentContext { get; init; }

        [CommandOption("--file <PATH>")]
        [Description(
            "Task file: frontmatter (project/type/objective/criteria/model/blocked-by/stacked-on/"
            + "stacked-on-pull-request/epic) + markdown "
            + "body as agent context")]
        public string? File { get; init; }

        [CommandOption("--from-issue <NUMBER-OR-URL>")]
        [Description(
            "Adopt an existing GitHub issue (PLAN.md §3.1a): the number (42 or #42), the owner/repo#42 "
            + "shorthand, or the issue URL on github.com. Read through the gh CLI from the project's "
            + "repository, so it uses your GitHub login and no token of Hall9k's. The title seeds the "
            + "objective and the body becomes agent context; the issue is recorded as the task's external "
            + "reference and rendered as a link by h9k task show. Acceptance criteria are NEVER "
            + "read out of an issue body — they are the readiness contract, so you supply them with "
            + "--criteria or at the prompt. An issue another hall9k install PUBLISHED is the one "
            + "exception, and not really one: it carries a machine-readable task record holding the "
            + "criteria their owner already wrote, plus the agent context, type, model, caps, "
            + "dependencies (as issue numbers) and epic, and adoption reconstructs the whole draft "
            + "from it rather than asking. That record is read once here and never re-checked, so "
            + "the origin's later revisions reach this copy only by adopting again. Only an issue "
            + "the source reports as open is adopted, so a closed or missing one is refused; the "
            + "state read at import is recorded as an observation of that moment, never re-checked "
            + "afterwards")]
        public string? FromIssue { get; init; }

        [CommandOption("--from-jira <KEY-OR-URL>")]
        [Description(
            "Adopt an existing Jira card (PLAN.md §3.1a): the key (PROJ-123) or the card's URL. Read "
            + "through the registered Jira connection (h9k connection add jira), which is the account "
            + "Hall9k signs in as — it holds no credentials of its own. The summary seeds the objective "
            + "and the description becomes agent context; the card key is recorded as the task's external "
            + "reference and rendered as a link by h9k task show. Acceptance criteria are NEVER read out "
            + "of a card description — they are the readiness contract, so you supply them with --criteria "
            + "or at the prompt. Only a card whose status category is open is adopted, so a closed or "
            + "missing one is refused; the state read at import is recorded as an observation of that "
            + "moment, never re-checked afterwards")]
        public string? FromJira { get; init; }

        [CommandOption("--from-pr <NUMBER-OR-URL>")]
        [Description(
            "Adopt an existing pull request to review on the owner's behalf (pr-review task type): the "
            + "number (42 or #42), the owner/repo#42 shorthand, or the pull request URL on github.com. "
            + "Read through the gh CLI from the project's repository. The title seeds the objective and "
            + "the description becomes agent context, exactly as --from-issue does for an issue; a Jira "
            + "card or GitHub issue the pull request itself references (a closing keyword, or a Jira key "
            + "in the title or body) is imported alongside it, best-effort, as further agent context. "
            + "Implies --type pr-review — pass it explicitly only if you like, and never a different type. "
            + "Acceptance criteria are never read out of the pull request; supply them with --criteria or "
            + "at the prompt. Only a pull request the source reports as open is adopted; the state read at "
            + "import is recorded as an observation of that moment, never re-checked afterwards. The run "
            + "this task dispatches never writes to the pull request or the remote in any form")]
        public string? FromPr { get; init; }

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

        [CommandOption("--epic <EPIC>")]
        [Description(
            "The epic this task joins: its id or an unambiguous fragment (h9k epic list shows them "
            + "all). Must be Open and belong to the same --project; a closed or another project's "
            + "epic is refused. Optional — a task belongs to at most one epic, and most tasks belong "
            + "to none")]
        public string? Epic { get; init; }

        [CommandOption("--review-stage-composition <COMPOSITION|default>")]
        [Description(
            "This task's own review stage composition, overriding every other level of the chain "
            + "(task: the review pipeline's stage composition becomes configuration recorded per run) "
            + "— full-pipeline, adversarial-only, conformance-only, skip-final-pass, or none. Resolved "
            + "once at this task's run dispatch and frozen for that run's whole lifetime. Omit it — or "
            + "pass 'default', which states no override — and the project's, then the node's, then the "
            + "compiled default (full-pipeline) decide. skip-final-pass and none waive Decisions Log #92's "
            + "mandatory pre-merge fresh-context read; adversarial-only, conformance-only, and none each "
            + "drop a lens's own attention budget entirely — every one of those needs "
            + "--accept-reduced-review.")]
        public string? ReviewStageComposition { get; init; }

        [CommandOption("--accept-reduced-review")]
        [Description(
            "Acknowledges the consequence --review-stage-composition just named, when the value passed "
            + "removes a load-bearing review guarantee. Required for skip-final-pass, none, "
            + "adversarial-only, or conformance-only; passed alongside any other value it is silently "
            + "dropped rather than refused, since there is no consequence to acknowledge there.")]
        public bool AcceptReducedReview { get; init; }

        [CommandOption("--pre-approved [MODE]")]
        [Description(
            "Give this task standing pre-approval from the start (task: a task can be published "
            + "pre-approved): the owner stops being a synchronous gate at the pull request, and the "
            + "daemon merges it on its own once GitHub's own gates read satisfied. The bare flag, or "
            + "on, means exactly that; after-human-review holds the same automatic merge until a human "
            + "reviewer has actually been requested on the pull request and every requested reviewer has "
            + "approved the current head; off is the default. Add the reviewers you want in GitHub — "
            + "hall9k stores no reviewer setting and requests no reviews. h9k task publish carries what "
            + "is granted here forward rather than clearing it. Most useful when adopting an issue whose "
            + "task record says how the ORIGIN install answered pre-approval for its copy — that is a "
            + "fact about the other install, and this is how this one gives its own answer")]
        public FlagValue<string> PreApproved { get; init; } = new();
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (settings.AcceptReducedReview && settings.ReviewStageComposition is null)
        {
            throw new DomainValidationException(
                "--accept-reduced-review has nothing to acknowledge without --review-stage-composition.");
        }

        string? project = settings.Project;
        string? objective = settings.Objective;
        string? type = settings.Type;
        string? agentContext = settings.AgentContext;
        string? model = settings.Model;
        string? epic = settings.Epic;
        IReadOnlyList<string> criteria = settings.Criteria;
        IReadOnlyList<string> blockedBy = settings.BlockedBy;
        string? stackedOn = settings.StackedOn;
        string? stackedOnPullRequest = settings.StackedOnPullRequest;

        AdoptionSource? adoption = ChooseSource(settings);
        if (settings.File.IsNotBlank() && adoption is { } seeded)
        {
            throw new DomainValidationException(
                $"--file and {seeded.Option} both seed a draft, from different places; pass one. "
                + $"To adopt {seeded.Article} {seeded.Noun} and add your own material, use "
                + $"{seeded.Option} with --context.");
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
            epic ??= file.Epic;
            criteria = criteria.Count > 0 ? criteria : file.Criteria;
            blockedBy = blockedBy.Count > 0 ? blockedBy : file.BlockedBy;
            stackedOn ??= file.StackedOn;
            stackedOnPullRequest ??= file.StackedOnPullRequest;
        }

        if (project.IsBlank())
        {
            throw new DomainValidationException(adoption is { } source
                ? $"{source.Option} files the {source.Noun} against a project, so it needs "
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
        // A stacked edge is also a dependency edge — the invariant TaskDecider.VetStackedEdge
        // enforces — so the option implies the blocked-by rather than making the human type the
        // same id twice. Merged here, on the near side of the prompts, for the same reason every
        // other refusal above is: an unresolvable --stacked-on must not cost someone the
        // acceptance criteria they were about to type.
        Guid? stackedOnId = stackedOn.IsNotBlank()
            ? await TaskIdResolver.ResolveAsync(session, stackedOn, cancellationToken)
            : null;
        if (stackedOnId is { } parentId && !dependencies.Contains(parentId))
        {
            dependencies = [.. dependencies, parentId];
        }

        // The remote form implies no blocked-by, and cannot: there is no local task to name, which
        // is the whole point of it. Parsed here, on the near side of the prompts, for the same
        // reason everything else on this path is.
        int? stackedOnPullRequestNumber = StackedPullRequestOption.Parse(stackedOnPullRequest);
        bool adoptingPullRequest = adoption?.Provider == WorkItemProvider.GitHubPullRequest;
        if (adoptingPullRequest && type.IsBlank())
        {
            type = "pr-review";
        }

        TaskType taskType = TaskType.Parse(type);
        if (adoptingPullRequest && taskType != TaskType.PrReview)
        {
            throw new DomainValidationException(
                $"--from-pr adopts a pull request to review, which is always a pr-review task; --type "
                + $"{type} does not match. Drop --type (pr-review is the default with --from-pr) or pass "
                + "--type pr-review.");
        }

        if (!adoptingPullRequest && taskType == TaskType.PrReview)
        {
            throw new DomainValidationException(
                "A pr-review task reviews an existing pull request, so --type pr-review needs "
                + "--from-pr <url> naming it.");
        }

        AgentModel taskModel = TaskDecider.VetModel(AgentModel.FromInput(model));
        // Vetted early, on the near side of the prompts below, for the same reason taskModel is
        // (adversarial review, cycle 1 — the comment above this one says why): refusing an
        // unusable value only at the decider would throw away the objective and criteria a human
        // just finished typing.
        string? reviewStageComposition = TaskDecider.VetReviewStageComposition(
            settings.ReviewStageComposition, settings.AcceptReducedReview, "--review-stage-composition");
        // Vetted here too, on the near side of AdoptAsync's own gh call and the criteria prompt
        // below, for the same reason the vet above is (independent pre-PR review, cycle 1,
        // conformance lens): refusing this mismatch only at the decider would pay for a real
        // network call and re-typed criteria and then throw both away.
        Guid taskId = DomainId.New();
        TaskDecider.RefuseCompositionOnPrReview(taskType, reviewStageComposition);
        // Vetted on the near side of the prompts for the identical reason, and with the identical
        // arguments the decider itself will re-vet with below: a stacked edge on a pr-review task,
        // or one naming this task itself, is refused before AdoptAsync's own gh call and the
        // criteria prompt are paid for.
        TaskDecider.VetStackedEdge(taskId, stackedOnId, stackedOnPullRequestNumber, dependencies, taskType);
        Guid? epicId = epic.IsNotBlank()
            ? await EpicIdResolver.ResolveForMembershipAsync(session, epic, projectDetails.Id, cancellationToken)
            : null;

        ImportedWorkItem? imported = adoption is null
            ? null
            : await AdoptAsync(session, projectDetails, adoption, cancellationToken);
        TaskRecord? record = imported is not null && adoption is not null
            ? TaskRecordAdoption.Read(adoption.Provider, imported)
            : null;
        TaskRecordAdoption.Resolution? resolution = null;
        // What the record came to, held for the adoption output and for the cap events below: it
        // carries the values this build could not use — a type, a model, a cap outside this build's
        // floors — because the record degrades rather than refusing, and a degrade nobody is told
        // about is the failure (AGENTS.md, never guess at unobserved facts).
        TaskRecordAdoption.Reconstruction? reconstructed = null;
        if (record is not null && imported is not null)
        {
            // The record is the whole task the origin published, so there is no seed to confirm and
            // nothing to ask at the prompt: criteria are criteria rather than context, and the
            // context body is the agent context verbatim. An explicit flag on this command line
            // still wins over all of it — adopting is not the same as surrendering the local call.
            resolution = await TaskRecordAdoption.ResolveAsync(
                session, record, imported.Reference, projectDetails.Id, cancellationToken);
            reconstructed = TaskRecordAdoption.Reconstruct(
                record,
                resolution,
                new TaskRecordAdoption.Overrides(
                    objective, criteria, agentContext, type, model, epic, blockedBy),
                imported.Reference.ToString());

            objective = reconstructed.Objective;
            criteria = reconstructed.Criteria;
            agentContext = reconstructed.AgentContext;
            if (reconstructed.Type is { } recordType)
            {
                taskType = TaskType.Parse(recordType);
            }

            if (reconstructed.Model is { } recordModel)
            {
                taskModel = TaskDecider.VetModel(AgentModel.FromInput(recordModel));
            }

            epicId ??= reconstructed.EpicId;
            // The record's edges join whatever this command line already declared rather than
            // replacing it: an explicit --blocked-by states the set outright (and then the
            // reconstruction hands back none), and a --stacked-on parent is already in there and
            // must stay.
            dependencies = [.. dependencies, .. reconstructed.Dependencies.Where(id => !dependencies.Contains(id))];

            TaskDecider.VetStackedEdge(taskId, stackedOnId, stackedOnPullRequestNumber, dependencies, taskType);
        }
        else if (imported is not null && adoption is not null)
        {
            objective = ChooseObjective(objective, imported, adoption);
            string? linkedContext = adoptingPullRequest
                ? await LinkedWorkItemImport.TryImportContextAsync(session, projectDetails, imported, cancellationToken)
                : null;
            string? additional = linkedContext.IsNotBlank() && agentContext.IsNotBlank()
                ? $"{linkedContext}\n\n{agentContext}"
                : linkedContext.IsNotBlank() ? linkedContext : agentContext;
            agentContext = WorkItemContext.Compose(imported, additional);
            criteria = criteria.Count > 0 ? criteria : AskForCriteria(imported, adoption);
        }

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
            dependencies,
            epicId: epicId,
            reviewStageComposition: reviewStageComposition,
            reviewStageCompositionAcknowledged: settings.AcceptReducedReview,
            stackedOnTaskId: stackedOnId,
            stackedOnPullRequestNumber: stackedOnPullRequestNumber,
            preApproval: PreApprovalInput.FromFlag(settings.PreApproved),
            // Recorded only when the record actually named the origin's own task. A hand-written
            // block that says nothing about where it came from leaves this null rather than an
            // origin with empty ids, which would read as a mirror of nowhere (AGENTS.md, never
            // guess at unobserved facts).
            origin: record?.Origin is { } candidate && candidate.TaskId != Guid.Empty ? candidate : null);
        session.Events.StartStream<TaskAggregate>(taskId, added);
        AppendRecordCaps(
            session, taskId, added, reconstructed?.Caps ?? TaskRecordCaps.None, context.OwnerId);

        await session.SaveChangesAsync(cancellationToken);

        // No doorbell: a draft is invisible to the dispatcher by design, so there is nothing
        // for a daemon to wake up for until a human publishes and assigns it (log #34).
        string modelNote = added.Model is { } chosen && chosen != AgentModel.Unknown
            ? $" [dim]on {chosen.Value.EscapeMarkup()}[/]"
            : string.Empty;
        AnsiConsole.MarkupLine(
            $"[blue]Draft created[/] in '{projectDetails.Name.EscapeMarkup()}': " +
            $"{ExternalText.OneLineMarkup(added.Objective)}{modelNote} [dim]({taskId})[/]");
        // The refusal path names the consequence (VetReviewStageComposition above); the accepted
        // path has to name it too, or the only operator who ever reads it is the one who tried
        // the command without --accept-reduced-review first (task: removing a load-bearing
        // guarantee names the decision it overrides at set time and requires the consequence to
        // be acknowledged in the command's own output; independent pre-PR review, cycle 1, both
        // lenses). ReviewStageCompositionAcknowledged is already clamped true only when a value
        // that genuinely needed it was actually accepted, so ReviewStageComposition is guaranteed
        // non-null here.
        if (added.ReviewStageCompositionAcknowledged
            && ReviewStageCompositionValidation.DescribeAcceptedConsequence(
                ReviewStageComposition.FromInput(added.ReviewStageComposition)) is { Length: > 0 } consequence)
        {
            AnsiConsole.MarkupLineInterpolated($"[dim]review-stage-composition consequence: {consequence}[/]");
        }
        if (imported is not null)
        {
            AnsiConsole.MarkupLine(
                $"[dim]  adopted {imported.Reference.ToString().EscapeMarkup()}, "
                + $"{ExternalText.OneLineMarkup(imported.Status.ToString())} when read at "
                + $"{imported.ObservedStamp}[/]");
        }

        if (record is not null && resolution is not null && reconstructed is not null)
        {
            AnnounceRecord(
                record, resolution, reconstructed, projectDetails.Name, added,
                shortId: TaskListCommand.ShortId(taskId));
        }
        else if (added.EffectivePreApproval.MergesAutomatically)
        {
            // Named on the accepting path too, not only where a record made it a comparison: this
            // removes the owner as a synchronous gate at the merge, and a flag that quietly does
            // that is a flag somebody will be surprised by later.
            AnsiConsole.MarkupLine(
                $"[dim]  pre-approved ({PreApprovalInput.Word(added.EffectivePreApproval)}): "
                + $"{PreApprovalInput.Describe(added.EffectivePreApproval)} — h9k task set-pre-approved "
                + "to change.[/]");
        }

        if (epicId is { } joinedEpic)
        {
            AnsiConsole.MarkupLine($"[dim]  in epic {TaskListCommand.ShortId(joinedEpic)}[/]");
        }

        if (dependencies.Length > 0)
        {
            AnsiConsole.MarkupLine(
                $"[dim]  blocked by {dependencies.Length} task(s): " +
                $"{string.Join(", ", dependencies.Select(TaskListCommand.ShortId))}[/]");
        }

        // Said out loud because it is the one edge with no blocked-by line above to give it away,
        // and because the release is not immediate: the pull request's state is read on the
        // closeout watcher's cadence, so a human who assigns this straight away needs to know why
        // it sits Blocked for a few minutes rather than dispatching (task: a stacked child can
        // stand on a pull request another install owns). The assignment is named as the thing that
        // starts the watch, not just as the next lifecycle step: the sweep reads a remote parent
        // only for an assigned task (RemoteStackedParentSweep.SweepOnceAsync), so a task left
        // Published is watched by nothing at all (independent pre-PR review, cycle 1, adversarial
        // lens — the class sweep off the same defect in the claim refusal).
        if (stackedOnPullRequestNumber is { } remoteParent)
        {
            AnsiConsole.MarkupLine(
                $"[dim]  stacked on pull request #{remoteParent} — assign it and it dispatches once that pull "
                + "request is observed open, which the closeout watcher's own sweep looks for on its cadence. "
                + "Nothing looks at that pull request until the task is assigned[/]");
        }

        string shortId = TaskListCommand.ShortId(taskId);
        if (imported is not null && added.AcceptanceCriteria.Count == 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]No acceptance criteria.[/] [dim]The {adoption?.Noun ?? "item"} description became "
                + "agent context; criteria are the readiness contract (PLAN.md §4) and Hall9k will not "
                + "invent them from it.[/]");
        }

        AnsiConsole.MarkupLine(added.AcceptanceCriteria.Count == 0
            ? $"[dim]Next:[/] h9k task revise {shortId} --criteria \"…\" [dim]then[/] h9k task publish {shortId}"
            : $"[dim]Next:[/] h9k task publish {shortId} [dim](a draft never dispatches; publishing then assigning is what starts it)[/]");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Carry the origin's cap overrides onto the adopted copy. They are separate events rather than
    /// fields on <see cref="TaskAdded"/> because that is what they are everywhere else in the
    /// platform — deliberately state-agnostic, settable mid-run — and adoption is not the place to
    /// invent a second way to record them.
    /// <para>
    /// <paramref name="caps"/> are the record's caps as
    /// <see cref="TaskRecordAdoption.Reconstruction.Caps"/> vetted them, never the record's own: a
    /// value outside this build's floors is already dropped there and named in the adoption output,
    /// because a cap this build cannot set is worth one degraded field and not the whole adoption
    /// (independent pre-PR review, cycle 1, both lenses).
    /// </para>
    /// </summary>
    internal static void AppendRecordCaps(
        IDocumentSession session, Guid taskId, TaskAdded added, TaskRecordCaps caps, Guid ownerId)
    {
        if (!caps.Any)
        {
            return;
        }

        TaskAggregate task = new();
        task.Apply(added);
        // The adoption's own moment, not a second clock reading: these events are appended to the
        // stream TaskAdded starts, in the same transaction, and they record the same observation —
        // that this install adopted the origin's caps as it created the task. A fresh UtcNow here
        // would stamp them microseconds after the creation they are part of and make the ordering
        // of one transaction's events depend on the clock (external review on PR #276).
        DateTimeOffset now = added.AddedAt;
        if (caps.MaxComplianceReviewCycles is not null || caps.MaxAdversarialReviewCycles is not null
            || caps.MaxFinalFullPassRounds is not null || caps.LifetimeReviewCycleBudget is not null)
        {
            session.Events.Append(taskId, TaskDecider.OverrideReviewCaps(
                task,
                Cap(caps.MaxComplianceReviewCycles),
                Cap(caps.MaxAdversarialReviewCycles),
                Cap(caps.MaxFinalFullPassRounds),
                Cap(caps.LifetimeReviewCycleBudget),
                now,
                ownerId));
        }

        if (caps.SessionCap is { } sessionCap)
        {
            session.Events.Append(taskId, TaskDecider.OverrideSessionCap(task, sessionCap, now, ownerId));
        }

        // A cap the record did not name is left alone rather than cleared: absent means "the origin
        // stated no override", and the levels above this task decide it here exactly as they did
        // there.
        static Optional<int?> Cap(int? value) =>
            value is null ? Optional<int?>.None : Optional<int?>.Of(value);
    }

    /// <summary>
    /// What the record actually gave this draft, said plainly — including every field it could not
    /// give it. The snapshot rule is stated outright (Decisions Log #60): this copy is read once
    /// and never re-checked, so the origin's later revisions reach it only by adopting again, and a
    /// human who does not know that will believe they are looking at a live mirror.
    /// </summary>
    private static void AnnounceRecord(
        TaskRecord record,
        TaskRecordAdoption.Resolution resolution,
        TaskRecordAdoption.Reconstruction reconstructed,
        string projectName,
        TaskAdded added,
        string shortId)
    {
        // Every value quoted out of the record goes through ExternalText, not EscapeMarkup alone.
        // The record is text in a GitHub issue body: anybody who can edit that issue authored it,
        // and EscapeMarkup neutralises only Spectre's syntax, never the terminal's own — the same
        // rule ObjectivePrompt's doc comment spells out for an adopted title.
        string origin = record.Origin.NodeName.IsNotBlank()
            ? ExternalText.OneLineMarkup(record.Origin.NodeName)
            : "another install";
        string originTask = record.Origin.TaskId == Guid.Empty
            ? string.Empty
            : $" as task {TaskListCommand.ShortId(record.Origin.TaskId)}";
        AnsiConsole.MarkupLine(
            $"[dim]  read the task record {origin} published{originTask}: objective, "
            + $"{record.Criteria.Count} criteria, agent context, type {ExternalText.OneLineMarkup(record.Type)}"
            + $"{(record.Model.IsNotBlank() ? $", model {ExternalText.OneLineMarkup(record.Model)}" : string.Empty)}"
            + $"{(reconstructed.Caps.Any ? ", caps" : string.Empty)}[/]");
        AnsiConsole.MarkupLine(
            "[dim]  read once, now yours: nothing re-checks the issue, so a later revision on "
            + "the origin reaches this copy only by adopting it again.[/]");

        if (reconstructed.UnrecognizedType.IsNotBlank())
        {
            // The record's forward-compatibility promise is that a later build's record degrades to
            // the fields this one understands rather than refusing the adoption — and this is the
            // field where that degrade is a real change to the work, so it is stated rather than
            // left to be noticed (independent pre-PR review, cycle 1, adversarial lens).
            AnsiConsole.MarkupLine(
                $"[yellow]  Unknown type:[/] [dim]the record says type "
                + $"{ExternalText.OneLineMarkup(reconstructed.UnrecognizedType)}, which this build does not "
                + "know — a later one wrote it. Everything else the record carries was read; this draft took "
                + $"type {added.Type.Value.ToLowerInvariant().EscapeMarkup()} instead. Name the type you want "
                + $"with:[/] h9k task revise {shortId} --type <type>");
        }

        if (reconstructed.UnusableModel.IsNotBlank())
        {
            // Same degrade, same reason it is said out loud: a model name reaches the executor's
            // shell command line, so one this build will not spawn costs the draft its model and
            // nothing else — the node's own default stands, and the operator is told which word
            // was dropped (independent pre-PR review, cycle 1).
            AnsiConsole.MarkupLine(
                $"[yellow]  Unusable model:[/] [dim]the record says model "
                + $"{ExternalText.OneLineMarkup(reconstructed.UnusableModel)}, which this build will not "
                + "spawn. This draft took no model of its own, so the node's default decides. Name the model "
                + $"you want with:[/] h9k task revise {shortId} --model <model>");
        }

        foreach (TaskRecordAdoption.UnusableCap cap in reconstructed.UnusableCaps)
        {
            // A cap outside this build's floors is the same class of degrade, and reachable only
            // from a block written by hand — a record hall9k wrote carries values its own origin
            // validated at set time. One line per cap, each naming the command that sets that cap
            // here, so an operator can self-correct from the message (AGENTS.md, CLI standards).
            AnsiConsole.MarkupLine(
                $"[yellow]  Unusable cap:[/] [dim]the record says {cap.Key} {cap.Value}, which is outside "
                + "what this build accepts, so this task took no override for it and the project or node "
                + "level decides it. Set one here with:[/] "
                + cap.Command(shortId));
        }

        // Both answers named as MODES, not as yes and no: after-human-review is neither, and the
        // whole point of this line is comparing the answer this install just gave against the one
        // the record says the origin gave. An unrecognized word in the record is reported as
        // unrecognized rather than read as off, the same degrade every other field gets.
        PreApprovalMode here = added.EffectivePreApproval;
        PreApprovalMode there = record.PreApproval;
        AnsiConsole.MarkupLine(here.MergesAutomatically
            ? $"[dim]  pre-approved here: {PreApprovalInput.Word(here).EscapeMarkup()}, because you passed "
                + $"--pre-approved (the origin's copy is {OriginsAnswer(there)}).[/]"
            : $"[dim]  pre-approved here: off{(there.MergesAutomatically
                ? $" — the origin's copy is {PreApprovalInput.Word(there).EscapeMarkup()}, which is a fact "
                    + $"about that install. Match it with h9k task publish {shortId} --pre-approved "
                    + PreApprovalInput.Word(there).EscapeMarkup()
                : string.Empty)}.[/]");

        foreach (int issue in resolution.UnresolvedIssues)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]  Unresolved dependency:[/] [dim]the record blocks this task on issue #{issue}, "
                + "and no task here has adopted it. Adopt the parent first, then add the edge:[/] "
                + $"h9k task add --project {projectName.EscapeMarkup()} --from-issue {issue} "
                + $"[dim]then[/] h9k task revise {shortId} --blocked-by <that task>");
        }

        if (record.DependenciesWithoutIssues > 0)
        {
            AnsiConsole.MarkupLine(
                $"[dim]  the origin had {record.DependenciesWithoutIssues} more blocker(s) with no issue "
                + "of their own, so the record could not name them and neither can this copy.[/]");
        }

        if (resolution.UnmatchedEpicTitle is { } epicTitle)
        {
            string title = ExternalText.OneLineMarkup(epicTitle);
            // "joined none" only when it really joined none: an explicit --epic wins over the
            // record (Reconstruct hands back no epic when one was named), so this task can have
            // joined an epic the operator chose while the record's own title still matched nothing
            // here. Printing the create-and-join guidance there contradicted the "in epic <id>"
            // line two lines below it (independent pre-PR review, cycle 1, conformance lens).
            AnsiConsole.MarkupLine(added.EpicId is { } joined
                ? $"[dim]  no open epic titled '{title}' here, which is the epic the record named — this "
                    + $"task joined epic {TaskListCommand.ShortId(joined)}, the one you named instead.[/]"
                : $"[dim]  no open epic titled '{title}' here, so this task joined none. "
                    + "Create it and join:[/] "
                    + $"h9k epic add --project {projectName.EscapeMarkup()} --title \"{title}\" "
                    + $"[dim]then[/] h9k task revise {shortId} --epic <that epic>");
        }
    }

    /// <summary>
    /// How the origin's own answer reads inside the comparison line, in the record's own words. A
    /// mode this build does not recognize is named as unrecognized rather than flattened to "not
    /// pre-approved": the record states what the ORIGIN chose, and a later build's answer read as
    /// off would misreport that install (AGENTS.md, never guess at unobserved facts).
    /// </summary>
    private static string OriginsAnswer(PreApprovalMode origin) => origin.Value switch
    {
        "Off" => "off",
        // TaskRecord.TryParse reads the field through PreApprovalMode.FromInput, which collapses a
        // field that was absent and one carrying a word this build does not know into the same
        // Unknown — so this says neither, rather than picking the one it cannot tell.
        "" => "not stated in a way this build reads",
        _ => $"{PreApprovalInput.Word(origin).EscapeMarkup()} too",
    };

    /// <summary>
    /// Which source a draft is being seeded from, and the words to say about it. It is a type
    /// rather than a flag because every message in this command that used to say "issue" now has
    /// to say either that or "card", and a boolean threaded through six methods is how those
    /// messages drift apart.
    /// </summary>
    private sealed record AdoptionSource(WorkItemProvider Provider, string Reference, string Option, string Noun)
    {
        /// <summary>"an issue", "a card" — English, kept beside the noun it belongs to.</summary>
        public string Article => "aeiou".Contains(char.ToLowerInvariant(Noun[0])) ? "an" : "a";
    }

    /// <summary>
    /// The one source this invocation adopts from, or null when it is not adopting. Two sources
    /// are refused rather than ranked: a task carries one external reference (PLAN.md §3.1a), so
    /// picking a winner would silently drop the other one the human asked for.
    /// </summary>
    private static AdoptionSource? ChooseSource(Settings settings)
    {
        AdoptionSource[] named =
        [
            .. new[]
            {
                new AdoptionSource(WorkItemProvider.GitHub, settings.FromIssue ?? string.Empty, "--from-issue", "issue"),
                new AdoptionSource(WorkItemProvider.Jira, settings.FromJira ?? string.Empty, "--from-jira", "card"),
                new AdoptionSource(WorkItemProvider.GitHubPullRequest, settings.FromPr ?? string.Empty, "--from-pr", "pull request"),
            }.Where(source => source.Reference.IsNotBlank()),
        ];

        return named.Length switch
        {
            0 => null,
            1 => named[0],
            _ => throw new DomainValidationException(
                $"{string.Join(" and ", named.Select(source => source.Option))} each adopt a different "
                + "item, and a task carries one external reference (PLAN.md §3.1a). Pass one, and write "
                + "the second task separately if both pieces of work are real."),
        };
    }

    /// <summary>
    /// Adopt an existing external item (PLAN.md §3.1a): read it through the resolver seam, then
    /// refuse a second adoption of the same item.
    /// <para>
    /// The importer is built from the registered connections rather than from a static default,
    /// because the two sources need opposite things: gh
    /// carries the machine's own login, and Jira needs a site and a token this install registered
    /// (PLAN.md §10). Which is why the seam pays off here rather than only in principle — the
    /// second source cost a provider and a line, not a second import path.
    /// </para>
    /// </summary>
    private static async Task<ImportedWorkItem> AdoptAsync(
        IQuerySession session, ProjectDetails project, AdoptionSource source, CancellationToken cancellationToken)
    {
        WorkItemImporter importer = await WorkItemConnections.ImporterAsync(session, cancellationToken);
        ImportedWorkItem imported = await importer.ImportAsync(
            new WorkItemImportRequest(source.Provider, source.Reference, project.RepositoryPath),
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
    /// <para>
    /// A pr-review task is the one exception, and only once it is Done. Its Done means the review
    /// finished, not that the pull request's own work is done — new commits can land and warrant
    /// another pass, and that second pass is exactly what <c>TaskDecider.Reopen</c> sends the
    /// owner here for (its own pr-review guard refuses the reopen and names this command as the
    /// route). A completed review does not hold its pull request hostage the way adopted work
    /// holds its issue: unlike an issue, closing out the review is not a claim that the pull
    /// request itself is finished, so a second review is not the two-closeouts contradiction this
    /// check otherwise guards against. A live (non-Done) pr-review task still blocks a second
    /// adoption, exactly like any other in-flight task.
    /// </para>
    /// <para>
    /// The Done-pr-review exclusion is applied inside the query rather than to whichever holder
    /// happens to sort first: with the exception in place, more than one non-abandoned task can
    /// now legitimately carry the same reference (a Done pr-review alongside a later live one),
    /// so "oldest by AddedAt" no longer means "the holder that matters." Filtering the excluded
    /// holders out server-side and taking the oldest survivor keeps the guard's promise — any
    /// live holder blocks — regardless of how many completed pr-reviews sort ahead of it.
    /// </para>
    /// </summary>
    internal static async Task RefuseSecondAdoptionAsync(
        IQuerySession session, ExternalReference reference, CancellationToken cancellationToken)
    {
        string canonical = reference.ToString();
        // The state and type are matched as SQL rather than compared in LINQ, which is how every
        // state filter in this repo is written (DispatchEngine, TaskDependencyResolver): TaskState
        // and TaskType are value objects, and Marten refuses to translate a comparison against one.
        TaskListItem? existing = await session.Query<TaskListItem>()
            .Where(task => task.ExternalReference == canonical)
            .Where(task => task.MatchesSql("d.data ->> 'state' <> ?", TaskState.Abandoned.Value))
            .Where(task => task.MatchesSql(
                "NOT (d.data ->> 'type' = ? AND d.data ->> 'state' = ?)",
                TaskType.PrReview.Value, TaskState.Done.Value))
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
    private static string ChooseObjective(string? objective, ImportedWorkItem imported, AdoptionSource source)
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
                    + $"h9k task add --project <name> {source.Option} <ref> --objective \"…\"");
        }

        TextPrompt<string> prompt = new(ObjectivePrompt(imported.Title, source.Noun));
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
    internal static string ObjectivePrompt(string title, string noun = "issue")
    {
        string seed = ObjectiveSeed(title);
        return seed.IsBlank()
            ? $"[bold]Objective[/] [dim](the {noun} has no title to seed one from, so there is "
                + "nothing to accept; type it)[/]:"
            : $"[bold]Objective[/] [dim](from the {noun} title; edit it or press enter)[/] "
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
    private static IReadOnlyList<string> AskForCriteria(ImportedWorkItem imported, AdoptionSource source)
    {
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            return [];
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(CriteriaHeading(imported.Title));
        AnsiConsole.MarkupLine(
            $"[dim]The {source.Noun} description is now this task's agent context. Acceptance criteria "
            + "are not in it: they are the readiness contract, and Hall9k does not invent them from a "
            + "description.[/]");
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

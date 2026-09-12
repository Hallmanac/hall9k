using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Cli.Orchestrator;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Project.Queries;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;

namespace Hall9k.Cli.Commands;

public sealed class ProjectShowCommand : Hall9kAsyncCommand<ProjectShowCommand.Settings>
{
    /// <summary>How many of the project's tasks the pane lists before pointing at h9k task list.</summary>
    private const int RecentTasks = 5;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PROJECT>")]
        [Description("Project name, an unambiguous fragment of it, or the full id (h9k project list shows them all)")]
        public string Project { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        ProjectDetails project = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);
        ConnectionDetails? connection = await session.LoadAsync<ConnectionDetails>(project.ConnectionId, cancellationToken);
        OwnerDetails? owner = await session.LoadAsync<OwnerDetails>(project.OwnerId, cancellationToken);
        OperatingSettings operatingSettings = (await PlatformConfigFile.TryReadOperatingSettingsAsync(cancellationToken)).Settings;

        AnsiConsole.Write(Registration(project, connection, owner));
        AnsiConsole.MarkupLine("\n[bold]Settings[/] [dim](change them with h9k project set "
            + $"{project.Name.EscapeMarkup()} …)[/]");
        ProjectSettingsHistory history = await ProjectSettingsHistory.ReadAsync(session, project.Id, cancellationToken);
        AnsiConsole.Write(SettingsPane(project, operatingSettings, history));

        IReadOnlyList<TaskStatusRow> rows = await TaskStatusComposer.ComposeAllAsync(
            session, DateTimeOffset.UtcNow, cancellationToken);
        WriteTasks(project, [.. rows.Where(row => row.ProjectId == project.Id)]);
        return ExitCodes.Ok;
    }

    private static Table Registration(ProjectDetails project, ConnectionDetails? connection, OwnerDetails? owner)
    {
        Table table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumns("k", "v");
        table.AddRow("[bold]Project[/]", $"[bold]{project.Name.EscapeMarkup()}[/]");
        table.AddRow("Id", $"[dim]{project.Id}[/]");
        // The home leads, because it is the answer to "where do I go to work on this" — the
        // repository path is one thing inside it. A project with none says so and names the
        // command that ends that state, rather than leaving a blank row to interpret.
        table.AddRow("Home", project.HomeDirectory.HasValue
            ? project.HomeDirectory.Value.EscapeMarkup()
              + (Directory.Exists(project.HomeDirectory.Value)
                  ? string.Empty
                  : $" [yellow](not created here — h9k project init {project.Name.EscapeMarkup()})[/]")
            : $"[dim]none recorded — create one: h9k project init {project.Name.EscapeMarkup()}[/]");
        table.AddRow("Repository", project.RepositoryPath.EscapeMarkup());
        table.AddRow("Remote", project.RepositoryUrl is { } url
            ? url.ToString().EscapeMarkup()
            : "[dim]none recorded[/]");
        table.AddRow("Base branch", project.BaseBranch.EscapeMarkup());

        // A project binds to a connection, never to "the machine's GitHub" (PLAN.md §10),
        // so name the binding — and say plainly when the connection is not readable here
        // rather than dressing the id up as an account.
        table.AddRow("Connection", connection is null
            ? $"[dim]id {project.ConnectionId} (no connection document found)[/]"
            : $"{connection.Provider.Value.EscapeMarkup()} · {connection.ExternalAccountId.EscapeMarkup()} "
              + $"[dim]({connection.CredentialReference.EscapeMarkup()})[/]");
        table.AddRow("Owner", owner is null
            ? $"[dim]id {project.OwnerId} (no owner document found)[/]"
            : owner.Name.EscapeMarkup());
        table.AddRow("Registered", $"[dim]{project.RegisteredAt.ToLocalTime():g}[/]");
        return table;
    }

    /// <summary>
    /// The settings pane. <paramref name="history"/> is this project's own recorded settings
    /// changes, which is the only thing that can tell an explicitly chosen value apart from an
    /// initialised default (Decisions Log #161, <see cref="ProjectSettingsHistory"/>): a row
    /// whose effective value would read identically either way prints its origin too — auto
    /// pr-review, the claim gate, skip permissions, the backlog policy and the close-linked-issue
    /// rule — rather than omitting the row, printing a bare "off" that could mean either, or
    /// calling a value "the default" when somebody may have typed it.
    /// <para>
    /// The rows that carry no origin are the ones where it would change nothing a reader does
    /// next: a setting whose untouched state is the absence of configuration, with the node or
    /// nothing at all deciding in its place and a lever printed either way (verify gates, context
    /// links, never-close labels, the Jira board, the four review caps, the stage composition,
    /// the run ceiling), and one that prints a value only <c>h9k project set</c> could have
    /// recorded (commit style, re-request review, the orchestrator model, which names its own
    /// resolution chain outright).
    /// </para>
    /// <para>
    /// Two rows below still name a value "the default" without reading the stream for it — the
    /// dispatch tier and the branch template, both of which have a <c>set</c> spelling that
    /// records exactly their default value. Neither is this change's (independent pre-PR review,
    /// cycle 1, conformance lens: named rather than fixed here, since they are pre-existing and
    /// this branch's diff does not touch them), and both would take the same
    /// <see cref="OriginNote"/> a row above uses.
    /// </para>
    /// </summary>
    private static Table SettingsPane(
        ProjectDetails project, OperatingSettings operatingSettings, ProjectSettingsHistory history)
    {
        AutoPrReviewSetting autoPrReview = AutoPrReviewSetting.From(history);
        bool claimGateRecorded = history.WasRecorded(change => change.ClaimGate);
        Table table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumns("k", "v");
        table.AddRow("Orchestrator model", OrchestratorModelRow(project, operatingSettings));
        table.AddRow("Skip permissions", SkipPermissionsRow(
            project, history.WasRecorded(change => change.SkipPermissions)));
        table.AddRow("Max parallel tasks", MaxParallelTasksRow(project));
        if (RetiredMaxParallelAgentsRow(project) is { } retired)
        {
            table.AddRow("Max parallel agents", retired);
        }

        table.AddRow("Priority", PriorityRow(project));

        table.AddRow("Commit style", project.CommitStyle == CommitStyle.Unknown
            ? "[dim]platform default — DaemonOptions.DefaultCommitStyle, narrative unless configured otherwise (log #26)[/]"
            : project.CommitStyle.Value.EscapeMarkup());
        table.AddRow("Re-request review", ReviewRerequestOption.Describe(
            project.ReviewRerequest,
            "after a fix follow-up pushes, closeout asks this pull request's reviewers for another pass, "
            + "capped by DaemonOptions.MaxReviewRerequestsAfterFixes (log #62)",
            "the owner preference decides (h9k owner show), else the node default "
            + "(DaemonOptions.DefaultReviewRerequest, off)"));
        table.AddRow("Verify gates", project.VerifyCommands.Count == 0
            ? $"[dim]none — add one: h9k project set {project.Name.EscapeMarkup()} --verify \"test=dotnet test\"[/]"
            : string.Join("\n", project.VerifyCommands.Select(gate =>
                $"{gate.Name.EscapeMarkup()} [dim]→[/] {gate.Command.EscapeMarkup()}")));
        table.AddRow("Jira board", project.JiraProjectKey.HasValue
            ? $"{project.JiraProjectKey.Value.EscapeMarkup()} [dim]— new cards are filed here; a reported "
              + "card key is checked against it[/]"
            : $"[dim]none bound — bind one: h9k project set {project.Name.EscapeMarkup()} --jira PROJ[/]");
        table.AddRow("Backlog policy", BacklogPolicyRow(
            project, history.WasRecorded(change => change.BacklogPolicy)));
        table.AddRow("Branch template", project.BranchNameTemplate == BranchNameTemplate.Default
            ? $"[dim]{BranchNameTemplate.Default.Value.EscapeMarkup()} — the platform default; state a "
              + $"convention: h9k project set {project.Name.EscapeMarkup()} --branch-template \"{{key}}-{{slug}}\"[/]"
            : $"{project.BranchNameTemplate.Value.EscapeMarkup()} [dim]— the name this project's task "
              + "branches are cut under[/]");
        table.AddRow("Context links", project.ContextLinks.Count == 0
            ? $"[dim]none — add one: h9k project set {project.Name.EscapeMarkup()} --link \"jira=https://…\"[/]"
            : string.Join("\n", project.ContextLinks.Select(link =>
                $"{link.Name.EscapeMarkup()} [dim]→[/] {link.Url.ToString().EscapeMarkup()}")));
        table.AddRow("Max compliance review cycles", ReviewCapRow(project, project.MaxComplianceReviewCycles, "max-compliance-review-cycles"));
        table.AddRow("Max adversarial review cycles", ReviewCapRow(project, project.MaxAdversarialReviewCycles, "max-adversarial-review-cycles"));
        table.AddRow("Max final-full-pass rounds", ReviewCapRow(project, project.MaxFinalFullPassRounds, "max-final-full-pass-rounds"));
        table.AddRow("Lifetime review-cycle budget", ReviewCapRow(project, project.LifetimeReviewCycleBudget, "lifetime-review-cycle-budget"));
        table.AddRow("Review stage composition", ReviewStageCompositionRow(project));
        table.AddRow("Auto pr-review", AutoPrReviewRow(project, autoPrReview));
        table.AddRow("Claim gate", ClaimGateRow(project, claimGateRecorded));
        table.AddRow("Close linked issue", CloseLinkedIssueRow(
            project, history.WasRecorded(change => change.CloseLinkedIssue)));
        table.AddRow("Never-close labels", project.NeverCloseLabels.Count == 0
            ? $"[dim]none — an issue's own labels never force never; add one: h9k project set "
              + $"{project.Name.EscapeMarkup()} --never-close-labels epic,prd,adr[/]"
            : string.Join(", ", project.NeverCloseLabels.Select(label => label.EscapeMarkup()))
              + " [dim]— an issue carrying any of these never closes at closeout time, regardless of "
              + "the close-linked-issue default; a task's own override still wins over the label[/]");
        table.AddRow("Writing conventions", WritingConventionsRow(
            project, history.WasRecorded(change => change.WritingConventions)));
        table.AddRow("Settings changed", project.SettingsChangedAt is { } changedAt
            ? $"[dim]{changedAt.ToLocalTime():g}[/]"
            : "[dim]never — still the registration defaults[/]");
        return table;
    }

    /// <summary>
    /// The words every origin-carrying row in this pane says about where its effective value came
    /// from (Decisions Log #161), in one place so two rows cannot describe the same fact
    /// differently: a value this project recorded is "explicit", and one nobody ever chose names
    /// the absence itself rather than only the word "default", which a reader could take for a
    /// choice of the default.
    /// </summary>
    private static string OriginNote(bool recorded) => recorded ? "explicit" : "default — nothing recorded here";

    /// <summary>
    /// The house style every composition prompt pastes verbatim, printed whole rather than
    /// summarized: it is the one setting whose exact wording is the setting, so a row that only
    /// said "customized" would tell a reader nothing they could act on. Its origin is always
    /// named, the always-printed rule auto pr-review and the claim gate already follow (Decisions
    /// Log #161) — the platform's default text and the identical text typed by hand are different
    /// facts, and only the second one survives a change to the default.
    /// </summary>
    internal static string WritingConventionsRow(ProjectDetails project, bool recorded)
    {
        string text = project.WritingConventions.Value.EscapeMarkup();
        return recorded
            ? $"{text} [dim]— explicit; restore the platform's: h9k project set "
              + $"{project.Name.EscapeMarkup()} --writing-conventions default[/]"
            : $"[dim]{text} ({OriginNote(recorded)}); state your own: h9k project set "
              + $"{project.Name.EscapeMarkup()} --writing-conventions \"<how prose has to read>\"[/]";
    }

    /// <summary>
    /// Whether dispatched agents run with <c>--dangerously-skip-permissions</c>, and whether that
    /// is this project's own recorded choice — the same always-printed origin rule auto pr-review
    /// and the claim gate follow (Decisions Log #161). "no" is both the platform's untouched
    /// default and what <c>--skip-permissions false</c> records, and the two are different facts:
    /// a project nobody has configured is one command away from running unattended, while one
    /// that chose "no" has an operator's answer standing behind every prompt a detached run
    /// cannot answer.
    /// <para>
    /// "yes" used to say nothing about origin, because it could not be anything but a choice: only
    /// <c>h9k project set</c> ever recorded it. Registration became a second, silent way to reach
    /// "yes" (Decisions Log #181) — every project <c>h9k project add</c> creates records it on
    /// <c>ProjectRegistered</c> itself, with no <c>h9k project set</c> step in between — so "yes"
    /// now names which of the two it is: <c>recorded</c> tells this row a later <c>project set</c>
    /// touched the field at all; when it has not, and the value still reads true, the only place
    /// that could have come from is registration.
    /// </para>
    /// </summary>
    internal static string SkipPermissionsRow(ProjectDetails project, bool recorded)
    {
        string name = project.Name.EscapeMarkup();
        if (project.SkipPermissions)
        {
            string origin = recorded ? "explicit" : "recorded at registration";
            return $"[yellow]yes[/] [dim]({origin}) — agents run with --dangerously-skip-permissions "
                + "(log #9, log #181)[/]";
        }

        return $"[dim]no ({OriginNote(recorded)}) — agents stop for every permission prompt, which a "
            + $"detached run cannot answer (log #9). Let them run unattended: h9k project set {name} "
            + "--skip-permissions true[/]";
    }

    /// <summary>
    /// Auto pr-review's effective value and its origin, printed always (Decisions Log #161) —
    /// the row this feature's three-day silence on both nodes was invisible behind. A project
    /// with nothing recorded reads "normal (default)", not a bare "off" that a reader could take
    /// for a choice somebody made; an explicit opt-out reads "off (explicit)" and names the
    /// command that reverses it, since off is now the setting a human has to have typed.
    /// </summary>
    internal static string AutoPrReviewRow(ProjectDetails project, AutoPrReviewSetting setting)
    {
        string name = project.Name.EscapeMarkup();
        string value = $"{setting.Speed.Value.ToLowerInvariant().EscapeMarkup()} "
            + $"[dim]({OriginNote(setting.Recorded)})[/]";
        return setting.IsOn
            ? $"{value} [dim]— a pull request GitHub assigns to this install's own login here mints, "
              + "publishes, and starts a pr-review task automatically (idea e5e98a33); a request GitHub "
              + "recorded before this project's own cutoff never starts on its own (no backfill). Turn it "
              + $"off:[/] h9k project set {name} --auto-pr-review off"
            : $"{value} [dim]— a GitHub reviewer assignment mints nothing here; every request GitHub makes "
              + "of this install's own login is still recorded and shown as a needs-you row in h9k status. "
              + $"Turn it on:[/] h9k project set {name} --auto-pr-review normal";
    }

    /// <summary>
    /// The claim gate's effective value and its origin, printed always — the same rule auto
    /// pr-review's row above follows, and for the same reason (Decisions Log #161): "off" as the
    /// platform's untouched default and "off" as a choice somebody made are different facts, and
    /// a row that renders them identically is how a setting goes unnoticed. Its default does not
    /// change here; only whether the row says which of the two it is.
    /// </summary>
    internal static string ClaimGateRow(ProjectDetails project, bool recorded)
    {
        string name = project.Name.EscapeMarkup();
        return project.ClaimGate == ClaimGate.Off
            ? $"[dim]off ({OriginNote(recorded)}) — assignment "
              + "inside Hall9k is the only claim rule; make the tracker's own assignment the go signal: "
              + $"h9k project set {name} --claim-gate tracker-assignee[/]"
            : "tracker-assignee [dim]— a task linked to a Jira card or a GitHub issue is claimed on this "
              + "install only while the tracker shows that item assigned to this install's own tracker "
              + "identity, so two teammates' installs cannot both run the same card (idea 64c75e43). "
              + "Satisfy it in one command with h9k task assign <id> --take, which takes an item nobody "
              + "holds; the gate itself is read-only, has no override flag, and a tracker that cannot be "
              + "read holds the claim[/]";
    }

    /// <summary>
    /// The model an orchestrator window for this project actually resolves to right now
    /// (<see cref="OrchestratorModel.ForProject"/>'s own chain), and which override in that chain
    /// is the one deciding it. Before this row existed, the only way to see the effective value
    /// was to cat the project's own recipes/settings.json or re-read config.json by hand
    /// (independent pre-PR review, cycle 3, conformance lens).
    /// </summary>
    internal static string OrchestratorModelRow(ProjectDetails project, OperatingSettings operatingSettings)
    {
        string resolved = OrchestratorModel.ForProject(project.OrchestratorModel, project.Model, operatingSettings);
        string origin = project.OrchestratorModel != AgentModel.Unknown
            ? "this project's own override"
            : project.Model != AgentModel.Unknown
                ? "this project's agent-dispatch model (h9k project set --model)"
                : "the node's own resolution (h9k config show)";
        return $"{resolved.EscapeMarkup()} [dim]— {origin}. Override: h9k project set "
            + $"{project.Name.EscapeMarkup()} --orchestrator-model <tier>[/]";
    }

    /// <summary>
    /// What the policy means today, said in the same words the option's own help does — a
    /// project bound to a policy nobody has looked at in months should not require re-reading
    /// h9k project set --help to remember what publishing a task will do. <c>none</c> carries its
    /// origin for the same reason auto pr-review's row does (Decisions Log #161): it is both the
    /// untouched default and the explicit "publish nothing externally", and a reader deciding
    /// whether to bind a board needs to know which of the two they are looking at.
    /// </summary>
    internal static string BacklogPolicyRow(ProjectDetails project, bool recorded)
    {
        string routing = project.BacklogRoutingGuidance.IsNotBlank()
            ? $" [dim](routing: {project.BacklogRoutingGuidance.EscapeMarkup()})[/]"
            : string.Empty;

        if (project.BacklogPolicy == BacklogPolicy.GitHubIssues)
        {
            return "github-issues [dim]— publishing authors a GitHub issue and links it, verified "
                + $"read-back included[/]{routing}";
        }

        if (project.BacklogPolicy == BacklogPolicy.Jira)
        {
            return "jira [dim]— publishing dispatches the same agent-mediated push h9k task "
                + $"push-to-jira does[/]{routing}";
        }

        return $"[dim]none ({OriginNote(recorded)}) — publishing tracks nothing externally; set one: "
            + $"h9k project set {project.Name.EscapeMarkup()} --backlog github-issues|jira[/]{routing}";
    }

    /// <summary>
    /// This project's own run ceiling (Decisions Log #140), labelled for what it counts — task
    /// runs, the node ceiling's own denomination (#111) — and stating the one thing a reader
    /// would otherwise have to work out: a run's review sessions are that run's own, so they do
    /// not count separately against this number. The pause reads as a pause, not as a zero.
    /// </summary>
    internal static string MaxParallelTasksRow(ProjectDetails project) => project.MaxParallelTasks switch
    {
        null => "[dim]not capped — this project fills whatever the node ceiling "
            + "(h9k config show) and other projects' activity leave free. Cap it: "
            + $"h9k project set {project.Name.EscapeMarkup()} --max-parallel-tasks 1[/]",
        0 => "[yellow]0 — paused[/] [dim]— its ready tasks are held even while this node sits idle, and "
            + "nothing raises the cap on its own. Runs already live finish normally. Resume it: "
            + $"h9k project set {project.Name.EscapeMarkup()} --max-parallel-tasks <n>[/]",
        int cap => $"{cap} [dim]— at most {cap} of this project's task runs are live at once; a ceiling, never a "
            + "reservation, so nothing is held free for it. A run's own review sessions do not count "
            + "separately (they are that run's, bounded by h9k config show's session cap per run)[/]",
    };

    /// <summary>
    /// Which tier this project's ready work competes in for a free dispatch slot (Decisions Log
    /// #141), stating the one thing a reader would otherwise have to work out: the default tier is
    /// not a lack of scheduling — it is the rotation, which is starvation-proof on its own — and a
    /// higher tier releases itself, unlike the cap of 0 immediately above it in this pane.
    /// </summary>
    internal static string PriorityRow(ProjectDetails project) => project.Priority switch
    {
        { } tier when tier == ProjectPriority.High =>
            "[yellow]high — focus[/] [dim]— this project wins every free slot over lower tiers while it has "
            + "ready work, and releases itself the moment its queue drains: nothing to remember, unlike a "
            + "pause. Nothing preempts — runs already live finish regardless[/]",
        { } tier when tier == ProjectPriority.Low =>
            "[yellow]low — background[/] [dim]— this project takes a free slot only when no normal- or "
            + "high-tier project has ready work, so a standing queue elsewhere can hold it indefinitely: "
            + $"h9k project set {project.Name.EscapeMarkup()} --priority normal[/]",
        { } tier when tier == ProjectPriority.Normal =>
            "[dim]normal — the default. Free slots rotate: whichever eligible project has gone longest "
            + "without a dispatch takes the next one, oldest task first within it. Focus on this project "
            + $"instead: h9k project set {project.Name.EscapeMarkup()} --priority high[/]",
        var tier =>
            $"[yellow]{tier.Value.EscapeMarkup()} — unrecognized[/] [dim]— recorded by a build that knew a "
            + "tier this one does not, so it is scheduled as normal rather than guessed at. Set one this "
            + $"build knows: h9k project set {project.Name.EscapeMarkup()} --priority normal[/]",
    };

    /// <summary>
    /// Whether true closeout closes a task's linked GitHub issue, and when (task: a task's linked
    /// GitHub issue is closed at true closeout under a configurable rule), stating the one thing a
    /// reader would otherwise have to work out: the default waits for every linked task, not just
    /// this one, and an explicit override on any single one of them decides it at the last one.
    /// <c>when-all-tasks-close</c> carries its origin rather than being labelled "the default"
    /// flatly (Decisions Log #161): <c>--close-linked-issue default</c> records exactly that value
    /// as a choice, and a row that calls a typed answer the default is guessing at provenance
    /// (AGENTS.md). The other rules can only have been recorded, so they say nothing about origin.
    /// </summary>
    internal static string CloseLinkedIssueRow(ProjectDetails project, bool recorded) => project.CloseLinkedIssue switch
    {
        { } rule when rule == CloseLinkedIssueRule.OnCloseout =>
            "on-closeout [dim]— every task's linked GitHub issue closes in the same step as its own "
            + "merge note, unconditionally. A never-close label or a task's own --close-linked-issue "
            + "override still applies[/]",
        { } rule when rule == CloseLinkedIssueRule.Never =>
            "[dim]never — the merge note is posted, and the issue is never closed here (the right "
            + "choice for an epic, a PRD, or an ADR). Close automatically on merge: h9k project set "
            + $"{project.Name.EscapeMarkup()} --close-linked-issue on-closeout[/]",
        { } rule when rule == CloseLinkedIssueRule.WhenAllTasksClose =>
            $"[dim]when-all-tasks-close ({OriginNote(recorded)}). The merge note is posted every time; the issue "
            + "closes only once every task linked to it has itself reached true closeout or been "
            + "abandoned, decided fresh at the last one across every linked task's own recorded rule[/]",
        var rule =>
            $"[yellow]{rule.Value.EscapeMarkup()} — unrecognized[/] [dim]— recorded by a build that knew a "
            + "rule this one does not, so the issue is left open rather than guessed at. Set one this "
            + $"build knows: h9k project set {project.Name.EscapeMarkup()} --close-linked-issue default[/]",
    };

    /// <summary>
    /// The retired session-denominated ceiling, shown only where there is a retirement to name:
    /// a project that recorded a value under the old <c>--max-parallel</c> and has not set the
    /// runs-denominated cap since. The old value is not carried over — nothing ever enforced it —
    /// so this row is the migration, said where the operator who set it will read it.
    /// </summary>
    internal static string? RetiredMaxParallelAgentsRow(ProjectDetails project) =>
        project.MaxParallelTasks is null
        && project.MaxParallelAgents != ProjectAggregate.LegacyMaxParallelAgentsDefault
            ? $"[yellow]{project.MaxParallelAgents} — retired[/] [dim]— recorded in agent sessions by the old "
                + "--max-parallel, which nothing ever enforced. It is retired rather than converted, so this "
                + "project is uncapped until you set the runs-denominated ceiling: "
                + $"h9k project set {project.Name.EscapeMarkup()} --max-parallel-tasks <n>[/]"
            : null;

    /// <summary>
    /// One of the four review-cycle caps (task: the review cycle caps become settable at three
    /// levels): this project's own override when set, else the resolution chain underneath it —
    /// this project outranks the node, and a task override outranks this project in turn.
    /// </summary>
    private static string ReviewCapRow(ProjectDetails project, int? projectOverride, string optionName) => projectOverride is { } value
        ? value.ToString()
        : $"[dim]not set — the node decides (h9k config show), unless a task overrides it. Set one: "
          + $"h9k project set {project.Name.EscapeMarkup()} --{optionName} <N>[/]";

    /// <summary>
    /// This project's own review stage composition override when set, else the resolution chain
    /// underneath it (task: the review pipeline's stage composition becomes configuration
    /// recorded per run) — this project outranks the node, and a task override outranks this
    /// project in turn. The <c>ReviewCapRow</c> shape, applied to the closed-set string instead
    /// of an int.
    /// </summary>
    private static string ReviewStageCompositionRow(ProjectDetails project) => project.ReviewStageComposition is { } value
        ? value.Value.EscapeMarkup()
        : $"[dim]not set — the node decides (h9k config show), unless a task overrides it. Set one: "
          + $"h9k project set {project.Name.EscapeMarkup()} --review-stage-composition <COMPOSITION>[/]";

    private static void WriteTasks(ProjectDetails project, IReadOnlyList<TaskStatusRow> rows)
    {
        string name = project.Name.EscapeMarkup();
        if (rows.Count == 0)
        {
            AnsiConsole.MarkupLine(
                $"\n[bold]Tasks[/] [dim]none yet. Queue one:[/] h9k task add --project {name} "
                + "--objective \"…\" --criteria \"…\"");
            return;
        }

        AnsiConsole.MarkupLine($"\n[bold]Tasks[/] {TaskRollup.From(rows).Summary()}");

        // Newest first: h9k project show is an orientation pane, and the freshest tasks
        // are what the person asking "what is this project up to" wants first.
        List<TaskStatusRow> newest = [.. rows.OrderByDescending(row => row.AddedAt).Take(RecentTasks)];

        AnsiConsole.Write(TaskTable(newest, AnsiConsole.Profile.Width, DateTimeOffset.UtcNow));

        int held = rows.Count - newest.Count;
        AnsiConsole.MarkupLine(held > 0
            ? $"[dim]Showing the {newest.Count} newest of {rows.Count}; {held} held back — all of them:[/] "
              + $"h9k task list --project {name} --all --include-archived"
            : $"[dim]All {rows.Count} of this project's tasks. Filter them with:[/] "
              + $"h9k task list --project {name} --state <state>");
    }

    /// <summary>
    /// The pane's task list: the same columns h9k task list shows for one project, with the
    /// objective truncated to exactly the width the fixed columns leave it so a row never wraps,
    /// and each row's summary line underneath it. Built apart from the query so the layout can be
    /// rendered and measured.
    /// </summary>
    internal static IRenderable TaskTable(IReadOnlyList<TaskStatusRow> rows, int consoleWidth, DateTimeOffset now)
    {
        string[] ages = [.. rows.Select(row => $"[dim]{row.AgeMarkup(now)}[/]")];
        return TaskRowLayout.Render(
            rows,
            [
                new TaskColumn("Id", [.. rows.Select(row => row.IdMarkup)]),
                new TaskColumn("Status", [.. rows.Select(row => row.StateMarkup)]),
                new TaskColumn("Type", [.. rows.Select(row => row.TypeMarkup)]),
            ],
            [
                new TaskColumn("Attention", [.. rows.Select(row => row.AttentionMarkup)]),
                new TaskColumn("Added", ages),
                new TaskColumn("PR", [.. rows.Select(row => row.PullRequestMarkup)]),
            ],
            [.. rows.Select(row => row.SummaryMarkup)],
            consoleWidth,
            headers: true);
    }
}

using System.Buffers.Binary;
using System.ComponentModel;
using Hall9k.Cli.DaemonControl;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Plan;
using Hall9k.Domain.Features.Plan.Events;
using Hall9k.Domain.Features.Plan.Projections;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Npgsql;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Start a plan: hand one or more drafts to the daemon on this node to carry through refinement and challenge to
/// Published, parking for a person when it cannot finish, or (with <c>--idea</c>) an idea to discover, which parks at a gate where
/// a person pursues it or stops. It records the plan on a stream of its own, rings the doorbell, and
/// returns without waiting; nothing here runs a session.
/// <para>
/// Every refusal is given here, before any session starts (<see cref="PlanStartRules"/>): who may publish every draft, a draft
/// that is not a plain Draft or is in a live plan or has a challenge or refinement outstanding, drafts from different
/// projects, a project that is archived or has no checkout or does not skip permissions, a context still carrying a
/// stranger's quoted text, and a missing tracker attestation.
/// </para>
/// </summary>
public sealed class PlanStartCommand : Hall9kAsyncCommand<PlanStartCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[DRAFTS]")]
        [Description(
            "The drafts to carry, each a task id (full, or an unambiguous fragment). All must be Drafts of one project, and you must "
            + "be allowed to publish every one of them. Name none with --idea")]
        public string[] Drafts { get; init; } = [];

        [CommandOption("--idea <IDEA>")]
        [Description(
            "Start from an idea instead of from drafts: the daemon runs one headless discovery session on it, whose report lands in "
            + "the idea's workspace, and the plan parks at a gate (h9k status lists it under Needs you). There you answer with "
            + "h9k plan pursue, which cuts the report's proposed tasks as drafts the plan carries to Published, or h9k plan stop, "
            + "a normal ending that leaves the idea Captured. Only the idea's assignee, or its creator when nobody holds it, may "
            + "start one, with no override; the idea must be Captured, in a project this node holds, with a workspace directory "
            + "on this node, and carry no tracker link. Capturing an idea never starts a plan, because a plan spends tokens")]
        public string? Idea { get; init; }

        [CommandOption("--at <PHASE>")]
        [Description(
            "Where the drafts start: refinement (the default) or challenge. At challenge the first thing that happens is the "
            + "independent challenge; it is refused for a draft with no acceptance criteria. A plan from an idea starts at discovery "
            + "(--idea), not here")]
        public string? At { get; init; }

        [CommandOption("--no-existing-item")]
        [Description(
            "The dedup attestation h9k task publish takes, given once for the plan: you searched the project's tracker and found "
            + "no open item covering these drafts. Required on a project whose backlog policy is jira, github-issues, or linear "
            + "unless every draft is already linked or --untracked is given. Recorded on the plan with who gave it, and what the "
            + "plan's publish passes on")]
        public bool NoExistingItem { get; init; }

        [CommandOption("--untracked")]
        [Description(
            "The sibling attestation: deliberately skip external tracking for these drafts. Means what it means on h9k task "
            + "publish, so it is refused alongside --no-existing-item and on a project whose backlog policy tracks nothing")]
        public bool Untracked { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        // Present, blank or not: an empty --idea next to draft ids must meet the same refusal as a named one, never fall through
        // to starting a plan from the drafts as though the option had not been given.
        if (settings.Idea is not null)
        {
            return await StartFromIdeaAsync(settings, cancellationToken);
        }

        if (settings.Drafts.Length == 0)
        {
            throw new DomainValidationException(
                "A plan needs at least one draft, or an idea to discover: h9k plan start <draft> [<draft>...], or "
                + "h9k plan start --idea <idea>. Draft ids come from h9k task list --state draft.");
        }

        PlanPhase start = settings.At is null
            ? PlanPhase.Refinement
            : PlanPhase.ParseStart(settings.At)
                ?? throw new DomainValidationException(
                    $"'{settings.At}' is not a phase a plan starts at: pass refinement (the default) or challenge. "
                    + "A plan from an idea starts at discovery with --idea.");

        using DocumentStore store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        List<PlanStartDraft> drafts = [];
        foreach (string named in settings.Drafts)
        {
            Guid taskId = await TaskIdResolver.ResolveAsync(session, named, cancellationToken);
            TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
                ?? throw new DomainNotFoundException($"No task {taskId}.");
            TaskDetails details = await session.LoadAsync<TaskDetails>(taskId, cancellationToken)
                ?? throw new DomainNotFoundException($"No task {taskId}.");

            // Whoever may publish a draft may put it in a plan, because the plan publishes it in their name.
            await TaskOwnerGuard.AssertMayActAsync(session, task, context, cancellationToken);
            drafts.Add(new PlanStartDraft(task, details));
        }

        ProjectDetails project = await session.LoadAsync<ProjectDetails>(drafts[0].Task.ProjectId, cancellationToken)
            ?? throw new DomainNotFoundException($"Draft {TaskListCommand.ShortId(drafts[0].Task.Id)} names a project that is not registered.");
        string checkout = ProjectCheckout.ForReading(project);
        string? missingCheckout = !Directory.Exists(checkout) || ProjectCheckout.IsBare(checkout)
            ? checkout
            : null;

        // The rule that a draft sits in at most one live plan is a check followed by an act, so it holds only if two starts
        // for one project cannot overlap: the lock is taken before the live plans are read and held until the plan is saved.
        await RequireSoleStarterAsync(session, project.Id, cancellationToken);
        IReadOnlyList<PlanDetails> livePlans = [.. (await session.Query<PlanDetails>().ToListAsync(cancellationToken)).Where(plan => plan.IsLive)];

        PlanStarted decided = PlanStartRules.Decide(
            drafts, project, livePlans, start, settings.NoExistingItem, settings.Untracked, context.OwnerId, context.NodeId,
            DateTimeOffset.UtcNow, ForeignSessionCeiling.Default, missingCheckout);
        PlanStarted started = decided with { Id = DomainId.New() };
        session.Events.StartStream<PlanAggregate>(started.Id, started);
        await session.SaveChangesAsync(cancellationToken);
        await Doorbell.RingAsync($"plan-started:{started.Id}", cancellationToken);

        string shortId = TaskListCommand.ShortId(started.Id);
        string noun = drafts.Count == 1
            ? "draft"
            : "drafts";
        AnsiConsole.MarkupLine(
            $"[blue]Plan {shortId} started[/] for {drafts.Count} {noun} of project {project.Name.EscapeMarkup()}, at {start.Word}");
        foreach (PlanStartDraft draft in drafts)
        {
            AnsiConsole.MarkupLine($"  {TaskListCommand.ShortId(draft.Task.Id)}  {ExternalText.OneLineMarkup(draft.Task.Objective)}");
        }

        AnsiConsole.MarkupLine(
            "[dim]  This returns without waiting. The daemon on this node refines each draft, has it challenged, applies what the "
            + "challenge accepts, and publishes the drafts when every one is ready, with no run, lease, branch, or worktree. It parks "
            + "for you, and the project's orchestrator feed says so, when it cannot finish. Publishing assigns and queues nothing. "
            + "Nothing leaves this node. Read where it stands with:[/] "
            + $"h9k plan show {shortId}");
        if (DaemonProcess.Probe() is null)
        {
            AnsiConsole.MarkupLine(
                "[yellow]  The daemon is not running.[/] [dim]A plan started while it is stopped waits for it: h9k daemon start[/]");
        }

        return ExitCodes.Ok;
    }

    /// <summary>
    /// <c>h9k plan start --idea</c>: records a plan in discovery with no drafts and returns without waiting. The daemon on this
    /// node dispatches the one discovery session. Naming drafts, a start phase, or an attestation alongside is refused: the
    /// drafts do not exist until a person pursues the report, and the attestations are given there.
    /// </summary>
    private static async Task<int> StartFromIdeaAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Drafts.Length > 0 || settings.At is not null || settings.NoExistingItem || settings.Untracked)
        {
            string offered = settings.Drafts.Length > 0
                ? "draft ids"
                : settings.At is not null
                    ? "--at"
                    : settings.NoExistingItem
                        ? "--no-existing-item"
                        : "--untracked";
            throw new DomainValidationException(
                $"--idea starts a plan in discovery, before any draft exists, so it takes {offered} nowhere: the drafts are cut when you "
                + "pursue the report at the gate, and the tracker attestations (--no-existing-item, --untracked) are given to "
                + "h9k plan pursue then. Start from an idea with h9k plan start --idea <idea>, or from drafts with "
                + "h9k plan start <draft> [<draft>...].");
        }

        using DocumentStore store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        Guid ideaId = await IdeaIdResolver.ResolveAsync(session, settings.Idea ?? string.Empty, cancellationToken);
        IdeaAggregate idea = await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No idea {ideaId}.");
        bool mayAct = await IdeaOwnerGuard.MayActAsync(session, idea, context, new GitLedgerChainReader(), cancellationToken);
        ProjectDetails? project = idea.ProjectId is { } projectId
            ? await session.LoadAsync<ProjectDetails>(projectId, cancellationToken)
            : null;
        string? missingCheckout = null;
        if (project is not null)
        {
            string checkout = ProjectCheckout.ForReading(project);
            missingCheckout = !Directory.Exists(checkout) || ProjectCheckout.IsBare(checkout)
                ? checkout
                : null;
        }

        // The same lock a start from drafts takes, keyed on the project: a check followed by an act is only one idea in one live
        // plan if two starts cannot overlap.
        await RequireSoleStarterAsync(session, project?.Id ?? idea.ProjectId ?? ideaId, cancellationToken);
        IReadOnlyList<PlanDetails> livePlans = [.. (await session.Query<PlanDetails>().ToListAsync(cancellationToken)).Where(plan => plan.IsLive)];

        PlanStarted decided = PlanStartRules.DecideFromIdea(
            idea, mayAct, project, livePlans, DiscoveryWorkspace.Exists(idea), missingCheckout, context.OwnerId, DateTimeOffset.UtcNow);
        PlanStarted started = decided with { Id = DomainId.New() };
        session.Events.StartStream<PlanAggregate>(started.Id, started);
        await session.SaveChangesAsync(cancellationToken);
        await Doorbell.RingAsync($"plan-started:{started.Id}", cancellationToken);

        string shortId = TaskListCommand.ShortId(started.Id);
        AnsiConsole.MarkupLine(
            $"[blue]Plan {shortId} started[/] on idea {TaskListCommand.ShortId(idea.Id)} of project {(project?.Name ?? string.Empty).EscapeMarkup()}, in discovery");
        AnsiConsole.MarkupLine(
            "[dim]  This returns without waiting. The daemon on this node runs one headless discovery session on the idea and copies "
            + $"its report to {DiscoveryWorkspace.ReportFileName(started.Id)} in the idea's workspace, then parks at a gate for you "
            + "(h9k status lists it under Needs you): h9k plan pursue cuts the report's proposed tasks as drafts the plan carries to "
            + "Published, and h9k plan stop ends it, which is a normal ending. It cuts no draft and decides nothing about the idea. "
            + "No run, lease, branch, or worktree is created, and nothing leaves this node. Read where it stands with:[/] "
            + $"h9k plan show {shortId}");
        if (DaemonProcess.Probe() is null)
        {
            AnsiConsole.MarkupLine(
                "[yellow]  The daemon is not running.[/] [dim]A plan started while it is stopped waits for it: h9k daemon start[/]");
        }

        return ExitCodes.Ok;
    }

    /// <summary>
    /// The advisory lock's first key: a constant standing for "starting a plan", so it cannot collide with an unrelated lock
    /// taken against the same store (<c>h9k decide import</c> takes its own under a different one).
    /// </summary>
    private const int StartLockClass = 0x0A1A4057;

    /// <summary>
    /// Takes Postgres' own advisory lock for one project, scoped to the session's transaction, so two <c>h9k plan start</c>
    /// commands cannot both pass the one-live-plan-per-draft rule against the same live plans. It refuses rather than
    /// waiting: the start in flight is about to record its plan, and the honest answer is to run this again once it has.
    /// </summary>
    private static async Task RequireSoleStarterAsync(IDocumentSession session, Guid projectId, CancellationToken cancellationToken)
    {
        await session.BeginTransactionAsync(cancellationToken);
        NpgsqlConnection connection = session.Connection
            ?? throw new InvalidOperationException(
                "The plan start's own session has no open connection after beginning its transaction, so the lock that keeps "
                + "two starts from overlapping cannot be taken. Nothing was recorded.");

        await using NpgsqlCommand command = new("select pg_try_advisory_xact_lock(@class, @scope)", connection);
        command.Parameters.AddWithValue("class", StartLockClass);
        command.Parameters.AddWithValue("scope", ProjectLockKey(projectId));
        if (await command.ExecuteScalarAsync(cancellationToken) is true)
        {
            return;
        }

        throw new DomainConflictException(
            "Another h9k plan start for this project is in flight right now, so this one recorded nothing. A draft sits in at "
            + "most one live plan, and two starts at once could each put the same draft in one. Run it again in a moment; "
            + "h9k plan list says whether the other start recorded its plan.");
    }

    /// <summary>The project's id folded into the <c>int4</c> Postgres takes, explicitly rather than by hash code, so every process derives the same number.</summary>
    private static int ProjectLockKey(Guid projectId)
    {
        Span<byte> bytes = stackalloc byte[16];
        projectId.TryWriteBytes(bytes);

        int key = 0;
        for (int index = 0; index < bytes.Length; index += sizeof(int))
        {
            key ^= BinaryPrimitives.ReadInt32LittleEndian(bytes[index..]);
        }

        return key;
    }
}

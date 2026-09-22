using System.Buffers.Binary;
using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Decision;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Npgsql;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// What one <see cref="DecisionImportCommand"/> run did: what it recorded, how much of that this
/// same change retired in the same act, what an earlier run had already recorded, and where it put
/// all of it.
/// </summary>
internal sealed record LegacyImportOutcome(int Recorded, int Retired, int AlreadyImported, ResolvedKnowledgeScope Scope);

/// <summary>
/// The one-time import of the markdown rulebooks into the decision store (idea d805fd8b, piece
/// 3): every entry PLAN.md's §16 v0 Decisions Log carried, and every standing rule AGENTS.md's Git
/// rules and Working agreements sections carried, recorded as a <see cref="DecisionRecorded"/>
/// keeping the citation it already had.
/// <para>
/// It is a command rather than a migration the daemon runs at startup because it has to be
/// deliberate and it has to be attributable: it appends the better part of three hundred events
/// under one owner's name, and which node runs it decides which node's ids the whole fleet ends up
/// citing. Run it once, from the shell, on a node whose replication is live.
/// </para>
/// <para>
/// Running it again is harmless, which is the point of the legacy id: the later run reads the
/// citations already recorded in this scope and records only what is missing. A first run
/// interrupted half way through is finished by a second. Two runs at the SAME time are a
/// different thing and are refused rather than merged — see
/// <see cref="RequireSoleImporterAsync"/>.
/// </para>
/// </summary>
public sealed class DecisionImportCommand : Hall9kAsyncCommand<DecisionImportCommand.Settings>
{
    /// <summary>
    /// The advisory lock's first key (see <see cref="RequireSoleImporterAsync"/>): a constant
    /// standing for "the one-time legacy import", so this lock cannot collide with an unrelated
    /// one taken elsewhere against the same store. The digits are idea d805fd8b's own, which is
    /// the only thing that made one number more memorable than another.
    /// </summary>
    private const int ImportLockClass = 0x0D805F8B;

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--project <PROJECT>")]
        [Description(
            "The project whose rulebook this is: its name, an unambiguous fragment of it, or its id. "
            + "Defaults to the project of the run you are importing from, then to the sole registered "
            + "project. Every imported decision is scoped to it and travels to every member of it")]
        public string? Project { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        LegacyImportOutcome outcome = await RunAsync(
            session, settings, context.OwnerId, context.NodeId, DateTimeOffset.UtcNow, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        Print(outcome);
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Everything but the store round trip and the printing, so the whole import is testable
    /// against a real document session, the same seam <see cref="DecideCommand.RunAsync"/> uses.
    /// Appends and returns; the caller saves.
    /// </summary>
    internal static async Task<LegacyImportOutcome> RunAsync(
        IDocumentSession session,
        Settings settings,
        Guid ownerId,
        Guid nodeId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        NodeAggregate node = await session.Events.AggregateStreamAsync<NodeAggregate>(nodeId, token: cancellationToken)
            ?? throw new DomainNotFoundException(
                $"Node {DomainId.Short(nodeId)} has no stream, so whether replication is switched on here "
                + "cannot be read. Run h9k doctor before importing anything.");

        LegacyKnowledgeImportDecider.RequireReplicationSwitchedOn(node.ReplicationSwitchOnSequence);

        ObservedRecordingContext observed = await RecordingProvenanceReader.ObserveAsync(
            session, taskReference: null, ownerId, cancellationToken);
        ResolvedKnowledgeScope scope = await KnowledgeScopeResolver.ResolveAsync(
            session, settings.Project, ownerScoped: false, observed.ProjectId, ownerId, "decision", cancellationToken);

        await RequireSoleImporterAsync(session, scope, cancellationToken);

        List<LegacyDecisionEntry> entries =
        [
            .. LegacyDecisionsLogParser.Parse(LegacyKnowledgeSource.DecisionsLog()),
            .. LegacyStandingRulesParser.Parse(LegacyKnowledgeSource.StandingRules()),
        ];

        // Every legacy id already recorded against this scope, whatever its status: a decision
        // somebody has since superseded was still imported, and importing it again under a fresh
        // id would resurrect a rule that stopped binding. Read inside the transaction the lock
        // above opened, so what this sees is what the save below writes against.
        IReadOnlyList<DecisionDetails> existing = await session.Query<DecisionDetails>()
            .Where(decision => decision.ScopeId == scope.ScopeId && decision.LegacyId != null)
            .ToListAsync(cancellationToken);

        LegacyImportPlan plan = LegacyKnowledgeImportDecider.Plan(
            entries, [.. existing.Select(decision => decision.LegacyId!)],
            scope.Scope, scope.ScopeId, observed.Provenance, now);

        // A retirement belongs on its own decision's stream, appended behind the recording it ends,
        // so the two land in one transaction and no reader ever sees the retired rule binding.
        Dictionary<Guid, DecisionSuperseded> retirements = plan.Retirements.ToDictionary(retired => retired.Id);
        foreach (DecisionRecorded recorded in plan.ToRecord)
        {
            if (retirements.TryGetValue(recorded.Id, out DecisionSuperseded? retired))
            {
                session.Events.StartStream<DecisionAggregate>(recorded.Id, recorded, retired);
                continue;
            }

            session.Events.StartStream<DecisionAggregate>(recorded.Id, recorded);
        }

        return new LegacyImportOutcome(plan.ToRecord.Count, plan.Retirements.Count, plan.AlreadyImported, scope);
    }

    /// <summary>
    /// Makes this call the only import in flight against this scope, and refuses if it is not.
    /// <para>
    /// Idempotency through the legacy id is a check followed by an act, and on its own it only
    /// holds for runs that do not overlap: two shells importing at once both read an empty set of
    /// citations, both plan the whole rulebook under their own fresh ids, and both commit, leaving
    /// two copies of every rule that then replicate to every member — and since nothing in this
    /// store is ever deleted, the only remedy is two hundred and eighty hand supersessions
    /// (independent pre-PR review, cycle 1, adversarial lens). So the read and the write are put
    /// inside one transaction and the transaction takes Postgres' own advisory lock first. The
    /// lock is held by the database rather than by this process, which is what makes it hold
    /// across two shells, and it is scoped to the transaction, so it is released by the commit or
    /// by the rollback and never by a process remembering to.
    /// </para>
    /// <para>
    /// It refuses rather than waiting. A blocking lock would make the second shell hang for as
    /// long as the first one takes, with nothing on screen saying why, and the honest answer is
    /// one line of advice: the run in flight is doing this run's work, so let it finish and look
    /// at what it recorded. Keyed by scope, because two projects' rulebooks are separate acts with
    /// no reason to queue behind each other.
    /// </para>
    /// </summary>
    private static async Task RequireSoleImporterAsync(
        IDocumentSession session, ResolvedKnowledgeScope scope, CancellationToken cancellationToken)
    {
        await session.BeginTransactionAsync(cancellationToken);
        NpgsqlConnection connection = session.Connection
            ?? throw new InvalidOperationException(
                "The import's own session has no open connection after beginning its transaction, so "
                + "the advisory lock that makes it the only importer cannot be taken. Nothing was "
                + "recorded.");

        // NpgsqlCommand.Transaction is documented as ignored — every command on a connection runs
        // inside whatever transaction that connection has open — so this lock is genuinely
        // Marten's transaction's lock rather than a separate one released the moment it is taken.
        await using NpgsqlCommand command = new("select pg_try_advisory_xact_lock(@class, @scope)", connection);
        command.Parameters.AddWithValue("class", ImportLockClass);
        command.Parameters.AddWithValue("scope", ScopeLockKey(scope.ScopeId));
        if (await command.ExecuteScalarAsync(cancellationToken) is true)
        {
            return;
        }

        throw new DomainConflictException(
            $"Another h9k decide import is in flight against {scope.Label} right now, so this one "
            + "recorded nothing. Two at once would each record the whole rulebook under their own ids "
            + "and leave two copies of every rule, which nothing in this store can delete. Let that run "
            + "finish and read what it recorded (h9k decide list); run this again if it was interrupted, "
            + "and it will record only what that run missed.");
    }

    /// <summary>
    /// The advisory lock's second key: one scope's id folded into the <c>int4</c> Postgres takes.
    /// Folded explicitly rather than through <see cref="object.GetHashCode"/>, because the two
    /// processes this lock exists to separate have to derive the same number from the same id, and
    /// a hash contract that is stable today is not a promise. A fold is lossy and two scopes could
    /// in principle land on one key, which costs a refusal that reads a little wrong and never
    /// costs correctness.
    /// </summary>
    private static int ScopeLockKey(Guid scopeId)
    {
        Span<byte> bytes = stackalloc byte[16];
        scopeId.TryWriteBytes(bytes);

        int key = 0;
        for (int index = 0; index < bytes.Length; index += sizeof(int))
        {
            key ^= BinaryPrimitives.ReadInt32LittleEndian(bytes[index..]);
        }

        return key;
    }

    private static void Print(LegacyImportOutcome outcome)
    {
        if (outcome.Recorded == 0)
        {
            AnsiConsole.MarkupLine(
                $"[blue]Nothing left to import[/] [dim]({outcome.AlreadyImported} entries are already recorded "
                + $"against {outcome.Scope.Label.EscapeMarkup()})[/]");
            return;
        }

        AnsiConsole.MarkupLine(
            $"[blue]Imported {outcome.Recorded} decisions[/] [dim]into {outcome.Scope.Label.EscapeMarkup()}[/]");
        if (outcome.AlreadyImported > 0)
        {
            AnsiConsole.MarkupLine(
                $"[dim]  already recorded by an earlier run, left alone:[/] {outcome.AlreadyImported}");
        }

        if (outcome.Retired > 0)
        {
            AnsiConsole.MarkupLine(
                $"[dim]  recorded and superseded in the same act, because this change is what retired "
                + $"them:[/] {outcome.Retired} [dim](named at the foot of decisions.md, in full with "
                + "h9k decide show \"<citation>\")[/]");
        }

        AnsiConsole.MarkupLine(
            "[dim]  each one keeps the citation it had, so[/] h9k decide show \"Decisions Log #62\" "
            + "[dim]resolves, and so does searching decisions.md for that text[/]");
        AnsiConsole.MarkupLine(
            "[dim]Read them back:[/] h9k decide list [dim]· the rendered file:[/] decisions.md "
            + "[dim]in this project's home[/]");
    }
}

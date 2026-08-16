# Hall9k v0 — Domain Model Draft (Task 2 output)

Drafted 2026-08-16. House style: static decider pattern, sealed-record events one per file,
vertical slices, inline `SingleStreamProjection` only, UUIDNext v7 IDs, Marten 8.x +
WolverineFx 5.x, value objects over primitives and enums (§8).

Folder layout mirrors NTS: `Hall9k.Domain/Features/{Feature}/` with the aggregate at the slice
root. **Subfolders (`Commands/`, `Events/`, `Handlers/`, `Queries/`, `Projections/`, `Documents/`)
are used only where a slice is big enough to want them** — Task, Run, Project. Tiny slices
(Owner, Node, Connection) stay flat: aggregate, event(s), and projection as sibling files, no
subfolders until growth demands them. (Subfolders are interior organization of a slice, not part
of the vertical-slice pattern itself.)

---

## 1. Stream map

| Stream (aggregate) | Lifespan | Owns |
|---|---|---|
| **Task** | days–weeks | the work's story: readiness contract, claims/leases, conversation, terminal outcome |
| **Run** | minutes–hours | one dispatch attempt: process, session, verification, PR, tokens |
| **Owner** | permanent | the human (§6.2 accountability root) |
| **Node** | permanent | one machine's identity |
| **Project** | permanent | repo binding + verify commands + agent policy |
| **Connection** | permanent | provider credential indirection (§10) |

Task ↔ Run linkage: `RunDispatched` carries `TaskId`; the Task stream never records run
internals. Reads join the two projections by `TaskId` (see §5 — no multi-stream projection,
per house style).

## 2. Task slice

### State split (proposal)

The §3.3 lifecycle mixes two concerns. Here they separate cleanly:

- **Task state** (work lifecycle): `Queued → Claimed → NeedsHuman ⇄ Claimed → Done | Failed | Abandoned`
  (+ `NeedsRefinement` reserved, not built in v0)
- **Run state** (execution lifecycle): `Dispatched → Running → Verifying → AwaitingReview → Completed | Failed | Killed | Superseded`

`h9k status` composes the display state: a claimed task shows its current run's state.

### Events (`Features/Task/Events/`, one record per file)

```csharp
public sealed record TaskAdded(
    Guid Id,
    Guid ProjectId,
    string Objective,
    IReadOnlyList<string> AcceptanceCriteria,
    TaskType Type,
    string? AgentContext,
    TaskConstraints? Constraints,        // null = no budget, nothing auto-killed (log #11)
    ExternalReference? ExternalReference, // set when adopted via --from-issue (resolves open decision #9 for v0)
    DateTimeOffset AddedAt,
    Guid AddedByOwnerId);

public sealed record TaskClaimed(
    Guid Id,
    Guid NodeId,
    Guid OwnerId,
    int LeaseGeneration,                 // fencing token (log #7)
    Guid RunId,                          // minted by the daemon BEFORE claiming; RunDispatched uses the same id.
                                         // Gives the Task stream its run linkage with no extra events, and makes
                                         // worktree naming (wt-<task>-<run>) deterministic pre-spawn.
    DateTimeOffset ClaimedAt);

public sealed record TaskRequeued(
    Guid Id,
    RequeueReason Reason,                // LeaseExpired | RunFailedRetryable | HumanRequested
    DateTimeOffset RequeuedAt);

public sealed record QuestionAsked(
    Guid Id,
    Guid QuestionId,
    Guid RunId,
    string Question,
    DateTimeOffset AskedAt);

public sealed record AnswerProvided(
    Guid Id,
    Guid QuestionId,
    string Answer,
    DateTimeOffset AnsweredAt,
    Guid AnsweredByOwnerId);

public sealed record TaskCompleted(Guid Id, Guid RunId, string? PullRequestUrl, DateTimeOffset CompletedAt);
public sealed record TaskFailed(Guid Id, Guid RunId, string Reason, DateTimeOffset FailedAt);
public sealed record TaskAbandoned(Guid Id, string? Reason, DateTimeOffset AbandonedAt, Guid AbandonedByOwnerId);

// Reserved, not built in v0:
// public sealed record TaskSentToRefinement(...);
```

### Aggregate (`Features/Task/TaskAggregate.cs`)

```csharp
public sealed class TaskAggregate
{
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Objective { get; private set; } = string.Empty;
    public TaskType Type { get; private set; }
    public TaskState State { get; private set; }
    public TaskConstraints? Constraints { get; private set; }
    public ExternalReference? ExternalReference { get; private set; }
    public int LeaseGeneration { get; private set; }
    public Guid? ClaimedByNodeId { get; private set; }
    public Guid? CurrentRunId { get; private set; }      // from the latest TaskClaimed
    public Guid? PendingQuestionId { get; private set; }

    private readonly List<string> _acceptanceCriteria = [];
    public IReadOnlyList<string> AcceptanceCriteria => _acceptanceCriteria;

    private readonly List<Guid> _runIds = [];            // accumulated from claims; run DETAILS stay a
    public IReadOnlyList<Guid> RunIds => _runIds;        // read-side query (RunListItem by TaskId)

    public void Apply(TaskAdded @event) { /* Id, ProjectId, contract fields; State = Queued */ }
    public void Apply(TaskClaimed @event) { /* LeaseGeneration = @event.LeaseGeneration; ClaimedByNodeId; State = Claimed */ }
    public void Apply(TaskRequeued @event) { /* ClaimedByNodeId = null; State = Queued */ }
    public void Apply(QuestionAsked @event) { /* PendingQuestionId; State = NeedsHuman */ }
    public void Apply(AnswerProvided @event) { /* PendingQuestionId = null; State = Claimed */ }
    public void Apply(TaskCompleted @event) { /* State = Done */ }
    public void Apply(TaskFailed @event) { /* State = Failed */ }
    public void Apply(TaskAbandoned @event) { /* State = Abandoned */ }
}
```

### Claim atomicity

The daemon claims by appending `TaskClaimed` with Marten's **optimistic concurrency on the
stream** (`AppendOptimistic` / expected-version). Two claimants racing → one succeeds, the
other gets a concurrency exception and moves on. No advisory locks, no claim table; the
stream version *is* the lock. Multi-daemon-safe from day one (§6.2).

### Value objects (slice root — see §8 for the type discipline these follow)

```csharp
public sealed record TaskConstraints(int? MaxTurns, long? MaxTokens, TimeSpan? MaxWallClock);

// Composite VO with parsing behavior: canonical form "github:owner/repo#42"
public sealed record ExternalReference(WorkItemProvider Provider, string Reference)
{
    public override string ToString() => $"{Provider}:{Reference}";
    public static ExternalReference Parse(string value) { /* split on first ':' */ }
}

// Closed-vocabulary VOs (house anatomy per §8; static instances shown, plumbing elided):
public sealed record WorkItemProvider   // GitHub, Jira, Unknown
public sealed record TaskType           // Feature, Bugfix, Refactor, Chore, Research, Unknown
public sealed record TaskState          // Queued, Claimed, NeedsHuman, Done, Failed, Abandoned, Unknown
                                        //   (+ NeedsRefinement reserved, not built in v0)
public sealed record RequeueReason      // LeaseExpired, RunFailedRetryable, HumanRequested, Unknown
```

## 3. Run slice

### Events (`Features/Run/Events/`)

```csharp
public sealed record RunDispatched(
    Guid Id,
    Guid TaskId,
    Guid NodeId,
    Guid OwnerId,                        // owner of the node AT dispatch time — frozen here so the §6.2
                                         // accountability chain survives any future node ownership change
    int LeaseGeneration,                 // stamped at birth; stale generation ⇒ output discarded (log #7)
    Guid SessionId,                      // daemon-minted, passed via --session-id (log #5)
    string WorktreePath,
    string Branch,
    ExecutorMode ExecutorMode,           // Subscription | ApiKey (log #1)
    DateTimeOffset DispatchedAt);

public sealed record RunProcessStarted(
    Guid Id,
    int ProcessId,
    DateTimeOffset ProcessStartedAt);    // PID + start time = identity, PID-reuse guard (log #2)

public sealed record RunResumed(         // after AnswerProvided: new process, same session (log #5)
    Guid Id,
    int ProcessId,
    DateTimeOffset ResumedAt);

public sealed record VerificationPassed(Guid Id, DateTimeOffset PassedAt);
public sealed record VerificationFailed(Guid Id, IReadOnlyList<string> FailedGates, DateTimeOffset FailedAt);
public sealed record PullRequestOpened(Guid Id, string PullRequestUrl, int PullRequestNumber, DateTimeOffset OpenedAt);

public sealed record TokensRecorded(     // from the stream-json result event, per run (§6.4)
    Guid Id,
    long InputTokens,
    long OutputTokens,
    decimal? CostUsd,
    DateTimeOffset RecordedAt);

public sealed record RunCompleted(Guid Id, DateTimeOffset CompletedAt);
public sealed record RunFailed(Guid Id, string Reason, DateTimeOffset FailedAt);
public sealed record RunKilled(Guid Id, KillReason Reason, Guid? KilledByOwnerId, DateTimeOffset KilledAt);
public sealed record RunSuperseded(Guid Id, int SupersededByGeneration, DateTimeOffset SupersededAt);
```

```csharp
// Closed-vocabulary VOs (anatomy per §8). ExecutorMode is the smart-enum case — it OWNS the
// spawn-flag rules from log #1 instead of leaving them to switch statements in the daemon:
public sealed record ExecutorMode       // Subscription, ApiKey, Unknown
{
    public bool UsesBareFlag => this == ApiKey;          // --bare is API-key-only (log #1)
    public bool InjectsApiKey => this == ApiKey;
}

public sealed record KillReason         // BudgetExceeded, HumanRequested, Superseded, Unknown
public sealed record RunState           // Dispatched, Running, Verifying, AwaitingReview,
                                        //   Completed, Failed, Killed, Superseded, Unknown
```

The `RunAggregate` mirrors the Task shape: sealed class, private setters, one `Apply` per event,
`RunState` derived. The transcript is **not** here — it's the run's `stream.jsonl` on disk
(log #2/#6); the stream records milestones only.

## 4. Reference aggregates (minimal v0 streams)

All event-sourced per the "event-sourcing first" principle; each is one or two events in v0.

```csharp
// Features/Owner/Events/
public sealed record OwnerRegistered(Guid Id, string Name, string? Email, DateTimeOffset RegisteredAt);

// Features/Node/Events/   (created at `h9kd install`)
public sealed record NodeRegistered(Guid Id, Guid OwnerId, string MachineName, string OperatingSystem, DateTimeOffset RegisteredAt);

// Features/Connection/Events/   (v0 has exactly one: github / gh-cli)
public sealed record ConnectionRegistered(
    Guid Id, Guid OwnerId, WorkItemProvider Provider, string ExternalAccountId,
    CredentialReference CredentialReference, DateTimeOffset RegisteredAt);

// Structured VO, not a bare string: canonical forms "gh-cli", "keychain:<name>", "env:<var>"
public sealed record CredentialReference(CredentialKind Kind, string? Identifier)
{
    public static readonly CredentialReference GhCli = new(CredentialKind.GhCli, null);
    public static CredentialReference Keychain(string name) => new(CredentialKind.Keychain, name);
    public static CredentialReference EnvironmentVariable(string name) => new(CredentialKind.EnvironmentVariable, name);
    // Parse/ToString round-trip the canonical string; CredentialKind is a closed-vocab VO per §8
}

// Features/Project/Events/
public sealed record ProjectRegistered(
    Guid Id, Guid OwnerId, Guid ConnectionId, string Name,
    string RepositoryPath,               // local clone/bare-repo path the daemon makes worktrees from
    Uri? RepositoryUrl,                  // the remote — plain git, provider-agnostic (GitHub today,
                                         // Azure DevOps et al. are a WorkItemProvider away, not a schema change)
    string BaseBranch,
    DateTimeOffset RegisteredAt);

public sealed record ProjectSettingsChanged(   // Optional<T> pattern: absent ≠ null
    Guid Id,
    Optional<IReadOnlyList<VerifyCommand>> VerifyCommands,
    Optional<bool> SkipPermissions,            // log #9: per-project opt-in
    Optional<int> MaxParallelAgents,
    Optional<IReadOnlyList<ContextLink>> ContextLinks,
    DateTimeOffset ChangedAt,
    Guid ChangedByOwnerId);

public sealed record VerifyCommand(string Name, string Command);   // "test", "dotnet test"

// Named pointers injected into every agent's context for this project: the agent follows them
// itself (via MCP, gh, or fetching). Gets "here's our Jira, figure it out" with zero connector
// work — connectors formalize specific providers later; links cover everything else forever.
public sealed record ContextLink(string Name, Uri Url);            // "jira", "wiki", "staging"
```

## 5. Projections & reads (all inline `SingleStreamProjection`, per house style)

| Document | Source stream | Serves |
|---|---|---|
| `TaskDetails` | Task | `h9k task show` |
| `TaskListItem` | Task | `h9k status`, daemon queue query (`State == Queued`) |
| `RunDetails` | Run | `h9k task show`, `h9k logs` header |
| `RunListItem` (has `TaskId`, `TaskState`-relevant fields) | Run | joined client-side by `TaskId` |

**No multi-stream projection.** `h9k task show` issues two queries (task by id, runs by
`TaskId`); `h9k status` composes display state in the query handler. Single-stream inline
projections are the house default; multi-stream waits until a read genuinely demands it.

## 6. Telemetry documents (mutable, NOT event-sourced, NOT projections)

Per log #7/#11: heartbeats and liveness are telemetry, not domain facts. Plain Marten
documents in `Documents/`, upserted in place by the daemon. They must not live on projection
documents (a projection rebuild would wipe them).

```csharp
// Features/Task/Documents/
public sealed class TaskLease
{
    public Guid Id { get; set; }              // == TaskId
    public Guid NodeId { get; set; }
    public int LeaseGeneration { get; set; }
    public DateTimeOffset HeartbeatAt { get; set; }
}

// Features/Run/Documents/
public sealed class RunActivity
{
    public Guid Id { get; set; }              // == RunId
    public DateTimeOffset LastActivityAt { get; set; }   // from tailing stream.jsonl
    public long StreamBytesRead { get; set; }            // daemon tail cursor, restart-safe
}
```

Lease expiry sweep = query `TaskLease` where `HeartbeatAt < now - timeout`. Stall detection =
`RunActivity.LastActivityAt < now - 1h` for live runs (log #11).

## 7. Command flow — two doors, one decider

The conventional Wolverine arrangement routes every mutation through an `[AggregateHandler]`.
Hall9k's decision #8 makes the CLI a thin writer with **no Wolverine host**, so:

- **Daemon-side mutations** (claim, dispatch, verification, terminal states) use house-style
  `[AggregateHandler]` static handlers under full Wolverine — identical to NTS.
- **CLI-side mutations** (`task add`, `ask`, `answer`, `project add`, registrations) call the
  same static decider logic directly and append the returned event(s) with a lightweight
  Marten session + raw `NOTIFY`. To keep one source of truth, validation lives in the static
  decider methods (e.g. `TaskDecider.Add(...)`, `TaskDecider.Answer(...)`) that both paths
  invoke — the Wolverine handler is a thin adapter over the same decider.
- House `Domain*Exception` hierarchy carries over; the CLI maps them to exit codes + stderr
  instead of HTTP statuses.

Everything else follows the established house setup: an `AddMartenEventStore()`-style config
extension (`UseSystemTextJsonForSerialization(EnumStorage.AsString, Casing.CamelCase)`,
`UseLightweightSessions`, `IntegrateWithWolverine(UseFastEventForwarding)`), `IDomainAssemblyMarker`,
a `FakeEvent<T>` stub for DB-free projection tests, Alba + Testcontainers for integration,
xUnit + FluentAssertions.

## 8. Type discipline — value objects over primitives and enums

Standing standard for this codebase: any closed domain vocabulary is a single-file
sealed-record value object, not an enum and not bare strings. Enums are reserved for
in-process technical outcomes that are never persisted or serialized (none exist in this
draft; the first legitimate one will likely be a process-spawn result inside the daemon).

The value-object anatomy (this stack serializes with System.Text.Json throughout — no
Newtonsoft anywhere):

```csharp
[JsonConverter(typeof(TaskTypeJsonConverter))]
public sealed record TaskType
{
    public static readonly TaskType Feature  = new("Feature");
    public static readonly TaskType Bugfix   = new("Bugfix");
    public static readonly TaskType Refactor = new("Refactor");
    public static readonly TaskType Chore    = new("Chore");
    public static readonly TaskType Research = new("Research");
    /// <summary>Not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly TaskType Unknown  = new("");

    public string Value { get; }
    private TaskType(string value) => Value = value;   // private ctor preserves the closed set

    public static implicit operator string(TaskType? type) => type?.Value ?? string.Empty;
    public static implicit operator TaskType(string? value) => value.IsBlank() ? Unknown : new TaskType(value);

    public bool Equals(TaskType? other) => other is not null && Value == other.Value;
    public bool Equals(string? other) => other is not null && Value == other;
    public override int GetHashCode() => Value.GetHashCode();

    private sealed class TaskTypeJsonConverter : JsonConverter<TaskType> { /* bare string in/out */ }
}
```

Rules carried over:
- **Non-enforcing by design**: the implicit conversion accepts any string; the guarantee is
  "the set is defined once," not "invalid values are unrepresentable." Old/new payloads
  round-trip as themselves (event-stream-safe across versions).
- VOs are declared **directly on events and aggregates**, initialized to the `Unknown`
  sentinel; they serialize as the bare string, so streams and projections stay readable JSONB.
- Events reference other aggregates **by ID only**, never object graphs.
- **Query discipline**: convert the VO to `string` *before* a Marten LINQ predicate
  (`string queued = TaskState.Queued; … Where(t => t.State == queued)`) — LINQ providers do
  not reliably invoke implicit conversions; whether Marten's handles the boxed VO is to be
  verified in a spike, and until proven the convert-first rule is mandatory.
- **Identity naming**: Marten-native `Guid Id` (PascalCase), on aggregates, events, and
  projections alike.
- **Acronyms are spelled out in type, method, and property names** (`PullRequestOpened`, not
  `PrOpened`); ubiquitous ones (`Api`, `Url`, `Id`) stay. Method *parameters* may abbreviate
  where the parameter's type already carries the meaning.

## 9. Open items folded forward to task 3/4

- Exact Marten schema bootstrap for the CLI (AutoCreate.None after `h9kd install` runs migrations).
- Where `Optional<T>` and shared value objects land (`Hall9k.Domain/Shared/` vs `Hall9k.Contracts`).
- Package versions: Marten 8.17.0, WolverineFx(.Marten) 5.9.2, UUIDNext 4.2.3 — pinned once, centrally (Directory.Packages.props).

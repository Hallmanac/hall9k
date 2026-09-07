using System.Text.Json.Nodes;
using Hall9k.Connectors.Credentials;
using Hall9k.Connectors.Processes;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// One attempt to put an identity in an item's assignee field: it wrote, or it did not and this is
/// the sentence the tracker answered with. Returned rather than thrown for the reason
/// <see cref="TrackerAssigneeRead"/> is — a take has several outcomes and only some of them are
/// failures, and the caller composing one refusal out of them needs the tracker's own words.
/// </summary>
public sealed record TrackerAssignmentWrite(string? Error)
{
    /// <summary>
    /// The tracker accepted the write. Whether the item actually carries it is the read-back's
    /// question. Named argument because a bare <c>new(null)</c> is ambiguous against a record's own
    /// generated copy constructor.
    /// </summary>
    public static TrackerAssignmentWrite Wrote() => new(Error: null);

    /// <summary>The write did not go through, and this is why, in the tracker's own words.</summary>
    public static TrackerAssignmentWrite Refused(string error) => new(error);
}

/// <summary>
/// What one <c>h9k task assign --take</c> concluded about the linked item (idea 64c75e43,
/// Decisions Log #143). An in-process outcome, never persisted as itself (AGENTS.md: enums only
/// for unpersisted in-process outcomes) — what lands in Postgres is
/// <see cref="Domain.Features.Tasks.Events.TrackerAssignmentWritten"/> for
/// <see cref="Taken"/> and <see cref="Domain.Features.Tasks.Events.TrackerAssignmentObserved"/>
/// for <see cref="AlreadyMine"/>.
/// </summary>
public enum TrackerTakeVerdict
{
    /// <summary>Nothing to take: the project's gate is off, or this task carries no gated reference.</summary>
    NotGated,

    /// <summary>The item was unassigned, this install wrote its own identity into it, and the read-back showed it holding.</summary>
    Taken,

    /// <summary>The tracker already showed the item assigned to this install, so nothing was written.</summary>
    AlreadyMine,

    /// <summary>Somebody else holds the item. Nothing was written, and no flag exists that would have.</summary>
    HeldByOther,

    /// <summary>The tracker could not be read, so nothing was written: a take that cannot see who holds an item cannot know it is taking it from nobody.</summary>
    Unreadable,

    /// <summary>The tracker refused the write itself, and said why.</summary>
    WriteRefused,

    /// <summary>The write was accepted but the read-back afterwards did not show this install holding the item.</summary>
    Unconfirmed,

    /// <summary>
    /// The write landed and the read-back showed this install holding the item — <em>alongside
    /// somebody else</em>, on an item the read a moment earlier said nobody held. Two installs took
    /// the same item at once, so neither can say it took it from nobody, and nothing was recorded.
    /// </summary>
    Contested,
}

/// <summary>
/// One take's whole answer, and the one place its sentences are written — the
/// <see cref="TrackerClaimDecision"/> of the write side (idea 64c75e43). <see cref="Decision"/> is
/// the fresh read the take rested on, kept whole so a caller that decides not to take (an
/// interactive offer declined) can still warn with the gate's own wording rather than a second
/// reading of the same answer.
/// </summary>
/// <param name="Verdict">What the take concluded.</param>
/// <param name="Decision">The fresh gate read this take was decided from.</param>
/// <param name="Assignee">
/// Who the item's assignee field held when it was read back — the evidence
/// <c>TrackerAssignmentWritten</c> records for <see cref="TrackerTakeVerdict.Taken"/>, and the
/// matching holder <c>TrackerAssignmentObserved</c> records for
/// <see cref="TrackerTakeVerdict.AlreadyMine"/>. Null on every other verdict, since there is
/// nothing observed to name — including <see cref="TrackerTakeVerdict.Contested"/>, where the
/// read-back did name this install but named somebody else beside it, which is a fact no event on
/// this task's stream is allowed to be composed from.
/// </param>
/// <param name="ObservedAt">When this install read the item back, which is the moment the assignment was actually seen. Never the tracker's own time: the assignee field carries none.</param>
/// <param name="Error">
/// The tracker's own sentence, verbatim, for a write it refused or a read-back that failed — and,
/// for <see cref="TrackerTakeVerdict.Contested"/>, whoever the read-back named beside this install,
/// which is that verdict's whole evidence and the one thing its refusal has to say out loud.
/// </param>
public sealed record TrackerTake(
    TrackerTakeVerdict Verdict,
    TrackerClaimDecision Decision,
    TrackerAssignee? Assignee,
    DateTimeOffset ObservedAt,
    string? Error)
{
    /// <summary>Whether the assignment may proceed. Only a take that ended with the item in this install's hands passes.</summary>
    public bool Passes => Verdict
        is TrackerTakeVerdict.NotGated or TrackerTakeVerdict.Taken or TrackerTakeVerdict.AlreadyMine;

    /// <summary>Whether the tracker was actually written to, which is what decides which event the task's stream carries.</summary>
    public bool Wrote => Verdict == TrackerTakeVerdict.Taken;

    /// <summary>
    /// Why the take refused, in the voice every other claim-gate door refuses in — and, for the
    /// two cases where a human might reach for one, that no flag exists that would have taken the
    /// item anyway. Empty on a verdict that did not refuse.
    /// <para>
    /// The held case deliberately does not reuse <see cref="TrackerClaimDecision.RefusalLine"/>:
    /// that sentence's lever is "assign the item to yourself", which is exactly what this command
    /// has just declined to do on the human's behalf, so repeating it would read as an instruction
    /// to do by hand the thing the platform refuses to do at all. The lever here is the holder.
    /// </para>
    /// </summary>
    public string RefusalLine => Verdict switch
    {
        TrackerTakeVerdict.HeldByOther =>
            $"{Decision.Tracker} shows {Decision.Item} assigned to {Decision.Holder}, so nothing was "
            + $"written and this task is unchanged. --take only ever moves an item from unassigned to "
            + $"this install{IdentityClause}, and never takes one from another person — there is no flag "
            + "that does. Ask them to unassign themselves, or take a different task; the moment the "
            + "tracker shows it unassigned, --take can have it.",
        TrackerTakeVerdict.Unreadable =>
            $"{Decision.Tracker} could not be read, so nothing was written and this task is unchanged: a "
            + "take that cannot see who holds an item cannot know it is taking it from nobody. "
            + $"{Decision.Tracker} reported: {Decision.Error} {Decision.Lever}",
        // Passive, and deliberately: this verdict covers the tracker refusing the write and it
        // covers this install failing to compose one, and "{Tracker} refused" would assert the
        // first about the second — a guessed provenance (AGENTS.md), in the one sentence a human
        // reads to decide where to go and fix it. Nothing is lost by not naming the tracker here,
        // because every Error this carries already names whoever actually refused, in their own
        // words (self-review, round two).
        TrackerTakeVerdict.WriteRefused =>
            $"{Decision.Item} could not be assigned to this install{IdentityClause}, so this task is "
            + $"unchanged: {Error}",
        TrackerTakeVerdict.Unconfirmed =>
            $"{Decision.Tracker} accepted the assignment of {Decision.Item} to this install"
            + $"{IdentityClause}, but reading the item back afterwards did not show it holding, so "
            + "nothing was recorded and this task is unchanged — the write may well have landed. "
            + $"{Decision.Tracker} answered: {Error} Look at the item, and run the same command again: "
            + "if the assignment did land, the next run sees it as already yours and simply proceeds.",
        // Deliberately does not end with "run it again", the way Unconfirmed above does: the item
        // now carries two assignments, an item assigned to several people passes the gate for each
        // of them (Decisions Log #142), so a second run would read AlreadyMine and claim — which is
        // the outcome this verdict exists to stop. The lever is the other person, not the command.
        TrackerTakeVerdict.Contested =>
            $"{Decision.Tracker} showed {Decision.Item} assigned to nobody, so this install wrote its "
            + $"own identity{IdentityClause} into it, and reading the item straight back afterwards "
            + $"showed {Error} on it as well. Somebody was put there between those two reads by "
            + "something this install cannot see — another install taking the same item in the same "
            + "moment above all — so nothing was recorded and this task is unchanged: a take that "
            + "cannot say it took an item from nobody does not claim it. This install's own assignment "
            + "is still on the item, because nothing here ever takes an assignment off one, so settle "
            + "with them who is doing the work and have whoever is not unassign themselves in "
            + $"{Decision.Tracker}. Do not simply run this again without looking: {Decision.Tracker} "
            + "now shows the item assigned to both of you, and an item assigned to several people "
            + "passes this gate for every one of them, so the next run would claim it. If the name "
            + "above is your own board's automation rather than a person, running it again is the way "
            + "through — knowing which it is, is the part only you can do.",
        _ => string.Empty,
    };

    /// <summary>
    /// What a take that succeeded says out loud: what the tracker shows now, and that this is what
    /// makes the gate pass. Empty on a verdict that took nothing.
    /// <para>
    /// It says the gate no longer holds this task, and deliberately not that the task will now
    /// dispatch: a take runs on a task with unmet dependencies too, and the assign door prints its
    /// own yellow "blocked on N dependency(ies) that have not closed out" line immediately above
    /// this one — two adjacent sentences flatly contradicting each other about whether the task is
    /// about to run (independent pre-PR review, cycle 1, adversarial lens). The gate is the only
    /// thing this class read and the only thing it can speak for; what else the queue is waiting on
    /// belongs to the door that knows.
    /// </para>
    /// </summary>
    public string TookLine => Verdict switch
    {
        TrackerTakeVerdict.Taken =>
            $"{Decision.Tracker} now shows {Decision.Item} assigned to {ObservedHolder} — read back "
            + "after the write, not assumed. This project's claim gate is tracker-assignee, so it "
            + "passes on its own from here and no longer holds this task.",
        TrackerTakeVerdict.AlreadyMine =>
            $"{Decision.Tracker} already showed {Decision.Item} assigned to {ObservedHolder}, so nothing was "
            + "written. This project's claim gate is tracker-assignee, so it passes on its own and does "
            + "not hold this task.",
        _ => string.Empty,
    };

    /// <summary>
    /// Who the read-back named, falling back to the identity the write used when the tracker
    /// offered no name. Not called <c>Holder</c>: <see cref="TrackerClaimDecision.Holder"/> is the
    /// person the gate's own read found <em>before</em> anything was written, and the two appear in
    /// adjacent sentences below — one word for both would leave a reader unable to tell which
    /// moment a sentence is about (self-review, round two).
    /// </summary>
    private string ObservedHolder =>
        Assignee?.Described ?? (Decision.Identity is { Length: > 0 } identity
            ? TrackerAssignee.Rendered(identity)
            : "this install");

    /// <summary>
    /// Who this install is, named only when the tracker actually said — borrowed from the read this
    /// take rested on rather than composed again here, so the never-guess rule (AGENTS.md) and its
    /// rendering live in exactly one place for both sides of the feature.
    /// </summary>
    private string IdentityClause => Decision.IdentityClause;

    /// <summary>
    /// A read that concluded before any write was attempted, carried through as the take's own
    /// verdict. Never handed an <see cref="TrackerClaimVerdict.Unassigned"/> decision:
    /// <see cref="TrackerAssignmentTake.TakeAsync"/> is the only caller and that verdict is the one
    /// it goes on to write for, so the default below is <see cref="TrackerClaimVerdict.NotGated"/>
    /// alone — see the fail-closed guard there for why that distinction is load-bearing rather than
    /// bookkeeping.
    /// </summary>
    internal static TrackerTake From(TrackerClaimDecision decision) => new(
        decision.Verdict switch
        {
            TrackerClaimVerdict.Assigned => TrackerTakeVerdict.AlreadyMine,
            TrackerClaimVerdict.HeldByOther => TrackerTakeVerdict.HeldByOther,
            TrackerClaimVerdict.Unreadable => TrackerTakeVerdict.Unreadable,
            _ => TrackerTakeVerdict.NotGated,
        },
        decision,
        decision.Assignee,
        decision.ObservedAt,
        decision.Error);

    internal static TrackerTake Taken(
        TrackerClaimDecision decision, TrackerAssignee assignee, DateTimeOffset observedAt) =>
        new(TrackerTakeVerdict.Taken, decision, assignee, observedAt, null);

    internal static TrackerTake WriteRefused(TrackerClaimDecision decision, string error) =>
        new(TrackerTakeVerdict.WriteRefused, decision, null, decision.ObservedAt, error);

    internal static TrackerTake Unconfirmed(TrackerClaimDecision decision, string error, DateTimeOffset observedAt) =>
        new(TrackerTakeVerdict.Unconfirmed, decision, null, observedAt, error);

    /// <summary>
    /// The write landed on an item somebody else landed on too. <paramref name="others"/> is who the
    /// read-back named beside this install, already rendered for a terminal, and it is carried as the
    /// take's <c>Error</c> rather than its <c>Assignee</c> on purpose: the assignee field is what an
    /// event is composed from, and a contested read-back is precisely the observation this feature
    /// refuses to record.
    /// </summary>
    internal static TrackerTake Contested(
        TrackerClaimDecision decision, string others, DateTimeOffset observedAt) =>
        new(TrackerTakeVerdict.Contested, decision, null, observedAt, others);
}

/// <summary>
/// The write behind <c>h9k task assign --take</c> (idea 64c75e43, Decisions Log #143): claiming
/// stops being a two-place act, because one command moves the tracker and the board together and
/// the gate then passes on its own.
/// <para>
/// It is a separate class from <see cref="TrackerClaimGate"/> on purpose, and that separation is
/// load-bearing rather than tidiness: the gate's whole contract, stated in its own doc comment and
/// relied on by Decisions Log #142, is that <b>nothing in it writes to the tracker</b> — every
/// claim door, the dispatcher's included, calls it on a cadence, and a gate that could write would
/// mean an unattended sweep assigning cards. So the read lives there and the write lives here, and
/// this class calls that one for the read rather than reading the tracker a second way.
/// </para>
/// <para>
/// <b>It only ever moves an item from unassigned to this install.</b> An item somebody else holds
/// is refused and nothing is written; there is no flag that takes one from another person, which is
/// also what narrows the race the idea names — a held item is never overwritten. What is left of
/// that race after the refusal is the window between the read and the write, and the read-back is
/// what closes it: an item that was unassigned a moment ago and now names somebody else beside this
/// install is two installs taking it at once, refused rather than claimed
/// (<see cref="TrackerTakeVerdict.Contested"/>). And the write is
/// always a <em>field update</em>, never a transition: the item's status is untouched, the same
/// standing rule <see cref="JiraWriteExecutor"/> and
/// <see cref="GitHubWorkItemProvider.CommentAsync"/> already hold to, because which state an item
/// belongs in is a team's own workflow rather than a fact about software.
/// </para>
/// <para>
/// <b>Read, write, read.</b> The read that decides is fresh at the moment of the take rather than
/// carried over from an earlier one, because the whole premise is that a stale copy is what would
/// let two installs both claim — so an interactive offer reads once to know whether it is worth
/// offering, and <see cref="TakeAsync"/> reads again after the human answers, which is also what
/// catches a teammate who took the item while the prompt was on screen. The read afterwards is
/// what a recorded event is composed from: a write that reported success and an item that actually
/// carries the assignment are two different facts (AGENTS.md, never guess at unobserved facts).
/// </para>
/// </summary>
public sealed class TrackerAssignmentTake(
    ProcessRunner? processRunner = null, JiraRequester? requester = null, CredentialVault? vault = null)
{
    private readonly ProcessRunner processRunner = processRunner ?? ExternalProcess.Runner;
    private readonly TrackerClaimGate claimGate = new(processRunner, requester, vault);

    /// <summary>
    /// The read on its own, for a caller deciding whether a take is worth offering at all — an
    /// interactive <c>h9k task assign</c> with no <c>--take</c>, which offers only on an item the
    /// tracker shows assigned to nobody. Exactly <see cref="TrackerClaimGate.CheckAsync"/>, reached
    /// through this class so a caller needs one seam rather than two.
    /// </summary>
    public Task<TrackerClaimDecision> ReadAsync(
        IDocumentStore store,
        ClaimGate gate,
        ExternalReference? reference,
        string workingDirectory,
        CancellationToken cancellationToken) =>
        claimGate.CheckAsync(store, gate, reference, workingDirectory, cancellationToken);

    /// <summary>
    /// Read the linked item fresh, write this install's own tracker identity into its assignee
    /// field if and only if the item has none, read it back, and report which of those happened.
    /// </summary>
    /// <param name="store">The store, rather than a caller's session, for <see cref="TrackerClaimGate.CheckAsync"/>'s own reason: recording this install's Jira identity on first use has to commit even when the take ends in a refusal that saves nothing.</param>
    /// <param name="gate">The project's setting. <see cref="ClaimGate.Off"/> answers <see cref="TrackerTakeVerdict.NotGated"/> without a single call.</param>
    /// <param name="reference">The task's linked item, or null when it has none.</param>
    /// <param name="workingDirectory">Where <c>gh</c> runs — the project's own repository path, since gh reads the repository from the directory it runs in.</param>
    public async Task<TrackerTake> TakeAsync(
        IDocumentStore store,
        ClaimGate gate,
        ExternalReference? reference,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        TrackerClaimDecision decision = await claimGate.CheckAsync(
            store, gate, reference, workingDirectory, cancellationToken);

        // Every verdict other than Unassigned is already the take's own answer: assigned to this
        // install is nothing to write, assigned to somebody else is refused outright, and a read
        // that failed cannot license a write at all.
        if (decision.Verdict != TrackerClaimVerdict.Unassigned)
        {
            return TrackerTake.From(decision);
        }

        // Unassigned is only ever produced for a gated reference whose identity read succeeded, so
        // this is where that guarantee is spent rather than asserted with a '!'. It refuses rather
        // than falling through to From(), which would map an Unassigned decision onto NotGated —
        // a verdict that *passes*, so a caller would assign the task with nothing written and
        // nothing said (self-review, this session): unreachable today, and the one shape of bug
        // this whole feature exists to prevent, so it fails closed instead of silently open.
        if (reference is not { } item || decision.Identity is not { } identity)
        {
            return TrackerTake.WriteRefused(
                decision,
                "the tracker answered that nobody holds the item, but this install could not tell "
                + "which item was asked about or which identity to write, so it wrote nothing rather "
                + "than guess. Nothing on this install ends that on its own — it is a defect, not a "
                + "state: report it with the command you ran.");
        }

        return item.Provider == WorkItemProvider.Jira
            ? await TakeJiraAsync(store, item, identity, decision, cancellationToken)
            : await TakeGitHubAsync(item, identity, decision, workingDirectory, cancellationToken);
    }

    /// <summary>
    /// Jira: an ordinary update carrying one field, through the same
    /// <see cref="JiraWriteExecutor"/> every other Jira write on this platform reaches Jira
    /// through, so the transport, the API version, and the classification of a failure into a
    /// rejected credential or a real refusal are the identical ones (Decisions Log #102, #114)
    /// rather than a second transport of its own.
    /// <c>assignee</c> sits outside <see cref="JiraWritePayload"/>'s forbidden-field list, which is
    /// the whole point of that list being about workflow fields: putting a person's name on a card
    /// is not moving it through anyone's states. <see cref="JiraWritePayload.Validate"/> is called
    /// here rather than trusted, because the executor deliberately refuses nothing about a payload
    /// itself.
    /// <para>
    /// What it deliberately does <em>not</em> go through is <see cref="JiraWriteCoordinator"/>, so
    /// this write leaves no <c>JiraWriteRequested</c>/<c>JiraWriteSucceeded</c>/
    /// <c>JiraWriteFailed</c> trail — an earlier draft of this comment said "audited … by the
    /// identical rules", which was never true of the half of that word the coordinator owns
    /// (independent pre-PR review, cycle 1, adversarial lens). The bypass is the design (Decisions
    /// Log #143): the coordinator records an intent before the call and leaves a write it could not
    /// finish pending for the daemon's own retry sweep, which is the opposite of what a take needs
    /// — a take answers now, in front of the human who typed the command, and a refusal has to
    /// leave the task untouched rather than queue a write that lands after they have gone. What the
    /// take records instead is on the task's own stream and only ever from the read-back:
    /// <see cref="Domain.Features.Tasks.Events.TrackerAssignmentWritten"/> once the item is
    /// confirmed held, and nothing at all otherwise — a refused or unconfirmed take leaves no
    /// record of the attempt anywhere but the sentence it printed, which is the honest reading of
    /// "nothing observed" and also why that sentence says the write may well have landed.
    /// </para>
    /// <para>
    /// The field's value is the nested <c>{"accountId": …}</c> object Jira's own assignee field
    /// takes, carried as the field's raw JSON text — which is exactly how a composed payload
    /// carries a typed field, so <c>AppendFields</c> embeds it as the object it is rather than as a
    /// quoted string (<see cref="JiraWritePayload.FromJson"/>'s own doc explains the shape). Never
    /// the email that authenticates and never the display name: <c>accountId</c> is the only thing
    /// the field carries.
    /// </para>
    /// </summary>
    private async Task<TrackerTake> TakeJiraAsync(
        IDocumentStore store,
        ExternalReference item,
        string identity,
        TrackerClaimDecision decision,
        CancellationToken cancellationToken)
    {
        if (!JiraIssueKey.TryParseBareKey(item.Key, out JiraIssueKey key))
        {
            // Unreachable through the gate, which refuses an unparseable key as Unreadable before a
            // verdict of Unassigned is ever possible — kept because the alternative is a '!' on a
            // parse this method did not perform, and a refusal costs nothing.
            return TrackerTake.WriteRefused(
                decision,
                $"'{Text.RelayedText.OneLine(item.ToString())}' does not read as a Jira key, so there is "
                + "no card whose assignee could be written.");
        }

        JiraWorkItemProvider provider;
        JiraWriteExecutor executor;
        try
        {
            await using IQuerySession query = store.QuerySession();
            ConnectionDetails? connection = await WorkItemConnections.FindJiraConnectionAsync(
                query, cancellationToken);
            if (connection is null)
            {
                return TrackerTake.WriteRefused(decision, WorkItemConnections.NoJiraConnection);
            }

            JiraAccount account = WorkItemConnections.Account(connection, vault);
            provider = new JiraWorkItemProvider(account, requester);
            executor = new JiraWriteExecutor(account, requester);
        }
        catch (DomainException exception)
        {
            // Two Jira connections with nothing saying which, or one with no site recorded — this
            // install's own configuration, answered by the refusal WorkItemConnections wrote for it.
            return TrackerTake.WriteRefused(decision, exception.Message);
        }

        JiraWritePayload payload = new(
            null,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["assignee"] = new JsonObject { ["accountId"] = identity }.ToJsonString(),
            },
            null);

        string? unverifiedWrite = null;
        try
        {
            payload.Validate(JiraWriteOperation.Update);
            await executor.UpdateAsync(key.Value, payload, cancellationToken);
        }
        // The assignee PUT itself answered 2xx and only the executor's own read-back after it
        // failed. Its message says so in as many words — "the update call itself already
        // succeeded, so do not record this as a refusal of the write" — so classifying it
        // WriteRefused would contradict both that sentence and this verdict's own documented
        // meaning, and would print "could not be assigned" about a write that very likely landed
        // (independent pre-PR review, cycle 1, adversarial lens). That read-back only ever
        // re-confirms the card exists, which is not the fact a take rests on anyway: the assignee
        // read below is, so the take goes on and asks it, carrying Jira's sentence along in case
        // that one cannot settle it either.
        catch (JiraWriteExecutionException exception) when (exception.WriteAlreadyRan)
        {
            unverifiedWrite = exception.Message;
        }
        catch (JiraWriteExecutionException exception)
        {
            return TrackerTake.WriteRefused(decision, exception.Message);
        }
        catch (DomainException exception)
        {
            return TrackerTake.WriteRefused(decision, exception.Message);
        }

        TrackerAssigneeRead read = await provider.ReadAssigneeAsync(key, cancellationToken);
        return Confirm(decision, read, identity, DateTimeOffset.UtcNow, unverifiedWrite);
    }

    /// <summary>
    /// GitHub: <c>gh issue edit --add-assignee</c> with the login read live on this very check —
    /// never a stored one, for the reason <see cref="TrackerClaimGate"/>'s own GitHub path
    /// documents, and here with a sharper edge: a stale login would put somebody else's name on the
    /// issue.
    /// </summary>
    private async Task<TrackerTake> TakeGitHubAsync(
        ExternalReference item,
        string identity,
        TrackerClaimDecision decision,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        GitHubWorkItemProvider issues = new(processRunner);
        TrackerAssignmentWrite write = await issues.AddAssigneeAsync(
            item, identity, workingDirectory, cancellationToken);
        if (write.Error is { } error)
        {
            return TrackerTake.WriteRefused(decision, error);
        }

        TrackerAssigneeRead read = await issues.ReadAssigneesAsync(item, workingDirectory, cancellationToken);
        return Confirm(decision, read, identity, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The read-back, turned into the take's verdict — the one place both providers' write paths
    /// agree about what counts as having taken an item, so neither can disagree with the other (and
    /// neither can disagree with <see cref="TrackerClaimGate"/>'s own matching, which compares the
    /// same way through the same <see cref="TrackerAssigneeRead.IdentityComparison"/>).
    /// <para>
    /// A read-back that failed, and one that came back naming nobody or naming somebody else
    /// <em>instead</em>, are the same verdict deliberately: in both cases this install cannot say
    /// the item is in its hands, and the honest thing is to record nothing and leave the task
    /// alone. The refusal says the write may well have landed, because it may well have —
    /// <see cref="TrackerTake.RefusalLine"/> carries that, along with the fact that running the
    /// command again is safe.
    /// </para>
    /// <para>
    /// A read-back naming this install <em>and somebody else</em> is its own verdict,
    /// <see cref="TrackerTakeVerdict.Contested"/>, and it is where the race this whole feature
    /// narrows can still show through. The read that licensed the write said nobody held the item,
    /// so anybody on it now arrived between that read and this one. Jira cannot produce that — the
    /// assignee field holds one person and the <c>PUT</c> replaces whoever was there — but GitHub's
    /// <c>--add-assignee</c> <em>adds</em>, so two installs taking the same issue in the same moment
    /// both write successfully and both find themselves among the assignees; treating that as
    /// <see cref="TrackerTakeVerdict.Taken"/> would let both claim the card silently and
    /// permanently, since being among several assignees passes the gate on every later read
    /// (Decisions Log #142). Refusing it leaves at most one winner, because the second read-back to
    /// run always sees both logins (independent pre-PR review, cycle 1, adversarial lens).
    /// </para>
    /// </summary>
    /// <param name="unverifiedWrite">
    /// Jira's own sentence when the write went through and only <see cref="JiraWriteExecutor"/>'s
    /// existence read-back after it failed — carried into a refusal this read-back cannot resolve,
    /// so the human sees both halves. Null on every other path, including GitHub's, which has no
    /// second read-back of its own.
    /// </param>
    private static TrackerTake Confirm(
        TrackerClaimDecision decision,
        TrackerAssigneeRead read,
        string identity,
        DateTimeOffset observedAt,
        string? unverifiedWrite = null)
    {
        string carried = unverifiedWrite is { } unverified ? unverified + " " : string.Empty;
        if (read.Error is { } error)
        {
            return TrackerTake.Unconfirmed(decision, carried + error, observedAt);
        }

        if (read.Assignees.FirstOrDefault(assignee =>
            string.Equals(assignee.Identity, identity, read.IdentityComparison)) is not { } match)
        {
            return TrackerTake.Unconfirmed(
                decision,
                carried + (read.Assignees.Count == 0
                    ? "it shows the item assigned to nobody."
                    : $"it shows the item assigned to {read.DescribeHolders()}."),
                observedAt);
        }

        IReadOnlyList<TrackerAssignee> others = [.. read.Assignees.Where(assignee =>
            !string.Equals(assignee.Identity, identity, read.IdentityComparison))];
        return others.Count == 0
            ? TrackerTake.Taken(decision, match, observedAt)
            : TrackerTake.Contested(
                decision, string.Join(", ", others.Select(assignee => assignee.Described)), observedAt);
    }
}

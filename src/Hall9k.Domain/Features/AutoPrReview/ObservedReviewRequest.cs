using Hall9k.Domain.Features.Project;

namespace Hall9k.Domain.Features.AutoPrReview;

/// <summary>
/// One review request GitHub has made of this install's own login, and what one decider — one
/// node sweeping one project — did about it, recorded whatever that project's own setting says
/// (Decisions Log #161): the record that makes this feature's state visible instead of silent.
/// One row per pull request in the ordinary case, since one project on one node is what observes
/// it; the decider is part of <see cref="ComputeId"/> because the outcome is decided from its own
/// recorded facts. Mutable telemetry, not an event (the
/// <c>TaskLease</c>/<c>RunActivity</c> convention, Decisions Log #7): it is a live view of a
/// standing request GitHub is still reporting, kept only while the request stands, and the
/// durable history of what was decided lives where it always has — on the minted task's own
/// stream (<c>PullRequestReviewAssignmentObserved</c>).
/// <para>
/// Written by <c>AutoPrReviewEngine</c>'s sweep and read by <c>h9k status</c>, which renders one
/// row per row here: a needs-you row where nothing started and the operator has to act, an
/// informational one where the daemon is already doing the work. The row is deleted the moment
/// GitHub stops reporting the request — a withdrawal, a merge, a close, or a submitted review
/// all end it — which is why absence from the search is enough to drop this row while it is
/// deliberately not enough to conclude a task (<c>ConcludeOneAsync</c> demands a timeline
/// removal event before it abandons anything). Dropping a row costs a re-record and one more
/// log line if the request comes back; abandoning a task on the same evidence would throw work
/// away.
/// </para>
/// </summary>
public sealed class ObservedReviewRequest
{
    /// <summary>
    /// <see cref="ComputeId"/> — one row per decider per request: the node and the project whose
    /// own recorded facts graded it, the pull request, and the login the review was requested of.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// The node whose sweep graded this request — a node is an install (PLAN.md §6.1), and its
    /// own <see cref="AutoPrReviewDefaultAdoption"/> moment is half of the cutoff
    /// <see cref="Outcome"/> was decided against, which is why it is part of
    /// <see cref="ComputeId"/>.
    /// </summary>
    public Guid ObservingNodeId { get; set; }

    /// <summary>
    /// The project whose own setting and registration <see cref="Outcome"/> was decided from —
    /// the other half of that decision, and so also part of <see cref="ComputeId"/>.
    /// </summary>
    public Guid ProjectId { get; set; }

    /// <summary><c>owner/repo</c> as the observing project's own recorded URL spells it.</summary>
    public string Repository { get; set; } = string.Empty;

    public int Number { get; set; }

    /// <summary>GitHub's own URL for the pull request, as the search reported it.</summary>
    public string PullRequestUrl { get; set; } = string.Empty;

    /// <summary>The login <c>gh</c> was authenticated as when this request was observed — read fresh, never cached.</summary>
    public string ReviewerLogin { get; set; } = string.Empty;

    /// <summary>Who requested the review, or null when GitHub's own timeline could not attribute it.</summary>
    public string? RequesterLogin { get; set; }

    /// <summary>
    /// GitHub's own requested-at time, not this install's poll time — the fact the no-backfill
    /// guard compares against. Null when the timeline read could not produce one, which is
    /// recorded as honestly unknown rather than filled in with the observation time (AGENTS.md:
    /// never guess at unobserved facts) and is itself a reason nothing starts on its own.
    /// </summary>
    public DateTimeOffset? RequestedAt { get; set; }

    /// <summary>When this install first saw the request. Never rewritten while the row stands.</summary>
    public DateTimeOffset FirstObservedAt { get; set; }

    /// <summary>When this install last saw GitHub still reporting it.</summary>
    public DateTimeOffset LastObservedAt { get; set; }

    /// <summary>The project's effective speed at the moment the outcome below was decided.</summary>
    public AutoPrReviewSpeed SettingWhenObserved { get; set; } = AutoPrReviewSpeed.Off;

    /// <summary>Whether that speed was this project's own recorded choice or the platform default.</summary>
    public bool SettingWasRecorded { get; set; }

    public ReviewRequestOutcome Outcome { get; set; } = ReviewRequestOutcome.Unknown;

    /// <summary>What the outcome needs said in words to be actionable — a mint refusal's own reason, a deferral. Null when the outcome says it all.</summary>
    public string? OutcomeDetail { get; set; }

    /// <summary>The task this request produced or was already covered by; null while nothing covers it.</summary>
    public Guid? TaskId { get; set; }

    /// <summary>
    /// Keyed on the decider and the request together: the observing node and project, the pull
    /// request, and the login the review was requested of — repository and login lower-cased,
    /// because GitHub's own casing for an <c>owner/repo</c> (or for a login) is under no
    /// obligation to match the casing a project recorded in its repository URL, and two rows for
    /// one request would be two log lines and two status rows for it (the same casing hazard
    /// <c>CreateOneAsync</c>'s own dedup fast path already guards).
    /// <para>
    /// The login is part of the key because this row is about a request GitHub made of
    /// <em>one</em> login (independent pre-PR review, cycle 1, adversarial lens, medium): two
    /// installs can share one database — the reason
    /// <see cref="AutoPrReviewDefaultAdoption"/> is keyed per node — and two <c>gh</c>
    /// authentications there see two different sets of requests. Keyed on the pull request alone,
    /// a pull request requesting both logins would leave one row for two requests, each install
    /// overwriting the other's outcome and cutoff verdict every sweep, which is a log line per
    /// tick forever rather than the one per request this record exists to bound it to.
    /// </para>
    /// <para>
    /// The project and the node are part of it because the outcome is decided from their own
    /// recorded facts and nothing else tells two of them apart (independent pre-PR review, cycle
    /// 1, adversarial lens, medium): the project's own setting and registration, and this node's
    /// own adoption moment, are exactly what <c>DecideAsync</c> grades a request against. Keyed
    /// on the request alone, two projects on one install pointing at the same repository — one
    /// recorded <c>off</c>, one on the default with a later registration — grade one standing
    /// request <see cref="ReviewRequestOutcome.HeldSettingOff"/> and
    /// <see cref="ReviewRequestOutcome.HeldBeforeCutoff"/> respectively and overwrite each
    /// other's answer on the one shared row every sweep; each overwrite reads as a genuine change
    /// to <c>AutoPrReviewObservation.IsReportable</c>, so the one-Info-line-per-pull-request rule
    /// this record exists to enforce becomes two lines per tick forever, with a <c>h9k status</c>
    /// row flapping between two causes and two levers. The same collision arises on the
    /// documented two-nodes-one-database deployment when both nodes authenticate <c>gh</c> as the
    /// same login and their own adoption moments differ. Origin incident: this branch's own
    /// pre-PR review, before the row had ever shipped.
    /// </para>
    /// <para>
    /// Dedup across deciders is deliberately not this key's job. One live task per pull request
    /// is <c>CreateOneAsync</c>'s own dedup on the task's canonical external reference, which
    /// every decider's mint goes through; and two deciders' rows that say the same thing print
    /// once because <c>ReviewRequestPane</c> collapses rows that render identically, leaving two
    /// rows only where the two genuinely disagree — each naming the project its own lever needs.
    /// </para>
    /// </summary>
    public static string ComputeId(
        Guid observingNodeId, Guid projectId, string repository, int number, string reviewerLogin) =>
        $"{observingNodeId:N}-{projectId:N}-{repository.ToLowerInvariant()}#{number}@{reviewerLogin.ToLowerInvariant()}";
}

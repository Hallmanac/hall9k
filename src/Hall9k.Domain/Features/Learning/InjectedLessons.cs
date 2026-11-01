using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Rendering;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Learning;

/// <summary>
/// One lesson as a prompt actually carries it (idea d805fd8b, piece 5): the id a session cites or
/// retires it by, the claim collapsed onto one line, the scope it travels under, and the
/// provenance mark that says who recorded it and where.
/// </summary>
public sealed record InjectedLesson(Guid Id, string Statement, KnowledgeScope Scope, LessonProvenanceMark Mark)
{
    /// <summary>The eight characters <c>h9k learn show</c> and <c>h9k learn retire</c> accept, which is why the line leads with them.</summary>
    public string ShortId => DomainId.Short(Id);

    /// <summary>
    /// The markdown bullet this lesson renders as, id first. The id is a prefix rather than a
    /// trailing citation because it is the handle: a session that finds a lesson wrong retires it
    /// by that id, and one that leans on a lesson cites it by that id, so it has to be the first
    /// thing on the line rather than something to hunt for at the end of a sentence.
    /// </summary>
    public string Line => $"- [{ShortId}] {Statement} ({ScopeLabel}; {Mark.Label})";

    /// <summary>
    /// How far this lesson reaches, in words rather than as the vocabulary value: a session
    /// reading the section has to be able to tell a claim about this codebase from a habit that
    /// rode in from the owner's other work, because the second one is the one that is wrong more
    /// often here (<see cref="KnowledgeScope"/>, "Scope determines travel").
    /// </summary>
    private string ScopeLabel =>
        Scope == KnowledgeScope.Owner ? "yours across every project" : "this project";
}

/// <summary>
/// How many lessons one provenance mark held out of a section. The mark travels with the count
/// rather than the count alone, because the held marks are different claims: a lesson written on a
/// machine this node does not control, one replicated from outside this owner's fleet, one whose
/// recording node nobody observed, and one whose stream carries no provenance at all. A section
/// that reports a total and names only the first is guessing at the others, which is the one thing
/// this whole slice is not allowed
/// to do (AGENTS.md, never guess at unobserved facts; cycle-1 pre-PR review, both lenses).
/// </summary>
public sealed record HeldLessonCount(LessonProvenanceMark Mark, int Count);

/// <summary>
/// The bounded, provenance-marked slice of a project's recorded lessons that a dispatched
/// session's prompt carries, together with everything the section has to be able to say out loud
/// about what it left out (idea d805fd8b, piece 5; backlog 55).
/// <para>
/// The counts are the point as much as the lessons are. A capped feed that quietly drops the
/// overflow teaches a session that what it was handed is everything there is, which is worse than
/// handing it nothing: it would stop looking. So every lesson that did not make the section is
/// counted under the reason it did not, and the composed prose says so and names
/// <c>h9k learn list</c>.
/// </para>
/// <para>
/// <see cref="ChosenForTask"/> marks a section whose lessons were picked for the task rather than
/// taken newest first. <see cref="HeldForCap"/> then counts every eligible lesson the section does
/// not carry, which for a picked set is the choice and the caps together, so the arithmetic the
/// prose reports (shown plus not carried equals eligible) holds for both selections.
/// </para>
/// </summary>
public sealed record InjectedLessons(
    IReadOnlyList<InjectedLesson> Lessons,
    int ActiveInScope,
    IReadOnlyList<HeldLessonCount> HeldForProvenanceByMark,
    int HeldForCap,
    LessonInjectionCaps Caps,
    bool ChosenForTask = false)
{
    /// <summary>Nothing recorded, nothing held back: what a project with no lessons yields, and what every caller that has no store to read passes.</summary>
    public static readonly InjectedLessons None = new([], 0, [], 0, LessonInjectionCaps.Default);

    /// <summary>True when at least one lesson is actually being carried into the prompt.</summary>
    public bool Any => Lessons.Count > 0;

    /// <summary>How many active lessons the provenance rule held back, whatever the reason; the breakdown is <see cref="HeldForProvenanceByMark"/>.</summary>
    public int HeldForProvenance => HeldForProvenanceByMark.Sum(held => held.Count);

    /// <summary>
    /// How many lessons the provenance rule let through, which is the set the two caps were then
    /// applied to. The number the truncation sentence reconciles against, rather than
    /// <see cref="ActiveInScope"/>: shown plus held-for-cap adds up to this and never to the
    /// active inventory, so a sentence that claimed the second would leave the provenance holds
    /// unaccounted for and a reader adding the numbers would find a gap (cycle-1 pre-PR review,
    /// adversarial lens).
    /// </summary>
    public int EligibleForPrompt => Lessons.Count + HeldForCap;

    /// <summary>True when a lesson this node would otherwise have injected was dropped by one of the two caps.</summary>
    public bool TruncatedByCap => HeldForCap > 0;

    /// <summary>
    /// Whether a prompt composes the section at all. True when something was held back even
    /// though nothing is being shown, which is the case a silent omission would get wrong: a
    /// session handed no lessons and told nothing would reasonably conclude the project has none,
    /// and stop looking. A project with genuinely nothing recorded gets no section, because there
    /// is no absence to explain.
    /// </summary>
    public bool WorthComposing => Lessons.Count > 0 || HeldForCap > 0 || HeldForProvenance > 0;
}

/// <summary>
/// Every lesson the composing node's provenance rule lets into a prompt, newest first, before
/// either cap applies, together with what a section has to report about the rest. This is the one
/// list the two selections share: <see cref="NewestFirst"/> is what a prompt carries today, and the
/// lesson picker is offered exactly <see cref="Eligible"/>, so a lesson held back on provenance
/// reaches no picker prompt and a lesson the caps alone hold back is offered.
/// </summary>
/// <param name="Eligible">The lessons the provenance rule let through, newest first (recorded time, then id).</param>
/// <param name="ActiveInScope">Every active lesson in scope, whatever its provenance.</param>
/// <param name="HeldForProvenanceByMark">The lessons the provenance rule held back, counted under each mark.</param>
/// <param name="Caps">The caps in force when this was read.</param>
/// <param name="PickerMode">The lesson-picker setting in force when this was read.</param>
public sealed record LessonCandidates(
    IReadOnlyList<InjectedLesson> Eligible,
    int ActiveInScope,
    IReadOnlyList<HeldLessonCount> HeldForProvenanceByMark,
    LessonInjectionCaps Caps,
    LessonPickerMode PickerMode)
{
    /// <summary>What a read that could not be made offers: no lessons, nothing held, the default caps, and shadow.</summary>
    public static readonly LessonCandidates None = new([], 0, [], LessonInjectionCaps.Default, LessonPickerMode.Shadow);

    /// <summary>The section a prompt carries when no pick applies: the newest eligible lessons that fit both caps.</summary>
    public InjectedLessons NewestFirst => LessonInjection.SelectNewestFirst(this);

    /// <summary>
    /// The section for <paramref name="recordedPick"/>, which is the task's recorded pick or null.
    /// The picked set is used only when the pick says it may be
    /// (<see cref="Tasks.Events.LessonPickRecorded.CarriesIntoPrompts"/>, against the setting this
    /// was read under), and then only as far as it still stands: every id is checked against the
    /// lessons this node may carry right now, so a lesson since retired or held back on provenance
    /// drops out, and when none remain the section is newest-first.
    /// </summary>
    public InjectedLessons For(Tasks.Events.LessonPickRecorded? recordedPick) =>
        recordedPick is { } pick && pick.CarriesIntoPrompts(PickerMode)
            ? LessonInjection.SelectPicked(this, pick.PickedLessonIds) ?? NewestFirst
            : NewestFirst;
}

/// <summary>
/// Turns a project's and an owner's recorded lessons into the bounded section a prompt carries
/// (idea d805fd8b, piece 5; backlog 55). Pure: the store read and the node's own identity are the
/// caller's (<see cref="Queries.LessonPromptFeed"/>), so every rule below is provable with no
/// database and reads identically for the daemon's dispatch, the daemon's review passes, and an
/// interactive <c>h9k task drive</c> claim in the CLI process.
/// </summary>
public static class LessonInjection
{
    /// <summary>
    /// The eligible lessons, with everything the provenance rule held back counted under its
    /// reason, and no cap applied. The one place the provenance predicate lives: both selections
    /// below start from this list, and a second copy of it is the drift
    /// <see cref="Queries.LessonPromptFeed"/> exists to prevent.
    /// <para>
    /// <paramref name="localFleet"/> is the asking owner's fleet, and it is only consulted for a
    /// lesson that was replicated here (<see cref="LearningDetails.ReceivedFromNodeId"/> non-null):
    /// null means it could not be read, which holds those lessons rather than admitting them.
    /// </para>
    /// </summary>
    public static LessonCandidates Eligible(
        IReadOnlyList<LearningDetails> projectLessons,
        IReadOnlyList<LearningDetails> ownerLessons,
        Guid thisNodeId,
        LessonInjectionCaps caps,
        IReadOnlySet<Guid>? localFleet = null,
        LessonPickerMode? pickerMode = null)
    {
        List<LearningDetails> active =
        [
            .. projectLessons.Concat(ownerLessons)
                .Where(lesson => lesson.Status == LearningStatus.Active)
                .OrderByDescending(lesson => lesson.RecordedAt)
                .ThenBy(lesson => lesson.Id.ToString("N"), StringComparer.Ordinal),
        ];

        List<InjectedLesson> eligible = [];
        Dictionary<LessonProvenanceMark, int> heldForProvenance = [];
        foreach (LearningDetails lesson in active)
        {
            LessonProvenanceMark mark = LessonProvenanceMark.Of(
                lesson.Provenance, lesson.RecordedOnNodeId, thisNodeId, lesson.ReceivedFromNodeId, localFleet);
            if (!mark.ReachesAPrompt)
            {
                heldForProvenance[mark] = heldForProvenance.GetValueOrDefault(mark) + 1;
                continue;
            }

            eligible.Add(new InjectedLesson(
                lesson.Id, KnowledgeDocumentText.SingleLine(lesson.Statement), lesson.Scope, mark));
        }

        // Reported in the vocabulary's own order rather than in whichever order the lessons
        // happened to arrive, so the same holds read the same way in every prompt, and with the
        // empty buckets dropped so a section never names a reason it held nothing back for.
        List<HeldLessonCount> heldByMark =
        [
            .. LessonProvenanceMark.HeldFromPrompts
                .Select(mark => new HeldLessonCount(mark, heldForProvenance.GetValueOrDefault(mark)))
                .Where(held => held.Count > 0),
        ];

        return new LessonCandidates(eligible, active.Count, heldByMark, caps, pickerMode ?? LessonPickerMode.Shadow);
    }

    /// <summary>
    /// The section's lessons, in the order a prompt lists them, with everything held back counted
    /// under its reason. Newest first through <see cref="Eligible"/> and then
    /// <see cref="SelectNewestFirst"/>; the parameters are those of <see cref="Eligible"/>.
    /// </summary>
    public static InjectedLessons Compose(
        IReadOnlyList<LearningDetails> projectLessons,
        IReadOnlyList<LearningDetails> ownerLessons,
        Guid thisNodeId,
        LessonInjectionCaps caps,
        IReadOnlySet<Guid>? localFleet = null) =>
        SelectNewestFirst(Eligible(projectLessons, ownerLessons, thisNodeId, caps, localFleet));

    /// <summary>
    /// The eligible lessons newest first, bounded by both caps.
    /// <para>
    /// Newest first, which is the one place this deliberately disagrees with
    /// <see cref="LessonsDocumentRenderer"/>'s oldest-first document. A document is read whole and
    /// chronology is what makes it legible; a capped feed has to drop something, and dropping the
    /// oldest keeps what the most recent runs learned, which is also the set that has not yet had
    /// time to be retired or absorbed into a harder rule. It matches <c>h9k learn list</c>'s own
    /// ordering too, so a session following the section's pointer reads the same list continuing
    /// where the section stopped rather than starting from the other end.
    /// </para>
    /// <para>
    /// The two caps are applied to the eligible lessons together, after the provenance filter and
    /// never before it: a run of another node's lessons at the head of the list must not be able
    /// to consume the budget and starve the section of the ones that actually reach a prompt. The
    /// character budget counts the rendered <see cref="InjectedLesson.Line"/> text, which is what
    /// the prompt actually pays for, and a lesson is never truncated mid-claim: half a claim is a
    /// different claim, so a lesson that will not fit whole is held back whole and counted. Here
    /// the first lesson that does not fit ends the section, so the section is always an unbroken run
    /// of the newest lessons.
    /// </para>
    /// </summary>
    public static InjectedLessons SelectNewestFirst(LessonCandidates candidates)
    {
        List<InjectedLesson> shown = [];
        int characters = 0;
        foreach (InjectedLesson lesson in candidates.Eligible)
        {
            int cost = lesson.Line.Length;
            if (shown.Count >= candidates.Caps.MaxLessons || characters + cost > candidates.Caps.MaxCharacters)
            {
                break;
            }

            shown.Add(lesson);
            characters += cost;
        }

        return new InjectedLessons(
            shown, candidates.ActiveInScope, candidates.HeldForProvenanceByMark,
            candidates.Eligible.Count - shown.Count, candidates.Caps);
    }

    /// <summary>
    /// The eligible lessons among <paramref name="pickedIds"/>, in the order given and within both
    /// caps, or null when none of them stands. An id that is not eligible now, or repeats one already
    /// taken, is skipped. A lesson that does not fit whole is left out whole and the next one is still
    /// tried, which is where this differs from <see cref="SelectNewestFirst"/>: a pick is a set the
    /// session chose, so one long lesson must not end it. The count cap does end it, because nothing
    /// after the last slot can fit.
    /// </summary>
    public static InjectedLessons? SelectPicked(LessonCandidates candidates, IEnumerable<Guid> pickedIds)
    {
        Dictionary<Guid, InjectedLesson> eligibleById = candidates.Eligible.ToDictionary(lesson => lesson.Id);
        HashSet<Guid> considered = [];
        List<InjectedLesson> shown = [];
        int characters = 0;
        foreach (Guid id in pickedIds)
        {
            if (!eligibleById.TryGetValue(id, out InjectedLesson? lesson) || !considered.Add(id))
            {
                continue;
            }

            if (shown.Count >= candidates.Caps.MaxLessons)
            {
                break;
            }

            int cost = lesson.Line.Length;
            if (characters + cost > candidates.Caps.MaxCharacters)
            {
                continue;
            }

            shown.Add(lesson);
            characters += cost;
        }

        return shown.Count == 0
            ? null
            : new InjectedLessons(
                shown, candidates.ActiveInScope, candidates.HeldForProvenanceByMark,
                candidates.Eligible.Count - shown.Count, candidates.Caps, ChosenForTask: true);
    }
}
